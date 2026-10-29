namespace BridgeRdp.Explorer;

// По умолчанию запускается локально и показывает файлы целевой RDP-машины.
// --legacy сохраняет прежний режим: файлы клиента из удалённой сессии.
internal static class Program
{
    internal static bool Legacy { get; private set; }
    internal static int Port { get; private set; } = 1080;

    internal static async Task<BridgeRdp.Files.BridgeClient> ConnectAsync()
    {
        if (Legacy)
            return new BridgeRdp.Files.BridgeClient(new ExplorerDvcTransport(await Task.Run(() => DvcChannel.Open(BridgeRdp.Protocol.ChannelInfo.Name))));
        return new BridgeRdp.Files.BridgeClient(await BridgeRdp.Files.FileBridge.ConnectAsync(Port, CancellationToken.None));
    }

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--legacy") Legacy = true;
            else if (args[i] == "--port" && ++i < args.Length && int.TryParse(args[i], out var port) && port is > 0 and <= 65535) Port = port;
            else { MessageBox.Show("Usage: BridgeRdp.Explorer.exe [--port 1080] [--legacy]"); return; }
        }
        Application.Run(new MainForm());
    }
}

internal sealed class ExplorerDvcTransport(DvcChannel channel) : BridgeRdp.Proxy.IMessageTransport
{
    public Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct) => channel.SendAsync(message, ct);
    public Task<byte[]?> ReceiveAsync(CancellationToken ct) => channel.ReceiveAsync(ct);
    public void Dispose() => channel.Dispose();
}
