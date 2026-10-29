using BridgeRdp.Explorer;
using BridgeRdp.Proxy;
using BridgeRdp.Files;
using BridgeRdp.Agent;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
try
{
    if (args.Any(a => a is not "--files-only" and not "--proxy-only") || args.Length > 1)
        throw new ArgumentException("Usage: BridgeRdp.Agent.exe [--files-only | --proxy-only]");
    Console.WriteLine("Run this agent inside the target RDP session. Ctrl+C stops all services.");
    var services = new List<Task>();
    services.Add(SessionKeepAlive.RunAsync(stop.Token));
    Console.WriteLine("Session keep-alive enabled: mouse moves after 3 minutes without keyboard or mouse input.");
    if (!args.Contains("--files-only")) services.Add(Task.Run(async () =>
    {
        using var transport = new AgentTransport(DvcChannel.Open(ProxyProtocol.ChannelName));
        Console.WriteLine("SOCKS5 channel connected (default local port: 1080).");
        await new TunnelAgent(transport).RunAsync(stop.Token);
    }));
    if (!args.Contains("--proxy-only")) services.Add(Task.Run(async () =>
    {
        using var transport = new AgentTransport(DvcChannel.Open(FileBridge.ChannelName));
        Console.WriteLine("File channel connected. Open BridgeRdp.Explorer.exe on the local machine.");
        await FileAgent.RunAsync(transport, stop.Token);
    }));
    await Task.WhenAny(services);
    stop.Cancel();
    // Observe failures even when another service ends through cancellation first.
    var all = Task.WhenAll(services);
    try { await all; }
    catch when (all.Exception is not null) { throw all.Exception.Flatten(); }
    Console.WriteLine("RDP channels closed.");
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}

sealed class AgentTransport(DvcChannel channel) : IMessageTransport
{
    public Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct) => channel.SendAsync(message, ct);
    public Task<byte[]?> ReceiveAsync(CancellationToken ct) => channel.ReceiveAsync(ct);
    public void Dispose() => channel.Dispose();
}
