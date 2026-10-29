using System.Collections.Concurrent;
using BridgeRdp.Protocol;
using BridgeRdp.Proxy;

namespace BridgeRdp.Files;

public readonly record struct Reply(Response Response, ReadOnlyMemory<byte> Payload);

/// <summary>Файловая операция на другой стороне RDP завершилась ошибкой.</summary>
public sealed class RemoteException(string message) : Exception(message);

/// <summary>
/// RPC-клиент поверх DVC: отправляет <see cref="Request"/> плагину и ждёт <see cref="Response"/> с тем же Id.
/// Можно держать несколько запросов в полёте одновременно.
/// </summary>
public sealed class BridgeClient : IDisposable
{
    // Сколько чанков файла держать в полёте — скрывает задержку RDP-канала.
    private const int PipelineDepth = 8;

    private readonly IMessageTransport _channel;
    private readonly SemaphoreSlim _writeLock = new(1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<Reply>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoop;
    private int _nextId;
    private int _closed;

    public BridgeClient(IMessageTransport channel)
    {
        _channel = channel;
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>Канал закрыт (клиент отключился или плагин выгружен). Аргумент — причина, если была ошибка.</summary>
    public event Action<Exception?>? Disconnected;

    // ---- Операции --------------------------------------------------------------

    public async Task<List<DriveEntry>> ListDrivesAsync(CancellationToken ct = default) =>
        (await RequestAsync(new Request { Op = Operation.ListDrives }, ct: ct)).Response.Drives ?? [];

    public async Task<List<FileEntry>> ListDirectoryAsync(string path, CancellationToken ct = default) =>
        (await RequestAsync(new Request { Op = Operation.ListDirectory, Path = path }, ct: ct)).Response.Entries ?? [];

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default) =>
        RequestAsync(new Request { Op = Operation.CreateDirectory, Path = path }, ct: ct);

    public Task DeleteAsync(string path, CancellationToken ct = default) =>
        RequestAsync(new Request { Op = Operation.Delete, Path = path }, ct: ct);

    public Task RenameAsync(string path, string newPath, CancellationToken ct = default) =>
        RequestAsync(new Request { Op = Operation.Rename, Path = path, NewPath = newPath }, ct: ct);

    /// <summary>Скачивает файл с клиентской машины. Прогресс — число полученных байт.</summary>
    public async Task DownloadFileAsync(string remotePath, string localPath, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        var staging = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(localPath))!, ".bridgerdp-" + Guid.NewGuid().ToString("N") + ".part");
        var window = new Queue<Task<Reply>>();
        try
        {
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {

                var first = await ReadChunkAsync(remotePath, 0, ct);
                var total = first.Response.FileLength;
                if (total < 0 || first.Payload.Length != Math.Min(FrameCodec.MaxChunkSize, total))
                    throw new IOException("Получен неполный файл");
                await output.WriteAsync(first.Payload, ct);
                long done = first.Payload.Length;
                progress?.Report((done, total));

                var nextOffset = done;
                while (done < total)
                {
                    while (window.Count < PipelineDepth && nextOffset < total)
                    {
                        window.Enqueue(ReadChunkAsync(remotePath, nextOffset, ct));
                        nextOffset += FrameCodec.MaxChunkSize;
                    }

                    var reply = await window.Dequeue();
                    if (reply.Payload.IsEmpty)
                    {
                        throw new EndOfStreamException("Файл изменился во время скачивания");
                    }

                    if (reply.Response.FileLength != total || reply.Payload.Length != Math.Min(FrameCodec.MaxChunkSize, total - done))
                        throw new IOException("Файл изменился во время скачивания");

                    await output.WriteAsync(reply.Payload, ct);
                    done += reply.Payload.Length;
                    progress?.Report((done, total));
                }
                if (done != total) throw new EndOfStreamException("Получен неполный файл");
            }
            ct.ThrowIfCancellationRequested();
            File.Move(staging, localPath, overwrite: true);
        }
        catch
        {
            TryDeleteLocal(staging);
            throw;
        }
        finally
        {
            try { await Task.WhenAll(window); } catch { /* Observe remaining pipeline completions. */ }
        }
    }

    /// <summary>Загружает файл на клиентскую машину. Прогресс — число подтверждённых байт.</summary>
    public async Task UploadFileAsync(string localPath, string remotePath, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        var staging = Path.Combine(Path.GetDirectoryName(remotePath) ?? ".", ".bridgerdp-" + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            await UploadContentAsync(localPath, staging, progress, ct);
            await RequestAsync(new Request { Op = Operation.Rename, Path = staging, NewPath = remotePath, Overwrite = true }, ct: ct);
        }
        catch
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await DeleteAsync(staging, cleanup.Token);
            }
            catch { /* A broken RDP channel may leave a .part file on the target. */ }
            throw;
        }
    }

    private async Task UploadContentAsync(string localPath, string remotePath, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        await using var input = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var total = input.Length;

        // Первый чанк (offset 0) создаёт/обрезает файл — отправляем его отдельно, даже если файл пустой.
        var chunk = await ReadLocalChunkAsync(input, ct);
        await WriteChunkAsync(remotePath, 0, chunk, ct);
        long offset = chunk.Length;
        var done = offset;
        progress?.Report((done, total));

        var window = new Queue<(Task Task, int Size)>();
        try
        {
            while ((chunk = await ReadLocalChunkAsync(input, ct)).Length > 0)
            {
                window.Enqueue((WriteChunkAsync(remotePath, offset, chunk, ct), chunk.Length));
                offset += chunk.Length;

                if (window.Count >= PipelineDepth)
                {
                    done += await CompleteAsync(window.Dequeue());
                    progress?.Report((done, total));
                }
            }

            while (window.Count > 0)
            {
                done += await CompleteAsync(window.Dequeue());
                progress?.Report((done, total));
            }
        }
        finally
        {
            try { await Task.WhenAll(window.Select(item => item.Task)); } catch { /* Observe remaining requests before cleanup. */ }
        }

        static async Task<int> CompleteAsync((Task Task, int Size) item)
        {
            await item.Task;
            return item.Size;
        }
    }

    private Task<Reply> ReadChunkAsync(string path, long offset, CancellationToken ct) =>
        RequestAsync(new Request { Op = Operation.ReadFile, Path = path, Offset = offset, Length = FrameCodec.MaxChunkSize }, ct: ct);

    private Task<Reply> WriteChunkAsync(string path, long offset, byte[] data, CancellationToken ct) =>
        RequestAsync(new Request { Op = Operation.WriteFile, Path = path, Offset = offset }, data, ct);

    private static async Task<byte[]> ReadLocalChunkAsync(FileStream input, CancellationToken ct)
    {
        var buffer = new byte[FrameCodec.MaxChunkSize];
        var read = await input.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
        return read == buffer.Length ? buffer : buffer[..read];
    }

    private static void TryDeleteLocal(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Недокачанный файл не удалился — не критично.
        }
    }

    // ---- Транспорт -------------------------------------------------------------

    public async Task<Reply> RequestAsync(Request request, ReadOnlyMemory<byte> payload = default, CancellationToken ct = default)
    {
        request.Id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.Id] = tcs;

        if (Volatile.Read(ref _closed) != 0 && _pending.TryRemove(request.Id, out _))
        {
            throw new IOException("Канал закрыт");
        }

        using var registration = ct.Register(() =>
        {
            if (_pending.TryRemove(request.Id, out var pending))
            {
                pending.TrySetCanceled(ct);
            }
        });

        var frame = FrameCodec.Encode(request, ProtocolJson.Default.Request, payload.Span);
        try
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                if (Volatile.Read(ref _closed) != 0) throw new IOException("Канал закрыт");
                ct.ThrowIfCancellationRequested();
                // A canceled operation must not leave a partial length-prefixed pipe frame.
                await _channel.SendAsync(frame, _cts.Token);
            }
            finally { _writeLock.Release(); }
        }
        catch
        {
            _pending.TryRemove(request.Id, out _);
            throw;
        }

        var reply = await tcs.Task;
        if (reply.Response.Error is { } error)
        {
            throw new RemoteException(error);
        }

        return reply;
    }

    private async Task ReadLoopAsync()
    {
        Exception? error = null;
        try
        {
            while (await _channel.ReceiveAsync(_cts.Token) is { } message)
            {
                var (response, payload) = FrameCodec.Decode(message, ProtocolJson.Default.Response);
                if (_pending.TryRemove(response.Id, out var tcs))
                {
                    tcs.TrySetResult(new Reply(response, payload));
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            error = ex;
        }

        Interlocked.Exchange(ref _closed, 1);
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var tcs))
            {
                tcs.TrySetException(new IOException("Канал закрыт", error));
            }
        }

        if (!_cts.IsCancellationRequested)
        {
            Disconnected?.Invoke(error);
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _closed, 1);
        _cts.Cancel();
        _channel.Dispose();
    }
}
