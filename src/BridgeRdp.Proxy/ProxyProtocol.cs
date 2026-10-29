using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BridgeRdp.Proxy;

public interface IMessageTransport : IDisposable
{
    Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct);
    Task<byte[]?> ReceiveAsync(CancellationToken ct);
}

public enum Command { Connect, Read, Write, Shutdown, Close }
public sealed record Packet
{
    public int Id { get; init; }
    public int Connection { get; init; }
    public Command Command { get; init; }
    public string? Host { get; init; }
    public int Port { get; init; }
    public string? Error { get; init; }
    public byte Status { get; init; }
}

[JsonSerializable(typeof(Packet))]
internal partial class ProxyJson : JsonSerializerContext;

public static class ProxyProtocol
{
    public const string ChannelName = "BridgeRdp.Proxy";
    public const int ChunkSize = 32 * 1024;
    public const int MaxFrameSize = ChunkSize + 4096;
    public const int MaxConnections = 128;

    public static byte[] Encode(Packet packet, ReadOnlySpan<byte> data = default)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(packet, ProxyJson.Default.Packet);
        if (json.Length > 4092 || data.Length > ChunkSize)
            throw new InvalidDataException("Proxy frame exceeds limit");
        var result = new byte[4 + json.Length + data.Length];
        BinaryPrimitives.WriteInt32LittleEndian(result, json.Length);
        json.CopyTo(result, 4);
        data.CopyTo(result.AsSpan(4 + json.Length));
        return result;
    }

    public static (Packet Header, ReadOnlyMemory<byte> Data) Decode(byte[] frame)
    {
        if (frame.Length < 4 || frame.Length > MaxFrameSize)
            throw new InvalidDataException("Invalid proxy frame size");
        int size = BinaryPrimitives.ReadInt32LittleEndian(frame);
        if (size <= 0 || size > 4092 || size > frame.Length - 4 || frame.Length - 4 - size > ChunkSize)
            throw new InvalidDataException("Invalid proxy header size");
        var packet = JsonSerializer.Deserialize(frame.AsSpan(4, size), ProxyJson.Default.Packet)
            ?? throw new InvalidDataException("Missing proxy header");
        return (packet, frame.AsMemory(4 + size));
    }
}
