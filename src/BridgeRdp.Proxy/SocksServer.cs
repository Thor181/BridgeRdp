using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BridgeRdp.Proxy;

/// <summary>SOCKS5 CONNECT only. DNS names are resolved by the target machine.</summary>
public sealed class SocksServer : IDisposable
{
    private readonly TunnelClient _tunnel;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop;
    private int _nextConnection;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public Action<Exception>? Error { get; init; }

    public SocksServer(TunnelClient tunnel, int port = 1080)
    {
        _tunnel = tunnel;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(tunnel.Stopping);
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
    }

    public async Task RunAsync()
    {
        var active = new HashSet<Task>();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var socket = await _listener.AcceptTcpClientAsync(_stop.Token);
                active.RemoveWhere(t => t.IsCompleted);
                if (active.Count >= ProxyProtocol.MaxConnections) { socket.Dispose(); continue; }
                active.Add(HandleAsync(socket, _stop.Token));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            _stop.Cancel();
            _listener.Stop();
            await Task.WhenAll(active);
        }
    }

    private async Task HandleAsync(TcpClient socket, CancellationToken ct)
    {
        using (socket)
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            int connection = Interlocked.Increment(ref _nextConnection);
            bool opened = false;
            bool replied = false;
            var stream = socket.GetStream();
            try
            {
                socket.NoDelay = true;
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handshake.CancelAfter(TimeSpan.FromSeconds(30));
                var token = handshake.Token;
                var greeting = await ReadAsync(stream, 2, token);
                if (greeting[0] != 5 || greeting[1] == 0) return;
                var methods = await ReadAsync(stream, greeting[1], token);
                if (!methods.Contains((byte)0))
                {
                    await stream.WriteAsync(new byte[] { 5, 255 }, token);
                    return;
                }
                await stream.WriteAsync(new byte[] { 5, 0 }, token);
                var request = await ReadAsync(stream, 4, token);
                if (request[0] != 5 || request[2] != 0) throw new InvalidDataException("Invalid SOCKS request");
                if (request[1] != 1) { await ReplyAsync(stream, 7, token); return; }
                string host;
                switch (request[3])
                {
                    case 1: host = new IPAddress(await ReadAsync(stream, 4, token)).ToString(); break;
                    case 4: host = new IPAddress(await ReadAsync(stream, 16, token)).ToString(); break;
                    case 3:
                        int size = (await ReadAsync(stream, 1, token))[0];
                        if (size == 0) throw new InvalidDataException("Empty host");
                        host = Encoding.ASCII.GetString(await ReadAsync(stream, size, token));
                        break;
                    default: await ReplyAsync(stream, 8, token); return;
                }
                var portBytes = await ReadAsync(stream, 2, token);
                int port = (portBytes[0] << 8) | portBytes[1];
                // Mark before sending: a canceled connect may still have reached the agent.
                opened = true;
                await _tunnel.RequestAsync(new Packet { Command = Command.Connect, Connection = connection, Host = host, Port = port }, default, token);
                await ReplyAsync(stream, 0, token);
                replied = true;
                var upstream = UploadAsync();
                var downstream = DownloadAsync();
                // EOF is a half-close: allow the other direction to finish. Errors abort both.
                var first = await Task.WhenAny(upstream, downstream);
                if (first.IsFaulted || first.IsCanceled) lifetime.Cancel();
                await Task.WhenAll(upstream, downstream);

                async Task UploadAsync()
                {
                    var buffer = new byte[ProxyProtocol.ChunkSize];
                    int count;
                    while ((count = await stream.ReadAsync(buffer, lifetime.Token)) > 0)
                        await _tunnel.RequestAsync(new Packet { Command = Command.Write, Connection = connection }, buffer.AsMemory(0, count), lifetime.Token);
                    await _tunnel.RequestAsync(new Packet { Command = Command.Shutdown, Connection = connection }, default, lifetime.Token);
                }

                async Task DownloadAsync()
                {
                    while (true)
                    {
                        var data = await _tunnel.RequestAsync(new Packet { Command = Command.Read, Connection = connection }, default, lifetime.Token);
                        if (data.IsEmpty) { socket.Client.Shutdown(SocketShutdown.Send); return; }
                        await stream.WriteAsync(data, lifetime.Token);
                    }
                }
            }
            catch (Exception ex)
            {
                lifetime.Cancel();
                if (!replied)
                {
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await ReplyAsync(stream, ex is ProxyException proxy ? proxy.Status : (byte)1, timeout.Token);
                    }
                    catch { /* SOCKS client may already have disconnected. */ }
                }
                if (!ct.IsCancellationRequested) Error?.Invoke(ex);
            }
            finally
            {
                lifetime.Cancel();
                if (opened && !_tunnel.Stopping.IsCancellationRequested)
                {
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        await _tunnel.RequestAsync(new Packet { Command = Command.Close, Connection = connection }, default, timeout.Token);
                    }
                    catch { /* Channel teardown also closes all remote sockets. */ }
                }
            }
        }
    }

    private static async Task<byte[]> ReadAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        var data = new byte[count];
        await stream.ReadExactlyAsync(data, ct);
        return data;
    }

    private static ValueTask ReplyAsync(NetworkStream stream, byte status, CancellationToken ct) =>
        stream.WriteAsync(new byte[] { 5, status, 0, 1, 0, 0, 0, 0, 0, 0 }, ct);

    public void Dispose() => _stop.Cancel();
}
