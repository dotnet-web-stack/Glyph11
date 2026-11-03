using System.Buffers;
using System.Text;
using Glyph11;
using Glyph11.Parser;
using Glyph11.Parser.UltraHardened;
using Glyph11.Protocol;
using Glyph11.Utils;

namespace Tests;

/// <summary>
/// The status code carried by a bad HTTP-version: a version that matches the grammar
/// (<c>"HTTP/" DIGIT "." DIGIT</c>) but is not one the parser implements is 505; bytes that are not
/// an HTTP-version at all are a malformed message, 400. The rule is the same on the request line
/// and the status line.
/// </summary>
public class HttpVersionStatusTests : IDisposable
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

    public static TheoryData<string, bool> Unsupported =>
        BothSegmentations("HTTP/2.0", "HTTP/1.2", "HTTP/0.9", "HTTP/3.0");

    public static TheoryData<string, bool> Malformed =>
        BothSegmentations("http/1.1", "HTTP/1.x", "HTTP1.1", "HTTP/11", "HTTP/1.1x", "HTTP/1.", "HTTP/");

    public static TheoryData<string, bool> Implemented =>
        BothSegmentations("HTTP/1.0", "HTTP/1.1");

    // ================================================================
    // Request line
    // ================================================================

    [Theory]
    [MemberData(nameof(Unsupported))]
    public void Request_WellFormedUnsupportedVersion_Throws505(string version, bool multi)
    {
        var ex = Assert.Throws<HttpParseException>(
            () => ParseRequest($"GET / {version}\r\nHost: x\r\n\r\n", multi));

        Assert.Equal(505, ex.StatusCode);
        Assert.False(ex.IsLimitViolation);
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Request_MalformedVersion_Throws400(string version, bool multi)
    {
        var ex = Assert.Throws<HttpParseException>(
            () => ParseRequest($"GET / {version}\r\nHost: x\r\n\r\n", multi));

        Assert.Equal(400, ex.StatusCode);
        Assert.False(ex.IsLimitViolation);
    }

    [Theory]
    [MemberData(nameof(Implemented))]
    public void Request_ImplementedVersion_Parses(string version, bool multi)
    {
        Assert.True(ParseRequest($"GET / {version}\r\nHost: x\r\n\r\n", multi));
        AssertAscii.Equal(version, _request.Version);
    }

    // ================================================================
    // Status line
    // ================================================================

    [Theory]
    [MemberData(nameof(Unsupported))]
    public void Response_WellFormedUnsupportedVersion_Throws505(string version, bool multi)
    {
        var ex = Assert.Throws<HttpParseException>(
            () => ParseResponse($"{version} 200 OK\r\nContent-Length: 0\r\n\r\n", multi));

        Assert.Equal(505, ex.StatusCode);
        Assert.False(ex.IsLimitViolation);
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Response_MalformedVersion_Throws400(string version, bool multi)
    {
        var ex = Assert.Throws<HttpParseException>(
            () => ParseResponse($"{version} 200 OK\r\nContent-Length: 0\r\n\r\n", multi));

        Assert.Equal(400, ex.StatusCode);
        Assert.False(ex.IsLimitViolation);
    }

    [Theory]
    [MemberData(nameof(Implemented))]
    public void Response_ImplementedVersion_Parses(string version, bool multi)
    {
        Assert.True(ParseResponse($"{version} 200 OK\r\nContent-Length: 0\r\n\r\n", multi));
        AssertAscii.Equal(version, _response.Version);
    }

    // ================================================================
    // IsLimitViolation means 431 and nothing else
    // ================================================================

    [Fact]
    public void IsLimitViolation_OnlyFor431()
    {
        Assert.False(new HttpParseException("structural").IsLimitViolation);
        Assert.False(new HttpParseException("unsupported version", 505).IsLimitViolation);
        Assert.True(new HttpParseException("too large", 431).IsLimitViolation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Request_LimitBreach_IsLimitViolation(bool multi)
    {
        var limits = Defaults with { MaxTotalHeaderBytes = 20 };

        var ex = Assert.Throws<HttpParseException>(
            () => ParseRequest("GET / HTTP/1.1\r\nHost: localhost\r\n\r\n", multi, limits));

        Assert.Equal(431, ex.StatusCode);
        Assert.True(ex.IsLimitViolation);
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static TheoryData<string, bool> BothSegmentations(params string[] versions)
    {
        var data = new TheoryData<string, bool>();
        foreach (var v in versions)
        {
            data.Add(v, false);
            data.Add(v, true);
        }
        return data;
    }

    private bool ParseRequest(string raw, bool multiSegment)
        => ParseRequest(raw, multiSegment, Defaults);

    private bool ParseRequest(string raw, bool multiSegment, ParserLimits limits)
    {
        var bytes = Encoding.ASCII.GetBytes(raw);

        if (multiSegment)
        {
            var seq = SplitIntoSegments(bytes);
            return UltraHardenedParser.TryExtractFullHeaderValidated(ref seq, _request, in limits, out _);
        }

        ReadOnlyMemory<byte> rom = bytes;
        return UltraHardenedParser.TryExtractFullHeaderROM(ref rom, _request, in limits, out _);
    }

    private bool ParseResponse(string raw, bool multiSegment)
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
