using System.Buffers;
using System.Text;
using Glyph11;
using Glyph11.Parser;
using Glyph11.Parser.UltraHardened;
using Glyph11.Protocol;
using Glyph11.Utils;

namespace Tests;

/// <summary>
/// The response parser accepts status codes 100-599 only (RFC 9110 §15): a three-digit code
/// outside that range is well-formed but is not a status.
/// </summary>
public class ResponseStatusRangeTests : IDisposable
{
    private readonly BinaryResponse _response = new();
    private static readonly ParserLimits Defaults = ParserLimits.Default;

    public void Dispose()
    {
        _response.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("100", false), InlineData("100", true)]
    [InlineData("200", false), InlineData("200", true)]
    [InlineData("599", false), InlineData("599", true)]
    public void Accepts_StatusInRange(string code, bool multi)
    {
        Assert.True(Parse($"HTTP/1.1 {code} Reason\r\nContent-Length: 0\r\n\r\n", multi));
        Assert.Equal(int.Parse(code), _response.Status);
        AssertAscii.Equal(code, _response.StatusCode);
    }

    [Theory]
    [InlineData("600", false), InlineData("600", true)]
    [InlineData("699", false), InlineData("699", true)]
    [InlineData("999", false), InlineData("999", true)]
    [InlineData("099", false), InlineData("099", true)]
    [InlineData("000", false), InlineData("000", true)]
    public void Throws_StatusOutOfRange(string code, bool multi)
    {
        var ex = Assert.Throws<HttpParseException>(
            () => Parse($"HTTP/1.1 {code} Reason\r\nContent-Length: 0\r\n\r\n", multi));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("Status code must be in the range 100-599.", ex.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_StatusOutOfRange_WithoutReasonPhrase(bool multi)
    {
        // "HTTP/1.1 600" with no SP and no phrase is the bare-code form the parser accepts for
        // valid codes; the range check must not depend on which form the line uses.
        Assert.Throws<HttpParseException>(() => Parse("HTTP/1.1 600\r\n\r\n", multi));
    }

    private bool Parse(string raw, bool multiSegment)
    {
        var bytes = Encoding.ASCII.GetBytes(raw);
        var limits = Defaults;

        if (multiSegment)
        {
            var seq = SplitIntoSegments(bytes);
            return UltraHardenedParser.TryExtractFullResponseHeaderValidated(ref seq, _response, in limits, out _);
        }

        ReadOnlyMemory<byte> rom = bytes;
        return UltraHardenedParser.TryExtractFullResponseHeaderROM(ref rom, _response, in limits, out _);
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
