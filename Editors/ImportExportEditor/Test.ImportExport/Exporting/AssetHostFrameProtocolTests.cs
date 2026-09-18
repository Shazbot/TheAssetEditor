using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using WH3AssetHost;

namespace Test.ImportExport.Exporting;

public sealed class AssetHostFrameProtocolTests
{
    [Test]
    public async Task ReadFrameAsync_HandlesPartialHeaderAndPayloadReads()
    {
        var payload = Encoding.UTF8.GetBytes("{\"protocolVersion\":1}");
        var framed = Frame(payload);
        await using var stream = new ChunkedReadStream(framed, 1);

        var result = await NamedPipeFrameProtocol.ReadFrameAsync(stream);

        Assert.That(result, Is.EqualTo(payload));
    }

    [Test]
    public void ReadFrameAsync_RejectsZeroLengthFrame()
    {
        using var stream = new MemoryStream([0, 0, 0, 0]);

        var exception = Assert.ThrowsAsync<AssetHostFrameException>(
            async () => await NamedPipeFrameProtocol.ReadFrameAsync(stream));

        Assert.That(exception!.Code, Is.EqualTo("EmptyFrame"));
        Assert.That(exception.CanRespond, Is.True);
    }

    [Test]
    public void ReadFrameAsync_RejectsPayloadOverOneMiB()
    {
        var header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            header,
            (uint)AssetHostProtocol.MaxFramePayloadBytes + 1);
        using var stream = new MemoryStream(header);

        var exception = Assert.ThrowsAsync<AssetHostFrameException>(
            async () => await NamedPipeFrameProtocol.ReadFrameAsync(stream));

        Assert.That(exception!.Code, Is.EqualTo("FrameTooLarge"));
        Assert.That(exception.CanRespond, Is.True);
    }

    [Test]
    public async Task WriteAndReadFrameAsync_RoundTripsUtf8Json()
    {
        using var stream = new MemoryStream();
        var value = JsonSerializer.SerializeToElement(
            new { message = "čarobno", number = 7 },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        await NamedPipeFrameProtocol.WriteJsonFrameAsync(stream, value);
        stream.Position = 0;
        var payload = await NamedPipeFrameProtocol.ReadFrameAsync(stream);

        Assert.That(payload, Is.Not.Null);
        using var document = JsonDocument.Parse(payload!);
        Assert.That(document.RootElement.GetProperty("message").GetString(), Is.EqualTo("čarobno"));
        Assert.That(document.RootElement.GetProperty("number").GetInt32(), Is.EqualTo(7));
    }

    private static byte[] Frame(byte[] payload)
    {
        var framed = new byte[sizeof(uint) + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(framed, (uint)payload.Length);
        payload.CopyTo(framed, sizeof(uint));
        return framed;
    }

    private sealed class ChunkedReadStream(byte[] data, int chunkSize) : MemoryStream(data, writable: false)
    {
        public override int Read(Span<byte> buffer)
            => base.Read(buffer[..Math.Min(chunkSize, buffer.Length)]);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(chunkSize, buffer.Length)], cancellationToken);
    }
}
