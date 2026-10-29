using BridgeRdp.Protocol;

namespace BridgeRdp.Files;

/// <summary>
/// Р’С‹РїРѕР»РЅСЏРµС‚ С„Р°Р№Р»РѕРІС‹Рµ РѕРїРµСЂР°С†РёРё РЅР° РєР»РёРµРЅС‚СЃРєРѕР№ РјР°С€РёРЅРµ. РћС€РёР±РєРё РїСЂРѕР±СЂР°СЃС‹РІР°СЋС‚СЃСЏ РёСЃРєР»СЋС‡РµРЅРёСЏРјРё вЂ”
/// <see cref="ClientChannel"/> РїСЂРµРІСЂР°С‰Р°РµС‚ РёС… РІ <see cref="Response.Error"/>.
/// </summary>
public static class FileSystemService
{
    public static (Response Response, byte[]? Payload) Execute(Request request, ReadOnlyMemory<byte> payload)
    {
        using var session = new FileSystemSession();
        return session.Execute(request, payload);
    }
}

/// <summary>Open handles belong to one sequential file-agent session.</summary>
public sealed class FileSystemSession : IDisposable
{
    private readonly Dictionary<(string Path, bool Write), Microsoft.Win32.SafeHandles.SafeFileHandle> _handles = new();

    private Microsoft.Win32.SafeHandles.SafeFileHandle Open(string path, bool write, long offset)
    {
        path = Path.GetFullPath(path);
        var key = (path, write);
        if (offset == 0 && _handles.Remove(key, out var previous)) previous.Dispose();
        if (_handles.TryGetValue(key, out var handle)) return handle;
        if (_handles.Count >= 64) throw new IOException("Too many open file transfers");
        handle = File.OpenHandle(path, write ? (offset == 0 ? FileMode.Create : FileMode.Open) : FileMode.Open,
            write ? FileAccess.Write : FileAccess.Read, write ? FileShare.Read : FileShare.ReadWrite | FileShare.Delete);
        _handles.Add(key, handle);
        return handle;
    }

    private Response Close(string path)
    {
        path = Path.GetFullPath(path);
        foreach (var key in _handles.Keys.Where(k => string.Equals(k.Path, path, StringComparison.OrdinalIgnoreCase)).ToArray())
            if (_handles.Remove(key, out var handle)) handle.Dispose();
        return new Response();
    }

    public void Dispose()
    {
        foreach (var handle in _handles.Values) handle.Dispose();
        _handles.Clear();
    }

    private Response CloseAll() { Dispose(); return new Response(); }

    private static readonly EnumerationOptions ListOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0, // РїРѕРєР°Р·С‹РІР°РµРј Рё СЃРєСЂС‹С‚С‹Рµ, UI СЂРёСЃСѓРµС‚ РёС… Р±Р»РµРґРЅРµРµ
    };

    public (Response Response, byte[]? Payload) Execute(Request request, ReadOnlyMemory<byte> payload) =>
        request.Op switch
        {
            Operation.CloseFile => (Close(RequirePath(request)), null),
            Operation.CloseFiles => (CloseAll(), null),
            Operation.ListDrives => (ListDrives(), null),
            Operation.ListDirectory => (ListDirectory(RequirePath(request)), null),
            Operation.ReadFile => ReadFile(RequirePath(request), request.Offset, request.Length),
            Operation.WriteFile => (WriteFile(RequirePath(request), request.Offset, payload.Span), null),
            Operation.CreateDirectory => (CreateDirectory(RequirePath(request)), null),
            Operation.Delete => (Delete(RequirePath(request)), null),
            Operation.Rename => (Rename(RequirePath(request), request.NewPath ?? throw new ArgumentException("NewPath is required"), request.Overwrite), null),
            _ => throw new NotSupportedException($"Unknown operation: {request.Op}"),
        };

    private static string RequirePath(Request request) =>
        string.IsNullOrEmpty(request.Path) ? throw new ArgumentException("Path is required") : request.Path;

    private static Response ListDrives()
    {
        var drives = new List<DriveEntry>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            var entry = new DriveEntry { Name = drive.Name, DriveType = drive.DriveType.ToString() };
            try
            {
                if (drive.IsReady)
                {
                    entry.IsReady = true;
                    entry.Label = drive.VolumeLabel;
                    entry.TotalSize = drive.TotalSize;
                    entry.FreeSpace = drive.AvailableFreeSpace;
                }
            }
            catch (IOException)
            {
                // Р”РёСЃРє РѕС‚РІР°Р»РёР»СЃСЏ РјРµР¶РґСѓ РїСЂРѕРІРµСЂРєРѕР№ Рё С‡С‚РµРЅРёРµРј вЂ” РїРѕРєР°Р·С‹РІР°РµРј РєР°Рє РЅРµРіРѕС‚РѕРІС‹Р№.
            }

            drives.Add(entry);
        }

        return new Response { Drives = drives };
    }

    private static Response ListDirectory(string path)
    {
        var entries = new DirectoryInfo(path)
            .EnumerateFileSystemInfos("*", ListOptions)
            .Select(info => new FileEntry
            {
                Name = info.Name,
                IsDirectory = info is DirectoryInfo,
                Size = info is FileInfo file ? file.Length : 0,
                LastWriteTime = info.LastWriteTime,
                Attributes = info.Attributes,
            })
            .ToList();

        return new Response { Entries = entries };
    }

    private (Response, byte[]) ReadFile(string path, long offset, int length)
    {
        if (offset < 0 || length < 0 || length > FrameCodec.MaxChunkSize) throw new ArgumentOutOfRangeException(nameof(offset));
        var handle = Open(path, false, offset);
        var fileLength = RandomAccess.GetLength(handle);

        var toRead = (int)Math.Clamp(fileLength - offset, 0, Math.Min(length, FrameCodec.MaxChunkSize));
        var buffer = new byte[toRead];
        var read = 0;
        while (read < toRead)
        {
            var n = RandomAccess.Read(handle, buffer.AsSpan(read), offset + read);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        return (new Response { FileLength = fileLength }, read == toRead ? buffer : buffer[..read]);
    }

    private Response WriteFile(string path, long offset, ReadOnlySpan<byte> data)
    {
        if (offset < 0 || data.Length > FrameCodec.MaxChunkSize) throw new ArgumentOutOfRangeException(nameof(offset));
        var handle = Open(path, true, offset);
        RandomAccess.Write(handle, data, offset);
        return new Response();
    }

    private static Response CreateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return new Response();
    }

    private static Response Delete(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
        else
        {
            throw new FileNotFoundException($"Not found: {path}");
        }

        return new Response();
    }

    private static Response Rename(string path, string newPath, bool overwrite)
    {
        if (Directory.Exists(path))
        {
            Directory.Move(path, newPath);
        }
        else
        {
            File.Move(path, newPath, overwrite);
        }

        return new Response();
    }
}
