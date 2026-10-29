using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using BridgeRdp.Protocol;
using BridgeRdp.Proxy;

namespace BridgeRdp.Files;

public static class FileBridge
{
    public const string ChannelName = "BridgeRdp.Files";
    public const int MaxMessageSize = 16 * 1024 * 1024;

    public static string PipeName(int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var user = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserDomainName + "\\" + Environment.UserName)))[..16];
        return $"BridgeRdp.Files.{user}.{port}";
    }

    public static async Task<IMessageTransport> ConnectAsync(int port, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", PipeName(port), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(5000, ct);
            return new StreamMessageTransport(pipe);
        }
        catch
        {
            pipe.Dispose();
            throw new IOException($"Нет файлового подключения для порта {port}. Запустите агент в RDP-сессии и проверьте регистрацию плагина.");
        }
    }
}

/// <summary>Length-prefixed messages over a byte stream; one reader and one writer.</summary>
public sealed class StreamMessageTransport(Stream stream) : IMessageTransport
{
    public async Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        if (message.Length <= 0 || message.Length > FileBridge.MaxMessageSize) throw new InvalidDataException("Invalid file message length");
        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, message.Length);
        await stream.WriteAsync(length, ct);
        await stream.WriteAsync(message, ct);
        await stream.FlushAsync(ct);
    }

    public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
    {
        var length = new byte[4];
        if (await stream.ReadAsync(length.AsMemory(0, 1), ct) == 0) return null;
        await stream.ReadExactlyAsync(length.AsMemory(1), ct);
        int count = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (count <= 0 || count > FileBridge.MaxMessageSize) throw new InvalidDataException("Invalid file message length");
        var message = new byte[count];
        await stream.ReadExactlyAsync(message, ct);
        return message;
    }

    public void Dispose() => stream.Dispose();
}

public static class FileAgent
{
    public static async Task RunAsync(IMessageTransport transport, CancellationToken ct)
    {
        using var session = new FileSystemSession();
        while (await transport.ReceiveAsync(ct) is { } message)
        {
            var (request, data) = FrameCodec.Decode(message, ProtocolJson.Default.Request);
            Response response;
            byte[]? payload = null;
            try { (response, payload) = session.Execute(request, data); }
            catch (Exception ex) { response = new Response { Error = ex.Message }; }
            response.Id = request.Id;
            var frame = FrameCodec.Encode(response, ProtocolJson.Default.Response, payload);
            if (frame.Length > FileBridge.MaxMessageSize)
                frame = FrameCodec.Encode(new Response { Id = request.Id, Error = "Слишком большой список файлов" }, ProtocolJson.Default.Response);
            await transport.SendAsync(frame, ct);
        }
    }
}
