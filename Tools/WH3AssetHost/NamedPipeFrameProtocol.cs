using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace WH3AssetHost;

public sealed class AssetHostFrameException : IOException
{
    public AssetHostFrameException(string code, string message, bool canRespond)
        : base(message)
    {
        Code = code;
        CanRespond = canRespond;
    }

    public string Code { get; }
    public bool CanRespond { get; }
}

/// <summary>
/// The pipe protocol is deliberately byte-mode and carries no delimiter:
/// unsigned little-endian payload length followed by UTF-8 JSON bytes.
/// </summary>
public static class NamedPipeFrameProtocol
{
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[sizeof(uint)];
        var headerBytes = await ReadExactlyOrEofAsync(stream, header, cancellationToken);
        if (headerBytes == 0)
            return null;
        if (headerBytes != header.Length)
        {
            throw new AssetHostFrameException(
                "TruncatedFrame",
                "The frame length header ended before four bytes were received.",
                canRespond: false);
        }

        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (payloadLength == 0)
        {
            throw new AssetHostFrameException(
                "EmptyFrame",
                "A frame payload must contain at least one byte.",
                canRespond: true);
        }
        if (payloadLength > AssetHostProtocol.MaxFramePayloadBytes)
        {
            throw new AssetHostFrameException(
                "FrameTooLarge",
                $"The frame payload exceeds the {AssetHostProtocol.MaxFramePayloadBytes}-byte limit.",
                canRespond: true);
        }

        var payload = new byte[(int)payloadLength];
        var payloadBytes = await ReadExactlyOrEofAsync(stream, payload, cancellationToken);
        if (payloadBytes != payload.Length)
        {
            throw new AssetHostFrameException(
                "TruncatedFrame",
                "The frame payload ended before the declared length was received.",
                canRespond: false);
        }
        return payload;
    }

    public static Task WriteJsonFrameAsync(
        Stream stream,
        AssetHostResponse value,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(value, AssetHostJsonContext.Default.AssetHostResponse);
        return WriteFrameAsync(stream, Encoding.UTF8.GetBytes(json), cancellationToken);
    }

    public static Task WriteJsonFrameAsync(
        Stream stream,
        AssetHostMissingSkeletonDecisionRequest value,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(
            value,
            AssetHostJsonContext.Default.AssetHostMissingSkeletonDecisionRequest);
        return WriteFrameAsync(stream, Encoding.UTF8.GetBytes(json), cancellationToken);
    }

    public static Task WriteJsonFrameAsync(
        Stream stream,
        JsonElement value,
        CancellationToken cancellationToken = default)
    {
        var json = value.GetRawText();
        return WriteFrameAsync(stream, Encoding.UTF8.GetBytes(json), cancellationToken);
    }

    public static async Task WriteFrameAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (payload.Length == 0)
            throw new ArgumentOutOfRangeException(nameof(payload), "A frame payload cannot be empty.");
        if (payload.Length > AssetHostProtocol.MaxFramePayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(payload), "The frame payload is too large.");

        var header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)payload.Length));
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<int> ReadExactlyOrEofAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }
}
