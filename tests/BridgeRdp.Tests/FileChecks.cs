using System.IO.Pipes;
using System.Security.Cryptography;
using BridgeRdp.Files;
using BridgeRdp.Protocol;
using System.Threading.Channels;

internal static class FileChecks
{
    public static async Task RunAsync(CancellationToken ct)
    {
        await CheckRelayAsync(ct);
        string root = Path.Combine(Path.GetTempPath(), "BridgeRdp.Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        string pipeName = "BridgeRdp.Tests." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var wait = server.WaitForConnectionAsync(ct);
        await pipe.ConnectAsync(ct);
        await wait;
        using var agentTransport = new StreamMessageTransport(server);
        using var client = new BridgeClient(new StreamMessageTransport(pipe));
        var toAgent = Channel.CreateBounded<byte[]>(64);
        var fromAgent = Channel.CreateBounded<byte[]>(64);
        using var relayRemote = new MemoryTransport(fromAgent, toAgent);
        using var fileRemote = new MemoryTransport(toAgent, fromAgent);
        var agent = Task.WhenAll(FileAgent.RunAsync(fileRemote, stop.Token),
            FileRequestRelay.RunAsync(agentTransport, relayRemote, server.Disconnect, stop.Token));
        try
        {
            CheckHandleLifetime(root);
            string remote = Path.Combine(root, "РЈРґР°Р»С‘РЅРЅР°СЏ РїР°РїРєР°");
            await client.CreateDirectoryAsync(remote, ct);
            var folders = await client.ListDirectoryAsync(root, ct);
            Require(folders.Any(f => f.IsDirectory && f.Name == "РЈРґР°Р»С‘РЅРЅР°СЏ РїР°РїРєР°"), "Unicode directory missing");
            Require((await client.ListDrivesAsync(ct)).Count > 0, "No drives returned");
            Console.WriteLine("PASS file drives and Unicode directory navigation over named pipe");

            byte[] bytes = RandomNumberGenerator.GetBytes(FrameCodec.MaxChunkSize * 67 + 357);
            string source = Path.Combine(root, "Р»РѕРєР°Р»СЊРЅС‹Р№-С„Р°Р№Р».bin");
            string target = Path.Combine(remote, "РґР°РЅРЅС‹Рµ.bin");
            string download = Path.Combine(root, "download.bin");
            await File.WriteAllBytesAsync(source, bytes, ct);
            await File.WriteAllTextAsync(target, "previous remote file", ct);
            await File.WriteAllTextAsync(download, "previous local file", ct);
            await client.UploadFileAsync(source, target, null, ct);
            Require((await File.ReadAllBytesAsync(target, ct)).SequenceEqual(bytes), "Upload corrupted");
            await client.DownloadFileAsync(target, download, null, ct);
            Require((await File.ReadAllBytesAsync(download, ct)).SequenceEqual(bytes), "Download corrupted");
            Require((await client.ListDirectoryAsync(remote, ct)).Single().Size == bytes.Length, "Wrong file size");
            Console.WriteLine("PASS chunked upload/download and replacement of existing files");

            string empty = Path.Combine(root, "empty");
            await File.WriteAllBytesAsync(empty, [], ct);
            await client.UploadFileAsync(empty, Path.Combine(remote, "empty"), null, ct);
            await client.DownloadFileAsync(Path.Combine(remote, "empty"), download, null, ct);
            Require(new FileInfo(download).Length == 0, "Empty file transfer failed");

            using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                await File.WriteAllTextAsync(target, "keep remote", ct);
                await ExpectFailureAsync(() => client.UploadFileAsync(source, target,
                    new InlineProgress(p => { if (p.Done > FrameCodec.MaxChunkSize * 2) cancel.Cancel(); }), cancel.Token));
                Require(await File.ReadAllTextAsync(target, ct) == "keep remote", "Canceled upload destroyed destination");
            }
            await client.UploadFileAsync(source, target, null, ct);
            using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                await File.WriteAllTextAsync(download, "keep local", ct);
                await ExpectFailureAsync(() => client.DownloadFileAsync(target, download,
                    new InlineProgress(p => { if (p.Done > FrameCodec.MaxChunkSize * 2) cancel.Cancel(); }), cancel.Token));
                Require(await File.ReadAllTextAsync(download, ct) == "keep local", "Canceled download destroyed destination");
            }
            await ExpectFailureAsync(() => client.DownloadFileAsync(Path.Combine(remote, "missing"), download, null, ct));
            Require(await File.ReadAllTextAsync(download, ct) == "keep local", "Failed read destroyed destination");
            Require(!Directory.EnumerateFiles(root, "*.part", SearchOption.AllDirectories).Any(), "Temporary file leaked");
            Console.WriteLine("PASS empty files, cancellation, failed reads and preservation of existing files");

            string renamed = Path.Combine(remote, "РїРµСЂРµРёРјРµРЅРѕРІР°РЅ.bin");
            await client.RenameAsync(target, renamed, ct);
            Require(File.Exists(renamed) && !File.Exists(target), "Rename failed");
            await client.DeleteAsync(remote, ct);
            Require(!Directory.Exists(remote), "Recursive delete failed");
            Console.WriteLine("PASS file rename and recursive directory deletion");

            // Interrupt a transfer after its first response while reads remain in flight.
            await ExpectFailureAsync(() => client.DownloadFileAsync(source, download,
                new InlineProgress(_ => client.Dispose()), ct));
            Require(await File.ReadAllTextAsync(download, ct) == "keep local", "Disconnect destroyed destination");
            Console.WriteLine("PASS file channel disconnect preserves destination");
        }
        finally
        {
            client.Dispose();
            stop.Cancel();
            try { await agent; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            // The generated test directory is the only tree removed here.
            if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()))
                throw new InvalidOperationException("Unexpected test directory");
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CheckHandleLifetime(string root)
    {
        var path = Path.Combine(root, "handles.bin");
        using (var session = new FileSystemSession())
        {
            session.Execute(new Request { Op = Operation.WriteFile, Path = path }, new byte[] { 1 });
            try
            {
                using var unexpected = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                throw new Exception("Transfer handle was not retained");
            }
            catch (IOException) { }
            session.Execute(new Request { Op = Operation.WriteFile, Path = path, Offset = 1 }, new byte[] { 2 });
            session.Execute(new Request { Op = Operation.CloseFile, Path = path }, default);
            using (var released = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Require(released.Length == 2, "Transfer writes were truncated");
            session.Execute(new Request { Op = Operation.ReadFile, Path = path, Length = 1 }, default);
            session.Execute(new Request { Op = Operation.CloseFiles }, default);
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            session.Execute(new Request { Op = Operation.WriteFile, Path = path }, new byte[] { 3 });
        }
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        File.Delete(path);
        Console.WriteLine("PASS retained file handles, explicit close, session reset and disposal");
    }

    private static async Task CheckRelayAsync(CancellationToken ct)
    {
        var requests = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        var responses = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        var forwarded = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        var replies = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        using var local = new MemoryTransport(requests, responses);
        using var remote = new MemoryTransport(replies, forwarded);
        var relay = FileRequestRelay.RunAsync(local, remote, local.Dispose, ct);
        for (int i = 0; i < 40; i++) await requests.Writer.WriteAsync([(byte)i], ct);
        // No replies yet: all 32 window slots must reach the remote side independently.
        for (int i = 0; i < FileRequestRelay.WindowSize; i++)
            Require((await forwarded.Reader.ReadAsync(ct))[0] == i, "Relay did not pipeline requests in order");
        Require(!forwarded.Reader.TryRead(out _), "Relay window exceeded");
        for (int i = 0; i < 40; i++)
        {
            await replies.Writer.WriteAsync([(byte)i], ct);
            Require((await responses.Reader.ReadAsync(ct))[0] == i, "Relay response reordered");
            if (i < 8) Require((await forwarded.Reader.ReadAsync(ct))[0] == i + 32, "Relay window not replenished");
        }
        requests.Writer.TryComplete();
        await relay;

        // A vanished UI must not leave responses for the next connection.
        var pending = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        var unused = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        using var disconnected = new MemoryTransport(pending, unused);
        var drain = FileRequestRelay.RunAsync(disconnected, remote, disconnected.Dispose, ct);
        for (int i = 0; i < 4; i++) await pending.Writer.WriteAsync([(byte)i], ct);
        for (int i = 0; i < 4; i++) await forwarded.Reader.ReadAsync(ct);
        pending.Writer.TryComplete();
        unused.Writer.TryComplete();
        for (int i = 0; i < 4; i++) await replies.Writer.WriteAsync([(byte)i], ct);
        await drain;
        Require(!replies.Reader.TryRead(out _), "Disconnect left stale replies");
        // A remote failure while the window is full must also unblock the sender.
        var blockedRequests = Channel.CreateUnbounded<byte[]>();
        var blockedResponses = Channel.CreateUnbounded<byte[]>();
        var deadRequests = Channel.CreateUnbounded<byte[]>();
        var deadResponses = Channel.CreateUnbounded<byte[]>();
        using var blockedLocal = new MemoryTransport(blockedRequests, blockedResponses);
        using var deadRemote = new MemoryTransport(deadResponses, deadRequests);
        var failedRelay = FileRequestRelay.RunAsync(blockedLocal, deadRemote, blockedLocal.Dispose, ct);
        for (int i = 0; i < 40; i++) await blockedRequests.Writer.WriteAsync([(byte)i], ct);
        for (int i = 0; i < FileRequestRelay.WindowSize; i++) await deadRequests.Reader.ReadAsync(ct);
        deadResponses.Writer.TryComplete();
        await ExpectFailureAsync(() => failedRelay.WaitAsync(TimeSpan.FromSeconds(3), ct));
        Console.WriteLine("PASS file bridge pipeline, bounded window, ordered responses, disconnect drain and remote failure");
    }

    private static async Task ExpectFailureAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or RemoteException or ObjectDisposedException) { return; }
        throw new Exception("Expected file operation to fail");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class InlineProgress(Action<(long Done, long Total)> report) : IProgress<(long Done, long Total)>
    {
        public void Report((long Done, long Total) value) => report(value);
    }
}
