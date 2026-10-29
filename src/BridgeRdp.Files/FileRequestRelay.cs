using System.Threading.Channels;
using BridgeRdp.Proxy;

namespace BridgeRdp.Files;

/// <summary>Bounded, ordered request forwarding; drains replies before a new UI can connect.</summary>
public static class FileRequestRelay
{
    public const int WindowSize = 32;

    public static async Task RunAsync(IMessageTransport local, IMessageTransport remote, Action disconnectLocal, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = stop.Token;
        using var slots = new SemaphoreSlim(WindowSize);
        var sent = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var replies = ForwardRepliesAsync();
        try
        {
            while (await local.ReceiveAsync(token) is { } request)
            {
                await slots.WaitAsync(token);
                try { await remote.SendAsync(request, token); }
                catch { stop.Cancel(); throw; }
                sent.Writer.TryWrite(true);
            }
        }
        catch (Exception ex) when (!stop.IsCancellationRequested && ex is IOException or InvalidOperationException or ObjectDisposedException)
        { /* Local disconnect: drain requests already sent. */ }
        finally
        {
            sent.Writer.TryComplete();
            await replies;
        }

        async Task ForwardRepliesAsync()
        {
            bool connected = true;
            try
            {
                await foreach (var _ in sent.Reader.ReadAllAsync(token))
                {
                    var response = await remote.ReceiveAsync(token) ?? throw new EndOfStreamException("File channel closed");
                    slots.Release();
                    if (!connected) continue;
                    try { await local.SendAsync(response, token); }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
                    {
                        connected = false;
                        disconnectLocal();
                    }
                }
            }
            catch
            {
                // Unblock both the request reader and a sender waiting for a window slot.
                stop.Cancel();
                disconnectLocal();
                throw;
            }
        }
    }
}
