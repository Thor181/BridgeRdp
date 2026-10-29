using System.Runtime.InteropServices;

namespace BridgeRdp.Client.Interop;

// Интерфейсы из tsvirtualchannels.h (Remote Desktop Client DVC API).
// IWTSVirtualChannel принимаем как сырой указатель и вызываем через vtable (см. NativeVirtualChannel),
// чтобы CLR не пытался маршалить его между апартаментами при записи из рабочих потоков.

[ComImport, ComVisible(true), Guid("A1230201-1439-4E62-A414-190D0AC3D40E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWTSPlugin
{
    void Initialize(IWTSVirtualChannelManager pChannelMgr);
    void Connected();
    void Disconnected(uint dwDisconnectCode);
    void Terminated();
}

[ComImport, ComVisible(true), Guid("A1230206-9A39-4D58-8674-CDB4DFF4E73B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWTSListener
{
    [return: MarshalAs(UnmanagedType.Interface)]
    object GetConfiguration(); // IPropertyBag
}

[ComImport, ComVisible(true), Guid("A1230203-D6A7-11D8-B9FD-000BDBD1F198"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWTSListenerCallback
{
    void OnNewChannelConnection(
        IntPtr pChannel, // IWTSVirtualChannel*
        [MarshalAs(UnmanagedType.BStr)] string? data,
        [MarshalAs(UnmanagedType.Bool)] out bool pbAccept,
        out IWTSVirtualChannelCallback? ppCallback);
}

[ComImport, ComVisible(true), Guid("A1230204-D6A7-11D8-B9FD-000BDBD1F198"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWTSVirtualChannelCallback
{
    void OnDataReceived(uint cbSize, IntPtr pBuffer);
    void OnClose();
}

[ComImport, ComVisible(true), Guid("A1230205-D6A7-11D8-B9FD-000BDBD1F198"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWTSVirtualChannelManager
{
    IWTSListener CreateListener(
        [MarshalAs(UnmanagedType.LPStr)] string pszChannelName,
        uint uFlags,
        IWTSListenerCallback pListenerCallback);
}
