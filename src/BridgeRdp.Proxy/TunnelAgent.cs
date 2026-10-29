using System.Collections.Concurrent;
using System.Net.Sockets;

namespace BridgeRdp.Proxy;

/// <summary>Runs on the RDP target. Each socket has at most one read and one write in flight.</summary>
public sealed class TunnelAgent(IMessageTransport transport)
{
    private sealed class Connection : IDisposable
    {
        public TcpClient Socket { get; } = new();
        public NetworkStream Stream { get; set; } = null!;
        public SemaphoreSlim Read { get; } = new(1);
        public SemaphoreSlim Write { get; } = new(1);
        public void Dispose() => Socket.Dispose();
    }

    private readonly ConcurrentDictionary<int, Connection> _connections = new();
    private readonly SemaphoreSlim _writes = new(1);

    public async Task RunAsync(CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var active = new HashSet<Task>();
        try
        {
            while (await transport.ReceiveAsync(stop.Token) is { } frame)
            {
                var (request, data) = ProxyProtocol.Decode(frame);
                active.RemoveWhere(t => t.IsCompletedSuccessfully);
                // A failed response write means the whole transport is unusable.
                foreach (var failed in active.Where(t => t.IsFaulted)) await failed;
                if (active.Count >= ProxyProtocol.MaxConnections * 3)
                    throw new InvalidDataException("Too many outstanding proxy requests");
                active.Add(HandleAsync(request, data, stop.Token));
            }
        }
        finally
        {
            stop.Cancel();
            foreach (var connection in _connections.Values) connection.Dispose();
            try { await Task.WhenAll(active); } catch { /* Transport is already closing. */ }
            _connections.Clear();
        }
    }

    private async Task HandleAsync(Packet request, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        Packet response = request with { Host = null, Port = 0 };
        ReadOnlyMemory<byte> result = default;
        try { result = await ExecuteAsync(request, data, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            byte status = (byte)(ex is SocketException socket ? socket.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => 5,
                SocketError.NetworkUnreachable => 3,
                SocketError.HostNotFound or SocketError.HostUnreachable => 4,
                SocketError.TimedOut => 6,
                _ => 1
            } : 1);
            response = response with { Error = ex.Message, Status = status };
        }
        await _writes.WaitAsync(ct);
        try { await transport.SendAsync(ProxyProtocol.Encode(response, result.Span), ct); }
        finally { _writes.Release(); }
    }

    private async Task<ReadOnlyMemory<byte>> ExecuteAsync(Packet request, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (request.Connection <= 0) throw new InvalidDataException("Invalid connection ID");
        if (request.Command == Command.Connect)
        {
            if (string.IsNullOrWhiteSpace(request.Host) || request.Host.Length > 255 || request.Port is < 1 or > 65535)
                throw new InvalidDataException("Invalid target address");
            if (_connections.Count >= ProxyProtocol.MaxConnections) throw new IOException("Connection limit reached");
            var connection = new Connection();
            if (!_connections.TryAdd(request.Connection, connection))
            {
                connection.Dispose();
                throw new IOException("Duplicate connection ID");
            }
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await connection.Socket.ConnectAsync(request.Host, request.Port, timeout.Token);
                connection.Socket.NoDelay = true;
                connection.Stream = connection.Socket.GetStream();
            }
            catch
            {
                _connections.TryRemove(request.Connection, out _);
                connection.Dispose();
                throw;
            }
            return default;
        }
        if (request.Command == Command.Close)
        {
            if (_connections.TryRemove(request.Connection, out var closing)) closing.Dispose();
            return default;
        }
        if (!_connections.TryGetValue(request.Connection, out var existing)) throw new IOException("Unknown connection");
        var gate = request.Command == Command.Read ? existing.Read : existing.Write;
        if (!gate.Wait(0)) throw new IOException("Concurrent operation on the same socket direction");
        try
        {
            switch (request.Command)
            {
                case Command.Read:
                    var buffer = new byte[ProxyProtocol.ChunkSize];
                    int count = await existing.Stream.ReadAsync(buffer, ct);
                    return buffer.AsMemory(0, count);
                case Command.Write:
                    await existing.Stream.WriteAsync(data, ct);
                    return default;
                case Command.Shutdown:
                    existing.Socket.Client.Shutdown(SocketShutdown.Send);
                    return default;
                default: throw new InvalidDataException("Unknown proxy command");
            }
        }
        finally { gate.Release(); }
    }
}
