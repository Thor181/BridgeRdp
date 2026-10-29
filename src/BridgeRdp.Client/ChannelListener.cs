using BridgeRdp.Client.Interop;

namespace BridgeRdp.Client;

/// <summary>
/// Вызывается, когда серверная сторона открывает канал (WTSVirtualChannelOpenEx).
/// На каждое открытие создаётся отдельный <see cref="ClientChannel"/>.
/// </summary>
public sealed class ChannelListener : IWTSListenerCallback
{
    public void OnNewChannelConnection(
        IntPtr pChannel,
        string? data,
        out bool pbAccept,
        out IWTSVirtualChannelCallback? ppCallback)
    {
        try
        {
            ppCallback = new ClientChannel(new NativeVirtualChannel(pChannel));
            pbAccept = true;
            Log.Info("New channel connection accepted");
        }
        catch (Exception ex)
        {
            Log.Error("OnNewChannelConnection failed", ex);
            pbAccept = false;
            ppCallback = null;
        }
    }
}
