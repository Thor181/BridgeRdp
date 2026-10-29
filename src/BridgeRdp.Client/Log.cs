namespace BridgeRdp.Client;

/// <summary>
/// Плагин живёт внутри mstsc.exe без консоли, поэтому пишем лог в %TEMP%\BridgeRdp.Client.log.
/// </summary>
internal static class Log
{
    private static readonly object Sync = new();
    private static readonly string FilePath = Path.Combine(Path.GetTempPath(), "BridgeRdp.Client.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                File.AppendAllText(FilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [T{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Лог не должен ронять mstsc.
        }
    }
}
