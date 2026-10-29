using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using BridgeRdp.Client.Interop;
using BridgeRdp.Files;
using BridgeRdp.Protocol;
using BridgeRdp.Proxy;

namespace BridgeRdp.Client;

[ComVisible(true), Guid("C506B870-8D3C-4314-BE1B-16FC955E995B"), ClassInterface(ClassInterfaceType.None)]
public sealed class FilesChannel : IWTSVirtualChannelCallback, IDisposable
{
    private readonly NativeVirtualChannel _channel;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<byte[]> _inbox = Channel.CreateBounded<byte[]>(FileRequestRelay.WindowSize + 1);
    private readonly NamedPipeServerStream _pipe;
    private int _closed;
    internal bool IsClosed => Volatile.Read(ref _closed) != 0;

    internal FilesChannel(NativeVirtualChannel channel)
    {
        _channel = channel;
        try
        {
            int port = int.Parse(Environment.GetEnvironmentVariable("BRIDGERDP_SOCKS_PORT") ?? "1080");
            _pipe = new NamedPipeServerStream(FileBridge.PipeName(port), PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 64 * 1024, 64 * 1024);
        }
        catch { _channel.Dispose(); throw; }
        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _pipe.WaitForConnectionAsync(_stop.Token);
                // Drain all in-flight replies before accepting another UI so IDs cannot collide.
                var transport = new StreamMessageTransport(_pipe);
                try
                {
                    await FileRequestRelay.RunAsync(transport, new RemoteTransport(this),
                        () => { if (_pipe.IsConnected) _pipe.Disconnect(); }, _stop.Token);
                    // All previous responses have been consumed; release handles even after an abrupt UI exit.
                    _channel.Write(FrameCodec.Encode(new Request { Op = Operation.CloseFiles }, ProtocolJson.Default.Request));
                    var cleanup = await _inbox.Reader.ReadAsync(_stop.Token);
                    var (response, _) = FrameCodec.Decode(cleanup, ProtocolJson.Default.Response);
                    if (response.Error is { } error) throw new IOException(error);
                }
                catch (IOException ex) when (!_stop.IsCancellationRequested) { Log.Error("Explorer disconnected", ex); }
                finally { if (_pipe.IsConnected) _pipe.Disconnect(); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { Log.Error("File channel stopped", ex); }
        finally
        {
            Dispose();
            _pipe.Dispose();
            _channel.Dispose();
        }
    }

    private sealed class RemoteTransport(FilesChannel owner) : IMessageTransport
    {
        public Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            owner._channel.Write(message.ToArray());
            return Task.CompletedTask;
        }
        public async Task<byte[]?> ReceiveAsync(CancellationToken ct) => await owner._inbox.Reader.ReadAsync(ct);
        public void Dispose() { }
    }

    public void OnDataReceived(uint cbSize, IntPtr pBuffer)
    {
        if (cbSize > FileBridge.MaxMessageSize) { Dispose(); return; }
        var data = new byte[(int)cbSize];
        Marshal.Copy(pBuffer, data, 0, data.Length);
        if (!_inbox.Writer.TryWrite(data)) Dispose();
    }

    public void OnClose() => Dispose();
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _stop.Cancel();
        _inbox.Writer.TryComplete();
        try { _channel.Close(); } catch (Exception ex) { Log.Error("File channel close failed", ex); }
    }
}

[ComVisible(true), Guid("E15938C8-F253-487A-8F23-756B243F71DB"), ClassInterface(ClassInterfaceType.None)]
public sealed class FilesChannelListener : IWTSListenerCallback
{
    private readonly object _sync = new();
    private FilesChannel? _active;
    public void CloseChannel() { lock (_sync) { _active?.Dispose(); _active = null; } }

    public void OnNewChannelConnection(IntPtr pChannel, string? data, out bool pbAccept, out IWTSVirtualChannelCallback? ppCallback)
    {
        try
        {
            lock (_sync)
            {
                if (_active is { IsClosed: false }) throw new IOException("A file agent is already connected");
                _active = new FilesChannel(new NativeVirtualChannel(pChannel));
                ppCallback = _active;
                pbAccept = true;
            }
        }
        catch (Exception ex)
        {
            Log.Error("File channel rejected", ex);
            ppCallback = null;
            pbAccept = false;
        }
    }
}
