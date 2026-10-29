using System.Runtime.InteropServices;
using BridgeRdp.Client.Interop;
using BridgeRdp.Protocol;

namespace BridgeRdp.Client;

/// <summary>
/// Точка входа DVC-плагина. mstsc создаёт его через CoCreateInstance по CLSID,
/// указанному в HKCU\Software\Microsoft\Terminal Server Client\Default\AddIns\BridgeRdp\Name.
/// </summary>
[ComVisible(true)]
[Guid(ClsidString)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class BridgeRdpPlugin : IWTSPlugin
{
    public const string ClsidString = "81E782FA-3E1F-4574-9FB2-B689AFCA5BB1";

    // Держим ссылки, чтобы listener жил столько же, сколько плагин.
    private IWTSListener? _listener;
    private ChannelListener? _listenerCallback;
    private IWTSListener? _proxyListener;
    private ProxyChannelListener? _proxyCallback;
    private IWTSListener? _filesListener;
    private FilesChannelListener? _filesCallback;

    public void Initialize(IWTSVirtualChannelManager pChannelMgr)
    {
        try
        {
            _listenerCallback = new ChannelListener();
            _listener = pChannelMgr.CreateListener(ChannelInfo.Name, 0, _listenerCallback);
            Log.Info($"Initialize: listener '{ChannelInfo.Name}' created");
            _proxyCallback = new ProxyChannelListener();
            _proxyListener = pChannelMgr.CreateListener(BridgeRdp.Proxy.ProxyProtocol.ChannelName, 0, _proxyCallback);
            _filesCallback = new FilesChannelListener();
            _filesListener = pChannelMgr.CreateListener(BridgeRdp.Files.FileBridge.ChannelName, 0, _filesCallback);
        }
        catch (Exception ex)
        {
            Log.Error("Initialize failed", ex);
            throw;
        }
    }

    public void Connected() => Log.Info("Connected");

    public void Disconnected(uint dwDisconnectCode)
    {
        Log.Info($"Disconnected, code={dwDisconnectCode}");
        _proxyCallback?.CloseChannel();
        _filesCallback?.CloseChannel();
    }

    public void Terminated()
    {
        Log.Info("Terminated");
        _proxyCallback?.CloseChannel();
        _filesCallback?.CloseChannel();
        _listener = null;
        _listenerCallback = null;
        _proxyListener = null;
        _proxyCallback = null;
        _filesListener = null;
        _filesCallback = null;
    }
}
