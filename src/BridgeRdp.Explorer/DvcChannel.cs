using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static BridgeRdp.Explorer.Interop.WtsApi;

namespace BridgeRdp.Explorer;

/// <summary>
/// Серверная сторона DVC: открывает канал в текущей RDP-сессии и работает с ним как с потоком.
/// Чтение собирает сообщения из чанков (CHANNEL_PDU_HEADER), запись отправляет сообщение целиком.
/// </summary>
internal sealed class DvcChannel : IDisposable
{
    private const int ReadBufferSize = 64 * 1024;
    private const int MaxMessageSize = 16 * 1024 * 1024;

    private readonly IntPtr _wtsHandle;
    private readonly FileStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _readBuffer = new byte[ReadBufferSize];
    private bool _disposed;

    private DvcChannel(IntPtr wtsHandle, FileStream stream)
    {
        _wtsHandle = wtsHandle;
        _stream = stream;
    }

    /// <summary>
    /// Открывает канал. Блокирует, пока клиент не примет или не отклонит соединение.
    /// </summary>
    public static DvcChannel Open(string name)
    {
        var wtsHandle = WTSVirtualChannelOpenEx(WTS_CURRENT_SESSION, name, WTS_CHANNEL_OPTION_DYNAMIC);
        if (wtsHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"WTSVirtualChannelOpenEx('{name}') failed. Запущено ли внутри RDP-сессии и зарегистрирован ли плагин на клиенте?");
        }

        try
        {
            if (!WTSVirtualChannelQuery(wtsHandle, WTS_VIRTUAL_CLASS.WTSVirtualFileHandle, out var buffer, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSVirtualChannelQuery failed");
            }

            IntPtr fileHandle;
            try
            {
                fileHandle = Marshal.ReadIntPtr(buffer);
            }
            finally
            {
                WTSFreeMemory(buffer);
            }

            // Хэндлом владеет канал: он закрывается вместе с WTSVirtualChannelClose.
            // Хэндл открыт в overlapped-режиме, поэтому isAsync: true.
            var safeHandle = new SafeFileHandle(fileHandle, ownsHandle: false);
            var stream = new FileStream(safeHandle, FileAccess.ReadWrite, bufferSize: 0, isAsync: true);
            return new DvcChannel(wtsHandle, stream);
        }
        catch
        {
            WTSVirtualChannelClose(wtsHandle);
            throw;
        }
    }

    /// <summary>Отправляет одно сообщение.</summary>
    public async Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _stream.WriteAsync(message, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Читает одно сообщение целиком. Возвращает null, если канал закрыт.
    /// Не вызывай параллельно из нескольких потоков.
    /// </summary>
    public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        byte[]? message = null;
        var offset = 0;

        while (true)
        {
            var read = await _stream.ReadAsync(_readBuffer, cancellationToken);
            if (read == 0)
            {
                if (message is not null) throw new EndOfStreamException("Channel closed inside a message");
                return null;
            }

            if (read < CHANNEL_PDU_HEADER_SIZE)
            {
                throw new InvalidDataException($"Chunk too short: {read} bytes");
            }

            var totalLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(_readBuffer.AsSpan(0, 4));
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(_readBuffer.AsSpan(4, 4));
            var chunk = _readBuffer.AsSpan(CHANNEL_PDU_HEADER_SIZE, read - CHANNEL_PDU_HEADER_SIZE);

            if ((flags & CHANNEL_FLAG_FIRST) != 0)
            {
                if (message is not null || totalLength <= 0 || totalLength > MaxMessageSize)
                    throw new InvalidDataException("Invalid message size or overlapping messages");
                message = new byte[totalLength];
                offset = 0;
            }

            if (message is null || totalLength != message.Length || chunk.Length > message.Length - offset)
            {
                throw new InvalidDataException("Unexpected chunk sequence");
            }

            chunk.CopyTo(message.AsSpan(offset));
            offset += chunk.Length;

            if ((flags & CHANNEL_FLAG_LAST) != 0)
            {
                if (offset != message.Length) throw new InvalidDataException("Truncated DVC message");
                return message;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
        WTSVirtualChannelClose(_wtsHandle);
        _writeLock.Dispose();
    }
}
