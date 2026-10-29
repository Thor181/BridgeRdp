using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using BridgeRdp.Proxy;
using BridgeRdp.Protocol;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
var ct = timeout.Token;
await FileChecks.RunAsync(ct);
var toAgent = Channel.CreateBounded<byte[]>(512);
var toClient = Channel.CreateBounded<byte[]>(512);
using var clientTransport = new MemoryTransport(toClient, toAgent);
using var agentTransport = new MemoryTransport(toAgent, toClient);
using var tunnel = new TunnelClient(clientTransport);
using var socks = new SocksServer(tunnel, 0);
var agentTask = new TunnelAgent(agentTransport).RunAsync(ct);
var socksTask = socks.RunAsync();

await Check("frame limits and roundtrip", () =>
{
    byte[] payload = [0, 255, 42];
    var (header, data) = ProxyProtocol.Decode(ProxyProtocol.Encode(new Packet { Id = 7, Command = Command.Write }, payload));
    Require(header.Id == 7 && data.Span.SequenceEqual(payload), "Frame roundtrip");
    foreach (int size in new[] { -1, int.MaxValue, 4093 })
    {
        var malformed = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(malformed, size);
        Throws(() => ProxyProtocol.Decode(malformed));
        Throws(() => FrameCodec.Decode(malformed, ProtocolJson.Default.Request));
    }
    Throws(() => ProxyProtocol.Encode(new Packet(), new byte[ProxyProtocol.ChunkSize + 1]));
    return Task.CompletedTask;
});

await Check("SOCKS authentication and unsupported commands", async () =>
{
    using var rejected = await LocalAsync();
    var stream = rejected.GetStream();
    await stream.WriteAsync(new byte[] { 5, 1, 2 }, ct);
    Require((await ReadAsync(stream, 2))[1] == 255, "Unsupported auth must be rejected");
    using var socket = await LocalAsync();
    stream = socket.GetStream();
    await GreetingAsync(stream);
    await stream.WriteAsync(new byte[] { 5, 3, 0, 1 }, ct);
    Require((await ReadAsync(stream, 10))[1] == 7, "UDP ASSOCIATE must be rejected");
});

await Check("connection refused returns SOCKS status 5", async () =>
{
    var unavailable = new TcpListener(IPAddress.Loopback, 0);
    unavailable.Start();
    int port = ((IPEndPoint)unavailable.LocalEndpoint).Port;
    unavailable.Stop();
    using var socket = await LocalAsync();
    var stream = socket.GetStream();
    await GreetingAsync(stream);
    await ConnectRequestAsync(stream, "127.0.0.1", port, false);
    Require((await ReadAsync(stream, 10))[1] == 5, "Incorrect connection failure status");
});

await Check("8 concurrent binary transfers, remote DNS and TCP half-close", async () =>
{
    var target = new TcpListener(IPAddress.Loopback, 0);
    target.Start();
    try
    {
        int port = ((IPEndPoint)target.LocalEndpoint).Port;
        var echoTasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            using var remote = await target.AcceptTcpClientAsync(ct);
            var stream = remote.GetStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) != 0)
                await stream.WriteAsync(buffer.AsMemory(0, read), ct);
            // Trailer proves that EOF in one direction preserves the other direction.
            await stream.WriteAsync(new byte[] { 10, 20, 30 }, ct);
            remote.Client.Shutdown(SocketShutdown.Send);
        }).ToArray();
        var clients = Enumerable.Range(0, 8).Select(async index =>
        {
            using var socket = await LocalAsync();
            var stream = socket.GetStream();
            await GreetingAsync(stream);
            await ConnectRequestAsync(stream, index % 2 == 0 ? "localhost" : "127.0.0.1", port, index % 2 == 0);
            Require((await ReadAsync(stream, 10))[1] == 0, "SOCKS connect failed");
            var bytes = RandomNumberGenerator.GetBytes(256 * 1024 + index);
            var send = Task.Run(async () =>
            {
                await stream.WriteAsync(bytes, ct);
                socket.Client.Shutdown(SocketShutdown.Send);
            }, ct);
            var echoed = await ReadAsync(stream, bytes.Length + 3);
            Require(echoed.AsSpan(0, bytes.Length).SequenceEqual(bytes), "Binary transfer corrupted");
            Require(echoed.AsSpan(bytes.Length).SequenceEqual(new byte[] { 10, 20, 30 }), "Half-close lost response");
            Require(await stream.ReadAsync(new byte[1], ct) == 0, "Missing EOF");
            await send;
        }).ToArray();
        await Task.WhenAll(clients.Concat(echoTasks));
    }
    finally { target.Stop(); }
});

await Check("IPv6 destination", async () =>
{
    var target = new TcpListener(IPAddress.IPv6Loopback, 0);
    target.Start();
    try
    {
        var echo = Task.Run(async () =>
        {
            using var remote = await target.AcceptTcpClientAsync(ct);
            var stream = remote.GetStream();
            var bytes = await ReadAsync(stream, 3);
            await stream.WriteAsync(bytes, ct);
            remote.Client.Shutdown(SocketShutdown.Send);
            Require(await stream.ReadAsync(new byte[1], ct) == 0, "Missing client EOF");
        }, ct);
        using var socket = await LocalAsync();
        var stream = socket.GetStream();
        await GreetingAsync(stream);
        await ConnectRequestAsync(stream, "::1", ((IPEndPoint)target.LocalEndpoint).Port, false);
        Require((await ReadAsync(stream, 10))[1] == 0, "IPv6 connect failed");
        await stream.WriteAsync(new byte[] { 1, 2, 3 }, ct);
        Require((await ReadAsync(stream, 3)).SequenceEqual(new byte[] { 1, 2, 3 }), "IPv6 data corrupted");
        socket.Client.Shutdown(SocketShutdown.Send);
        Require(await stream.ReadAsync(new byte[1], ct) == 0, "Missing target EOF");
        await echo;
    }
    finally { target.Stop(); }
});

await Check("channel disconnect closes active sockets and listener", async () =>
{
    var target = new TcpListener(IPAddress.Loopback, 0);
    target.Start();
    try
    {
        using var socket = await LocalAsync();
        var accepted = target.AcceptTcpClientAsync(ct).AsTask();
        var stream = socket.GetStream();
        await GreetingAsync(stream);
        await ConnectRequestAsync(stream, "127.0.0.1", ((IPEndPoint)target.LocalEndpoint).Port, false);
        Require((await ReadAsync(stream, 10))[1] == 0, "Connect failed");
        using var remote = await accepted;
        clientTransport.Dispose();
        await Task.WhenAll(agentTask, tunnel.Completion, socksTask).WaitAsync(ct);
        Require(await remote.GetStream().ReadAsync(new byte[1], ct) == 0, "Remote socket leaked");
        Require(await stream.ReadAsync(new byte[1], ct) == 0, "Local socket leaked");
    }
    finally { target.Stop(); }
});
Console.WriteLine("All checks passed.");

async Task<TcpClient> LocalAsync()
{
    var socket = new TcpClient();
    await socket.ConnectAsync(IPAddress.Loopback, socks.Port, ct);
    return socket;
}
async Task<byte[]> ReadAsync(NetworkStream stream, int length)
{
    var bytes = new byte[length];
    await stream.ReadExactlyAsync(bytes, ct);
    return bytes;
}
async Task GreetingAsync(NetworkStream stream)
{
    // Split across TCP writes to exercise fragmented handshakes.
    await stream.WriteAsync(new byte[] { 5 }, ct);
    await stream.WriteAsync(new byte[] { 1, 0 }, ct);
    Require((await ReadAsync(stream, 2)).SequenceEqual(new byte[] { 5, 0 }), "Greeting failed");
}
async Task ConnectRequestAsync(NetworkStream stream, string host, int port, bool domain)
{
    var address = domain ? Encoding.ASCII.GetBytes(host) : IPAddress.Parse(host).GetAddressBytes();
    var bytes = new List<byte> { 5, 1, 0, domain ? (byte)3 : address.Length == 16 ? (byte)4 : (byte)1 };
    if (domain) bytes.Add((byte)address.Length);
    bytes.AddRange(address);
    bytes.Add((byte)(port >> 8));
    bytes.Add((byte)port);
    await stream.WriteAsync(bytes.ToArray(), ct);
}
static void Require(bool value, string message)
{
    if (!value) throw new Exception(message);
}
static void Throws(Action action)
{
    try { action(); }
    catch (InvalidDataException) { return; }
    throw new Exception("Malformed frame accepted");
}
static async Task Check(string name, Func<Task> action)
{
    await action();
    Console.WriteLine($"PASS {name}");
}

sealed class MemoryTransport(Channel<byte[]> incoming, Channel<byte[]> outgoing) : IMessageTransport
{
    public async Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct) =>
        await outgoing.Writer.WriteAsync(message.ToArray(), ct);
    public async Task<byte[]?> ReceiveAsync(CancellationToken ct) =>
        await incoming.Reader.WaitToReadAsync(ct) && incoming.Reader.TryRead(out var frame) ? frame : null;
    public void Dispose()
    {
        outgoing.Writer.TryComplete();
        incoming.Writer.TryComplete();
    }
}
