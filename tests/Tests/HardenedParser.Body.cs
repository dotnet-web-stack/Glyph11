using System.Text;
using Glyph11.Parser;
using Glyph11.Parser.Hardened;
using Glyph11.Validation;

namespace Tests;

public partial class HardenedParserTests
{
    // ================================================================
    // DetectBodyFraming
    // ================================================================

    [Fact]
    public void DetectBodyFraming_Chunked()
    {
        ParseHeader("POST / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n");
        var result = BodyFramingDetector.DetectBodyFraming(_request);

        Assert.Equal(BodyFraming.Chunked, result.Framing);
    }

    [Fact]
    public void DetectBodyFraming_ContentLength()
    {
        ParseHeader("POST / HTTP/1.1\r\nContent-Length: 42\r\n\r\n");
        var result = BodyFramingDetector.DetectBodyFraming(_request);

        Assert.Equal(BodyFraming.ContentLength, result.Framing);
        Assert.Equal(42, result.ContentLength);
    }

    [Fact]
    public void DetectBodyFraming_None()
    {
        ParseHeader("GET / HTTP/1.1\r\nHost: localhost\r\n\r\n");
        var result = BodyFramingDetector.DetectBodyFraming(_request);

        Assert.Equal(BodyFraming.None, result.Framing);
    }

    [Fact]
    public void DetectBodyFraming_ContentLengthZero()
    {
        ParseHeader("POST / HTTP/1.1\r\nContent-Length: 0\r\n\r\n");
        var result = BodyFramingDetector.DetectBodyFraming(_request);

        Assert.Equal(BodyFraming.None, result.Framing);
    }

    [Fact]
    public void DetectBodyFraming_ChunkedTakesPriority()
    {
        ParseHeader("POST / HTTP/1.1\r\nTransfer-Encoding: chunked\r\nContent-Length: 100\r\n\r\n");
        var result = BodyFramingDetector.DetectBodyFraming(_request);

        Assert.Equal(BodyFraming.Chunked, result.Framing);
    }

    // ================================================================
    // Helpers
    // ================================================================

    /// <summary>
    /// Parses only the header into _request, for use before calling DetectBodyFraming.
    /// </summary>
    private void ParseHeader(string raw)
    {
        _request.Clear();
        var bytes = Encoding.ASCII.GetBytes(raw);
        ReadOnlyMemory<byte> rom = bytes;
        HardenedParser.TryExtractFullHeaderROM(ref rom, _request, Defaults, out _);
    }
}
