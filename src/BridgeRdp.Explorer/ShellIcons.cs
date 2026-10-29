using System.Runtime.InteropServices;

namespace BridgeRdp.Explorer;

/// <summary>
/// Иконки и названия типов из оболочки Windows по расширению — файл при этом не обязан существовать локально.
/// </summary>
internal static class ShellIcons
{
    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_SMALLICON = 0x1;
    private const uint SHGFI_TYPENAME = 0x400;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>Иконка (16px) и название типа для файла с данным расширением.</summary>
    public static (Icon? Icon, string TypeName) ForExtension(string extension)
    {
        var info = new SHFILEINFO();
        var result = SHGetFileInfo("file" + extension, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_ICON | SHGFI_SMALLICON | SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            return (null, info.szTypeName ?? "");
        }

        var icon = (Icon)Icon.FromHandle(info.hIcon).Clone();
        DestroyIcon(info.hIcon);
        return (icon, info.szTypeName);
    }

    public static Icon Stock(StockIconId id) => SystemIcons.GetStockIcon(id, StockIconOptions.SmallIcon);
}
