using Glyph11.Parser.Hardened;
using Glyph11.Protocol;

namespace Tests;

public class KeyValueListTests
{
    [Fact]
    public void Indexer_ThrowsOnOutOfRange()
    {
        using var list = new KeyValueList();
        Assert.Throws<ArgumentOutOfRangeException>(() => list[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => list[-1]);
    }

    [Fact]
    public void GrowsWhenCapacityExceeded()
    {
        // Parse a request with many headers to force growth
        using var request = new BinaryRequest();
        var limits = ParserLimits.Default;

        var raw = "GET / HTTP/1.1\r\n";
        for (int i = 0; i < 20; i++)
            raw += $"X-H{i}: val{i}\r\n";
        raw += "\r\n";

        ReadOnlyMemory<byte> rom = System.Text.Encoding.ASCII.GetBytes(raw);
        HardenedParser.TryExtractFullHeaderROM(ref rom, request, in limits, out _);

        Assert.Equal(20, request.Headers.Count);

        for (int i = 0; i < 20; i++)
        {
            var kv = request.Headers[i];
            Assert.False(kv.Key.IsEmpty);
            Assert.False(kv.Value.IsEmpty);
        }
    }

    [Fact]
    public void AsSpan_ReturnsPopulatedEntries()
    {
        using var request = new BinaryRequest();
        var limits = ParserLimits.Default;

        ReadOnlyMemory<byte> rom = "GET / HTTP/1.1\r\nA: 1\r\nB: 2\r\n\r\n"u8.ToArray();
        HardenedParser.TryExtractFullHeaderROM(ref rom, request, in limits, out _);

        var span = request.Headers.AsSpan();
        Assert.Equal(2, span.Length);
    }

    [Fact]
    public void Dispose_CanBeCalledTwice()
    {
        var request = new BinaryRequest();
        var limits = ParserLimits.Default;

        ReadOnlyMemory<byte> rom = "GET / HTTP/1.1\r\nA: 1\r\n\r\n"u8.ToArray();
        HardenedParser.TryExtractFullHeaderROM(ref rom, request, in limits, out _);

        request.Dispose();
        var ex = Record.Exception(() => request.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void Constructor_ClampsNonPositiveCapacity()
    {
        using var list = new KeyValueList(initialCapacity: 0);
        // Should not throw — capacity is clamped to 1
        Assert.Equal(0, list.Count);
    }
}
