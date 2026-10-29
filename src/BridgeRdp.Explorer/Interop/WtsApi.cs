using System.Runtime.InteropServices;

namespace BridgeRdp.Explorer.Interop;

internal static class WtsApi
{
    public const uint WTS_CURRENT_SESSION = 0xFFFFFFFF;

    public const uint WTS_CHANNEL_OPTION_DYNAMIC = 0x00000001;

    // CHANNEL_PDU_HEADER: { UINT32 length; UINT32 flags; }
    public const int CHANNEL_PDU_HEADER_SIZE = 8;
    public const uint CHANNEL_FLAG_FIRST = 0x01;
    public const uint CHANNEL_FLAG_LAST = 0x02;

    public enum WTS_VIRTUAL_CLASS
    {
        WTSVirtualClientData = 0,
        WTSVirtualFileHandle = 1,
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern IntPtr WTSVirtualChannelOpenEx(
        uint sessionId,
        [MarshalAs(UnmanagedType.LPStr)] string pVirtualName,
        uint flags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSVirtualChannelQuery(
        IntPtr hChannelHandle,
        WTS_VIRTUAL_CLASS virtualClass,
        out IntPtr ppBuffer,
        out uint pBytesReturned);

    [DllImport("wtsapi32.dll")]
    public static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSVirtualChannelClose(IntPtr hChannelHandle);
}
