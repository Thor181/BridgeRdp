using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace BridgeRdp.Protocol;

/// <summary>
/// Одно DVC-сообщение = один кадр:
/// [int32 LE — длина JSON-заголовка][JSON-заголовок (UTF-8)][бинарный payload до конца сообщения].
/// </summary>
public static class FrameCodec
{
    /// <summary>Максимальный размер payload в одном кадре (чанк при передаче файлов).</summary>
    public const int MaxChunkSize = 60 * 1024;

    private const int HeaderLengthSize = 4;

    public static byte[] Encode<T>(T header, JsonTypeInfo<T> typeInfo, ReadOnlySpan<byte> payload = default)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(header, typeInfo);
        var frame = new byte[HeaderLengthSize + json.Length + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
        json.CopyTo(frame, HeaderLengthSize);
        payload.CopyTo(frame.AsSpan(HeaderLengthSize + json.Length));
        return frame;
    }

    public static (T Header, ReadOnlyMemory<byte> Payload) Decode<T>(ReadOnlyMemory<byte> frame, JsonTypeInfo<T> typeInfo)
    {
        if (frame.Length < HeaderLengthSize)
        {
            throw new InvalidDataException("Frame is too short");
        }

        var jsonLength = BinaryPrimitives.ReadInt32LittleEndian(frame.Span);
        if (jsonLength < 0 || jsonLength > frame.Length - HeaderLengthSize)
        {
            throw new InvalidDataException($"Invalid header length: {jsonLength}");
        }

        var header = JsonSerializer.Deserialize(frame.Span.Slice(HeaderLengthSize, jsonLength), typeInfo)
            ?? throw new InvalidDataException("Empty header");
        return (header, frame[(HeaderLengthSize + jsonLength)..]);
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(Request))]
[JsonSerializable(typeof(Response))]
public sealed partial class ProtocolJson : JsonSerializerContext;
