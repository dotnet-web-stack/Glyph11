using System.Buffers;
using System.Text;
using Glyph11.Parser;
using Glyph11.Parser.FlexibleParser;
using Glyph11.Parser.UltraHardened;
using Glyph11.Protocol;
using Glyph11.Utils;
using Glyph11.Validation;

namespace Tests;

/// <summary>
/// The validator accepts Content-Length values up to ulong.MaxValue; the framing helpers hold the
/// length in a long. A value a long cannot hold must still frame as a (too large) length - framed
/// as "no body", its body would be read as the next message on the connection.
/// </summary>
public class ContentLengthOverflowTests : IDisposable
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

    [Theory]
    [InlineData("9223372036854775806", false), InlineData("9223372036854775806", true)]
    [InlineData("9223372036854775807", false), InlineData("9223372036854775807", true)]
    public void Request_LengthThatFitsIsExact(string value, bool multi)
    {
        Assert.True(ParseRequest($"POST / HTTP/1.1\r\nHost: x\r\nContent-Length: {value}\r\n\r\n", multi));

        var framing = BodyFramingDetector.DetectBodyFraming(_request);
        Assert.Equal(BodyFraming.ContentLength, framing.Framing);
        Assert.Equal(long.Parse(value), framing.ContentLength);
        Assert.Equal(long.Parse(value), ContentLengthBodyReader.ParseContentLength(_request));
    }

    [Theory]
    [InlineData("9223372036854775808", false), InlineData("9223372036854775808", true)]
    [InlineData("18446744073709551615", false), InlineData("18446744073709551615", true)]
    [InlineData("9223372036854775808, 9223372036854775808", false)]
    [InlineData("9223372036854775808, 9223372036854775808", true)]
    public void Request_LengthBeyondLongSaturates_NotNoBody(string value, bool multi)
    {
        // Accepted by the validator...
        Assert.True(ParseRequest($"POST / HTTP/1.1\r\nHost: x\r\nContent-Length: {value}\r\n\r\n", multi));

        // ...so it must be framed as a length, not as a request without a body.
        var framing = BodyFramingDetector.DetectBodyFraming(_request);
        Assert.Equal(BodyFraming.ContentLength, framing.Framing);
        Assert.Equal(long.MaxValue, framing.ContentLength);
        Assert.Equal(long.MaxValue, ContentLengthBodyReader.ParseContentLength(_request));
    }

    [Theory]
    [InlineData("9223372036854775808", false), InlineData("9223372036854775808", true)]
    [InlineData("18446744073709551615", false), InlineData("18446744073709551615", true)]
    public void Response_LengthBeyondLongSaturates_NotUntilClose(string value, bool multi)
    {
        Assert.True(ParseResponse($"HTTP/1.1 200 OK\r\nContent-Length: {value}\r\n\r\n", multi));

        var framing = BodyFramingDetector.DetectResponseBodyFraming(_response, "GET"u8);
        Assert.Equal(BodyFraming.ContentLength, framing.Framing);
        Assert.Equal(long.MaxValue, framing.ContentLength);
    }

    [Theory]
    [InlineData("18446744073709551616")]          // ulong.MaxValue + 1: the validator would refuse it
    [InlineData("1234567890123456789012345")]     // 25 digits
    public void UnvalidatedLengthBeyondLongSaturates(string value)
    {
        // FlexibleParser does not validate Content-Length, so the helpers see the raw digits.
        Assert.True(ParseFlexible($"POST / HTTP/1.1\r\nHost: x\r\nContent-Length: {value}\r\n\r\n"));

        var framing = BodyFramingDetector.DetectBodyFraming(_request);
        Assert.Equal(BodyFraming.ContentLength, framing.Framing);
        Assert.Equal(long.MaxValue, framing.ContentLength);
        Assert.Equal(long.MaxValue, ContentLengthBodyReader.ParseContentLength(_request));
    }

    [Theory]
    [InlineData("12x")]
    [InlineData("99999999999999999999x")]   // saturated, then a non-digit: still not a length
    public void NonDigitsAreStillNotALength(string value)
    {
        Assert.True(ParseFlexible($"POST / HTTP/1.1\r\nHost: x\r\nContent-Length: {value}\r\n\r\n"));

        Assert.Equal(BodyFraming.None, BodyFramingDetector.DetectBodyFraming(_request).Framing);
        Assert.Equal(-1, ContentLengthBodyReader.ParseContentLength(_request));
    }

    // ================================================================
    // Helpers
    // ================================================================

    private bool ParseRequest(string raw, bool multiSegment)
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

    private bool ParseResponse(string raw, bool multiSegment)
    {
        var bytes = Encoding.ASCII.GetBytes(raw);

        if (multiSegment)
        {
            var seq = SplitIntoSegments(bytes);
            return UltraHardenedParser.TryExtractFullResponseHeaderValidated(ref seq, _response, in Defaults, out _);
        }

        ReadOnlyMemory<byte> rom = bytes;
        return UltraHardenedParser.TryExtractFullResponseHeaderROM(ref rom, _response, in Defaults, out _);
    }

    private bool ParseFlexible(string raw)
    {
        ReadOnlyMemory<byte> rom = Encoding.ASCII.GetBytes(raw);
        return FlexibleParser.TryExtractFullHeaderReadOnlyMemory(ref rom, _request, out _);
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
