using System.Buffers;
using System.Globalization;
using System.Numerics;
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

    // Any decimal length frames as its value, or as long.MaxValue when a long cannot hold it. The
    // expectation comes from BigInteger, independent of the helpers' arithmetic, and the cases crowd
    // around long.MaxValue, where the saturation check decides.
    [Fact]
    public void AnyDecimalLength_FramesAsItsValue_SaturatingAtLongMax()
    {
        var rng = new Random(20261102);
        for (int i = 0; i < 3000; i++)
        {
            string value = RandomDecimal(rng);
            var exact = BigInteger.Parse(value, CultureInfo.InvariantCulture);
            long expected = exact > long.MaxValue ? long.MaxValue : (long)exact;

            _request.Clear();
            Assert.True(ParseFlexible($"POST / HTTP/1.1\r\nHost: x\r\nContent-Length: {value}\r\n\r\n"));

            var framing = BodyFramingDetector.DetectBodyFraming(_request);
            Assert.True(expected == 0
                    ? framing.Framing == BodyFraming.None
                    : framing.Framing == BodyFraming.ContentLength && framing.ContentLength == expected,
                $"Content-Length: {value} framed as {framing.Framing} {framing.ContentLength}, expected {expected}");
            Assert.True(ContentLengthBodyReader.ParseContentLength(_request) == expected,
                $"Content-Length: {value} read as {ContentLengthBodyReader.ParseContentLength(_request)}, expected {expected}");
        }
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

    private static string RandomDecimal(Random rng) => rng.Next(4) switch
    {
        0 => Digits(rng, rng.Next(1, 26)),                                            // 1 to 25 digits
        1 => "92233720368547758" + Digits(rng, 2),                                    // 19 digits at the boundary
        2 => (new BigInteger(long.MaxValue) + rng.Next(-1000, 1001)).ToString(CultureInfo.InvariantCulture),
        _ => new string('0', rng.Next(1, 6)) + Digits(rng, rng.Next(1, 21)),          // leading zeros
    };

    private static string Digits(Random rng, int count)
    {
        var digits = new char[count];
        for (int i = 0; i < count; i++)
            digits[i] = (char)('0' + rng.Next(10));
        return new string(digits);
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
