using System.Runtime.InteropServices;
using System.Threading.Channels;
using BridgeRdp.Client.Interop;
using BridgeRdp.Proxy;

namespace BridgeRdp.Client;

[ComVisible(true), Guid("B22893E4-FA3C-49D1-98E2-A124AB1C50B3"), ClassInterface(ClassInterfaceType.None)]
public sealed class ProxyChannel : IWTSVirtualChannelCallback, IMessageTransport
{
    private readonly NativeVirtualChannel _channel;
    private readonly Channel<byte[]> _inbox = Channel.CreateBounded<byte[]>(512);
    private readonly TunnelClient _tunnel;
    private readonly SocksServer _server;
    private int _closed;
    internal bool IsClosed => Volatile.Read(ref _closed) != 0;

    internal ProxyChannel(NativeVirtualChannel channel)
    {
        _channel = channel;
        _tunnel = new TunnelClient(this);
        try
        {
            var value = Environment.GetEnvironmentVariable("BRIDGERDP_SOCKS_PORT");
            int port = value is null ? 1080 : int.Parse(value);
            if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            _server = new SocksServer(_tunnel, port) { Error = ex => Log.Error("SOCKS connection failed", ex) };
        }
        catch { _tunnel.Dispose(); throw; }
        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        Log.Info($"SOCKS5 listening on 127.0.0.1:{_server.Port}");
        try { await _server.RunAsync(); }
        catch (Exception ex) { Log.Error("SOCKS listener stopped", ex); }
        finally
        {
            _server.Dispose();
            _tunnel.Dispose();
            try { await _tunnel.Completion; } catch (Exception ex) { Log.Error("Proxy channel stopped", ex); }
        }
    }

    public Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _channel.Write(message.Span);
        return Task.CompletedTask;
    }

    public async Task<byte[]?> ReceiveAsync(CancellationToken ct) =>
        await _inbox.Reader.WaitToReadAsync(ct) && _inbox.Reader.TryRead(out var message) ? message : null;

    public void OnDataReceived(uint cbSize, IntPtr pBuffer)
    {
        if (cbSize > ProxyProtocol.MaxFrameSize) { Dispose(); return; }
        var data = new byte[(int)cbSize];
        Marshal.Copy(pBuffer, data, 0, data.Length);
        if (!_inbox.Writer.TryWrite(data)) Dispose();
    }

    public void OnClose() => Dispose();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _inbox.Writer.TryComplete();
        try { _channel.Close(); } catch (Exception ex) { Log.Error("Proxy close failed", ex); }
        _channel.Dispose();
    }
}

[ComVisible(true), Guid("B4DAA3FB-34F7-47B6-9062-017012730324"), ClassInterface(ClassInterfaceType.None)]
public sealed class ProxyChannelListener : IWTSListenerCallback
{
    private readonly object _sync = new();
    private ProxyChannel? _active;

    public void CloseChannel()
    {
        lock (_sync) { _active?.Dispose(); _active = null; }
    }

    public void OnNewChannelConnection(IntPtr pChannel, string? data, out bool pbAccept, out IWTSVirtualChannelCallback? ppCallback)
    {
        try
        {
            lock (_sync)
            {
                if (_active is { IsClosed: false }) throw new IOException("An agent is already connected to this RDP session");
                _active = new ProxyChannel(new NativeVirtualChannel(pChannel));
                ppCallback = _active;
                pbAccept = true;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Proxy channel rejected (check SOCKS port availability)", ex);
            ppCallback = null;
            pbAccept = false;
        }
    }
}
