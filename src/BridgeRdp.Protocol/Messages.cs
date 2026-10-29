namespace BridgeRdp.Protocol;

// Файловый протокол "запрос-ответ" поверх DVC в обоих направлениях.
// Ответ несёт тот же Id, что и запрос.

public enum Operation
{
    /// <summary>Список дисков. Ответ: <see cref="Response.Drives"/>.</summary>
    ListDrives,

    /// <summary>Содержимое папки <see cref="Request.Path"/>. Ответ: <see cref="Response.Entries"/>.</summary>
    ListDirectory,

    /// <summary>
    /// Чтение до <see cref="Request.Length"/> байт с <see cref="Request.Offset"/>.
    /// Данные — в payload ответа, полный размер файла — в <see cref="Response.FileLength"/>.
    /// </summary>
    ReadFile,

    /// <summary>
    /// Запись payload запроса по <see cref="Request.Offset"/>.
    /// Offset == 0 создаёт файл заново (или обрезает существующий).
    /// </summary>
    WriteFile,

    CreateDirectory,

    /// <summary>Удаляет файл или папку (рекурсивно).</summary>
    Delete,

    /// <summary>Переименовывает/перемещает <see cref="Request.Path"/> в <see cref="Request.NewPath"/>.</summary>
    Rename,

    /// <summary>Release handles for Path, or all session handles.</summary>
    CloseFile,
    CloseFiles,
}

public sealed class Request
{
    public int Id { get; set; }
    public Operation Op { get; set; }
    public string? Path { get; set; }
    public string? NewPath { get; set; }
    public long Offset { get; set; }
    public int Length { get; set; }
    public bool Overwrite { get; set; }
}

public sealed class Response
{
    public int Id { get; set; }

    /// <summary>Текст ошибки, если операция не выполнилась.</summary>
    public string? Error { get; set; }

    public List<DriveEntry>? Drives { get; set; }
    public List<FileEntry>? Entries { get; set; }
    public long FileLength { get; set; }
}

public sealed class DriveEntry
{
    public required string Name { get; set; }
    public string? Label { get; set; }
    public string? DriveType { get; set; }
    public bool IsReady { get; set; }
    public long TotalSize { get; set; }
    public long FreeSpace { get; set; }
}

public sealed class FileEntry
{
    public required string Name { get; set; }
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime LastWriteTime { get; set; }
    public FileAttributes Attributes { get; set; }
}
