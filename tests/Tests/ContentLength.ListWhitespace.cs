using System.Buffers;
using System.Text;
using Glyph11.Parser;
using Glyph11.Parser.UltraHardened;
using Glyph11.Protocol;
using Glyph11.Utils;
using Glyph11.Validation;

namespace Tests;

/// <summary>
/// A Content-Length list may carry OWS on either side of its commas, and the hardened parser
/// accepts all of these forms. The framing helpers must then read the same length the validator
/// accepted - a header framed as "no body" after validation leaves its body to be read as the next
/// message on the connection.
/// </summary>
public class ContentLengthListWhitespaceTests : IDisposable
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
    [InlineData("5", false), InlineData("5", true)]
    [InlineData("5,5", false), InlineData("5,5", true)]
    [InlineData("5, 5", false), InlineData("5, 5", true)]
    [InlineData("5 ,5", false), InlineData("5 ,5", true)]
    [InlineData("5\t, 5", false), InlineData("5\t, 5", true)]
    [InlineData("5 , 5 ", false), InlineData("5 , 5 ", true)]
    public void Request_IsFramedByTheValidatedLength(string value, bool multi)
    {
        Assert.True(ParseRequest($"POST / HTTP/1.1\r\nHost: x\r\nContent-Length: {value}\r\n\r\n", multi));

        var framing = BodyFramingDetector.DetectBodyFraming(_request);
        Assert.Equal(BodyFraming.ContentLength, framing.Framing);
        Assert.Equal(5, framing.ContentLength);
    }

    [Theory]
    [InlineData("5", false), InlineData("5", true)]
    [InlineData("5,5", false), InlineData("5,5", true)]
    [InlineData("5, 5", false), InlineData("5, 5", true)]
    [InlineData("5 ,5", false), InlineData("5 ,5", true)]
    [InlineData("5\t, 5", false), InlineData("5\t, 5", true)]
    [InlineData("5 , 5 ", false), InlineData("5 , 5 ", true)]
    public void Request_ContentLengthReaderAgrees(string value, bool multi)
    {
        Assert.True(ParseRequest($"POST / HTTP/1.1\r\nHost: x\r\nContent-Length: {value}\r\n\r\n", multi));

        Assert.Equal(5, ContentLengthBodyReader.ParseContentLength(_request));
    }

    [Theory]
    [InlineData("5 ,5", false), InlineData("5 ,5", true)]
    [InlineData("5\t, 5", false), InlineData("5\t, 5", true)]
    [InlineData("5 , 5 ", false), InlineData("5 , 5 ", true)]
    public void Response_IsFramedByTheValidatedLength_NotUntilClose(string value, bool multi)
    {
        // Framed as UntilClose, a client would read every later byte on the connection as this
        // response's body.
        Assert.True(ParseResponse($"HTTP/1.1 200 OK\r\nContent-Length: {value}\r\n\r\n", multi));

        var framing = BodyFramingDetector.DetectResponseBodyFraming(_response, "GET"u8);
        Assert.Equal(BodyFraming.ContentLength, framing.Framing);
        Assert.Equal(5, framing.ContentLength);
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

    private static ReadOnlySequence<byte> SplitIntoSegments(byte[] data)
    {
        int split1 = data.Length / 3;
        int split2 = 2 * data.Length / 3;

        var first = new BufferSegment(data.AsMemory(0, split1));
        var last = first.Append(data.AsMemory(split1, split2 - split1)).Append(data.AsMemory(split2));

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }
}
