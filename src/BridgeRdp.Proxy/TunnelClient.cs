using System.Collections.Concurrent;

namespace BridgeRdp.Proxy;

public sealed class ProxyException(byte status, string message) : IOException(message)
{
    public byte Status { get; } = status;
}

public sealed class TunnelClient : IDisposable
{
    private readonly IMessageTransport _transport;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<(Packet, ReadOnlyMemory<byte>)>> _pending = new();
    private readonly SemaphoreSlim _writes = new(1);
    private int _nextId;
    public Task Completion { get; }
    public CancellationToken Stopping => _stop.Token;

    public TunnelClient(IMessageTransport transport)
    {
        _transport = transport;
        Completion = Task.Run(ReadLoopAsync);
    }

    public async Task<ReadOnlyMemory<byte>> RequestAsync(Packet request, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        var token = linked.Token;
        token.ThrowIfCancellationRequested();
        int id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<(Packet, ReadOnlyMemory<byte>)>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion)) throw new IOException("Duplicate request ID");
        try
        {
            await _writes.WaitAsync(token);
            try { await _transport.SendAsync(ProxyProtocol.Encode(request with { Id = id }, data.Span), token); }
            finally { _writes.Release(); }
            var (reply, payload) = await completion.Task.WaitAsync(token);
            if (reply.Error is not null) throw new ProxyException(reply.Status == 0 ? (byte)1 : reply.Status, reply.Error);
            return payload;
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _transport.ReceiveAsync(_stop.Token) is { } frame)
            {
                var (packet, data) = ProxyProtocol.Decode(frame);
                if (_pending.TryRemove(packet.Id, out var completion)) completion.TrySetResult((packet, data));
            }
        }
        finally
        {
            _stop.Cancel();
            foreach (var pending in _pending.Values) pending.TrySetException(new IOException("RDP proxy channel closed"));
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _transport.Dispose();
    }
}
