using System.Runtime.InteropServices;
using System.Threading.Channels;
using BridgeRdp.Client.Interop;
using BridgeRdp.Protocol;
using BridgeRdp.Files;

namespace BridgeRdp.Client;

/// <summary>
/// Один открытый канал на стороне клиента. Входящие запросы складываются в очередь
/// и выполняются по одному в фоновом потоке, чтобы не блокировать потоки mstsc
/// и сохранить порядок (важно для записи файла чанками).
/// </summary>
public sealed class ClientChannel : IWTSVirtualChannelCallback
{
    private readonly NativeVirtualChannel _channel;
    private readonly Channel<byte[]> _inbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

    internal ClientChannel(NativeVirtualChannel channel)
    {
        _channel = channel;
        _ = Task.Run(ProcessInboxAsync);
    }

    private async Task ProcessInboxAsync()
    {
        await foreach (var message in _inbox.Reader.ReadAllAsync())
        {
            try
            {
                HandleMessage(message);
            }
            catch (Exception ex)
            {
                Log.Error("Failed to handle message", ex);
            }
        }

        _channel.Dispose();
    }

    private void HandleMessage(byte[] message)
    {
        var (request, payload) = FrameCodec.Decode(message, ProtocolJson.Default.Request);

        Response response;
        byte[]? responsePayload = null;
        try
        {
            (response, responsePayload) = FileSystemService.Execute(request, payload);
        }
        catch (Exception ex)
        {
            Log.Error($"{request.Op} '{request.Path}' failed", ex);
            response = new Response { Error = ex.Message };
        }

        response.Id = request.Id;
        _channel.Write(FrameCodec.Encode(response, ProtocolJson.Default.Response, responsePayload));
    }

    void IWTSVirtualChannelCallback.OnDataReceived(uint cbSize, IntPtr pBuffer)
    {
        // На клиенте сообщение приходит целиком, без CHANNEL_PDU_HEADER.
        var data = new byte[cbSize];
        Marshal.Copy(pBuffer, data, 0, (int)cbSize);
        _inbox.Writer.TryWrite(data);
    }

    void IWTSVirtualChannelCallback.OnClose()
    {
        Log.Info("Channel closed");
        _inbox.Writer.TryComplete();
    }
}
