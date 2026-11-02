using System.Buffers;
using System.Text;
using Glyph11;
using Glyph11.Parser;
using Glyph11.Parser.FlexibleParser;
using Glyph11.Parser.UltraHardened;
using Glyph11.Protocol;
using Glyph11.Utils;
using Glyph11.Validation;

namespace Tests;

/// <summary>
/// BodyFramingDetector and ContentLengthBodyReader must read the same Content-Length from the same
/// message. With several Content-Length fields one used to take the last and the other the first:
/// one layer framing by 10 and the other by 5 is the textbook Content-Length desync. The hardened
/// parser rejects conflicting fields itself; FlexibleParser does not, so these tests build their
/// requests with it - the helpers are the only check such a request meets.
/// </summary>
public class ContentLengthDuplicatesTests : IDisposable
{
    private readonly BinaryRequest _request = new();
    private readonly BinaryResponse _response = new();
    private static readonly ParserLimits Defaults = ParserLimits.Default;

    public void Dispose()
    {
        _request.Dispose();
        _response.Dispose();
        GC.SuppressFinalize(this);
    }

    // ================================================================
    // Conflicts: both helpers refuse, whichever field comes first
    // ================================================================

    [Theory]
    [InlineData("Content-Length: 5\r\nContent-Length: 10\r\n")]
    [InlineData("Content-Length: 10\r\nContent-Length: 5\r\n")]
    [InlineData("Content-Length: 5, 10\r\n")]
    [InlineData("Content-Length: 5\r\nContent-Length: 5, 10\r\n")]
    [InlineData("Content-Length: 5\r\nContent-Length: abc\r\n")]
    [InlineData("Content-Length: 5\r\nContent-Length:\r\n")]
    public void Conflict_BothHelpersThrow(string contentLengthFields)
    {
        ParseFlexible(contentLengthFields);

        var detected = Assert.Throws<HttpParseException>(() => BodyFramingDetector.DetectBodyFraming(_request));
        Assert.Equal(400, detected.StatusCode);
        Assert.Equal("Conflicting Content-Length headers.", detected.Message);

        var read = Assert.Throws<HttpParseException>(() => ContentLengthBodyReader.ParseContentLength(_request));
        Assert.Equal(400, read.StatusCode);
    }

    [Fact]
    public void Conflict_IsRefusedEvenWhenChunkedWouldWin()
    {
        // Transfer-Encoding overrides Content-Length, but a message carrying conflicting lengths
        // as well is a smuggling attempt whichever framing a recipient picks.
        ParseFlexible("Transfer-Encoding: chunked\r\nContent-Length: 5\r\nContent-Length: 10\r\n");

        Assert.Throws<HttpParseException>(() => BodyFramingDetector.DetectBodyFraming(_request));
    }

    // ================================================================
    // Agreement: identical duplicates are one length (RFC 9110 §8.6)
    // ================================================================

    [Theory]
    [InlineData("Content-Length: 5\r\nContent-Length: 5\r\n")]
    [InlineData("Content-Length: 5, 5\r\n")]
    [InlineData("Content-Length: 5\r\nContent-Length: 5, 5\r\n")]
    [InlineData("Content-Length: 5,\r\n")]
    [InlineData("Content-Length: 5\r\n")]
    public void Agreement_BothHelpersReadTheSameLength(string contentLengthFields)
    {
        ParseFlexible(contentLengthFields);

        var framing = BodyFramingDetector.DetectBodyFraming(_request);
        Assert.Equal(BodyFraming.ContentLength, framing.Framing);
        Assert.Equal(5, framing.ContentLength);
        Assert.Equal(5, ContentLengthBodyReader.ParseContentLength(_request));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Agreement_HardenedDuplicatesStillFrame(bool multi)
    {
        Assert.True(ParseHardened(
            "POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 5\r\nContent-Length: 5\r\n\r\n", multi));

        Assert.Equal(5, BodyFramingDetector.DetectBodyFraming(_request).ContentLength);
        Assert.Equal(5, ContentLengthBodyReader.ParseContentLength(_request));
    }

    [Theory]
    [InlineData("Content-Length: 5\r\nContent-Length: 5\r\n")]
    [InlineData("Content-Length: 5, 5\r\n")]
    public void Agreement_ResponseDuplicatesStillFrame(string contentLengthFields)
    {
        ReadOnlyMemory<byte> rom = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\n{contentLengthFields}\r\n");
        Assert.True(UltraHardenedParser.TryExtractFullResponseHeaderROM(ref rom, _response, in Defaults, out _));

        var framing = BodyFramingDetector.DetectResponseBodyFraming(_response, "GET"u8);
        Assert.Equal(BodyFraming.ContentLength, framing.Framing);
        Assert.Equal(5, framing.ContentLength);
    }

    // ================================================================
    // Unchanged: one field, or none
    // ================================================================

    [Theory]
    [InlineData("Content-Length: 0\r\n", 0)]
    [InlineData("Content-Length: abc\r\n", -1)]
    [InlineData("Content-Length: abc\r\nContent-Length: abc\r\n", -1)]
    [InlineData("", -1)]
    public void NoLength_IsNoBody(string contentLengthFields, long expected)
    {
        ParseFlexible(contentLengthFields);

        Assert.Equal(BodyFraming.None, BodyFramingDetector.DetectBodyFraming(_request).Framing);
        Assert.Equal(expected, ContentLengthBodyReader.ParseContentLength(_request));
    }

    // ================================================================
    // Helpers
    // ================================================================

    private void ParseFlexible(string contentLengthFields)
    {
        ReadOnlyMemory<byte> rom = Encoding.ASCII.GetBytes(
            $"POST / HTTP/1.1\r\nHost: x\r\n{contentLengthFields}\r\n");
        Assert.True(FlexibleParser.TryExtractFullHeaderReadOnlyMemory(ref rom, _request, out _));
    }

    private bool ParseHardened(string raw, bool multiSegment)
    {
        var bytes = Encoding.ASCII.GetBytes(raw);

        if (multiSegment)
        {
            var seq = SplitIntoSegments(bytes);
            return UltraHardenedParser.TryExtractFullHeaderValidated(ref seq, _request, in Defaults, out _);
        }

        ReadOnlyMemory<byte> rom = bytes;
        return UltraHardenedParser.TryExtractFullHeaderROM(ref rom, _request, in Defaults, out _);
    }

    private static ReadOnlySequence<byte> SplitIntoSegments(byte[] data)
    {
        int split1 = data.Length / 3;
        int split2 = 2 * data.Length / 3;

        var first = new BufferSegment(data.AsMemory(0, split1));
        var last = first.Append(data.AsMemory(split1, split2 - split1)).Append(data.AsMemory(split2));

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }
}
