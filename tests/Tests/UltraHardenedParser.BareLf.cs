using System.Buffers;
using System.Text;
using Glyph11;
using Glyph11.Parser;
using Glyph11.Parser.UltraHardened;
using Glyph11.Protocol;
using Glyph11.Utils;

namespace Tests;

/// <summary>
/// Bare LF line endings — RFC 9112 §2.2 (issue #59).
/// <para>
/// By default a message is never reported incomplete because of a bare LF: once a bare LF has
/// arrived no later byte can make the message valid, so it is rejected instead of waited on.
/// With <see cref="ParserLimits.AllowBareLf"/> a message that uses bare LF throughout is parsed,
/// and a message that mixes CRLF and bare LF is still rejected.
/// </para>
/// </summary>
public class UltraHardenedParserBareLfTests : IDisposable
{
    private readonly BinaryRequest _request = new();
    private readonly BinaryResponse _response = new();

    private static readonly ParserLimits Strict = ParserLimits.Default;
    private static readonly ParserLimits Lenient = ParserLimits.Default with { AllowBareLf = true };

    public void Dispose()
    {
        _request.Dispose();
        _response.Dispose();
        GC.SuppressFinalize(this);
    }

    // ================================================================
    // Default (strict): the table from the issue — requests
    // ================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Strict_CrlfThroughout_Parses(bool multi)
    {
        var (ok, bytesRead) = ParseRequest("GET / HTTP/1.1\r\nHost: x\r\n\r\n", multi, Strict);

        Assert.True(ok);
        Assert.Equal("GET / HTTP/1.1\r\nHost: x\r\n\r\n".Length - 1, bytesRead);
    }

    [Theory]
    [InlineData("GET / HTTP/1.1\nHost: x\r\n\r\n", false)]
    [InlineData("GET / HTTP/1.1\nHost: x\r\n\r\n", true)]
    [InlineData("GET / HTTP/1.1\r\nHost: x\r\nA: b\nc\r\n\r\n", false)]
    [InlineData("GET / HTTP/1.1\r\nHost: x\r\nA: b\nc\r\n\r\n", true)]
    public void Strict_CrlfBlockWithOneBareLf_Throws(string raw, bool multi)
    {
        Assert.Throws<HttpParseException>(() => ParseRequest(raw, multi, Strict));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Strict_BareLfThroughout_ThrowsInsteadOfIncomplete(bool multi)
    {
        var ex = Assert.Throws<HttpParseException>(
            () => ParseRequest("GET / HTTP/1.1\nHost: x\n\n", multi, Strict));

        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("Bare LF", ex.Message);
    }

    [Theory]
    [InlineData("GET / HTTP/1.1\nHo", false)]                 // LF-only prefix
    [InlineData("GET / HTTP/1.1\nHo", true)]
    [InlineData("GET / HTTP/1.1\r\nHost: x\n", false)]       // CRLF so far, then a bare LF
    [InlineData("GET / HTTP/1.1\r\nHost: x\n", true)]
    [InlineData("\nGET / HTTP/1.1\r\n", false)]               // bare LF as the very first byte
    [InlineData("\nGET / HTTP/1.1\r\n", true)]
    public void Strict_IncompleteWithBareLf_ThrowsRightAway(string raw, bool multi)
    {
        Assert.Throws<HttpParseException>(() => ParseRequest(raw, multi, Strict));
    }

    [Theory]
    [InlineData("GET / HTTP/1.1\r\nHost: x\r\n", false)]
    [InlineData("GET / HTTP/1.1\r\nHost: x\r\n", true)]
    [InlineData("GET / HTTP/1.1\r\nHost: x\r\n\r", false)]   // terminator cut after its CR
    [InlineData("GET / HTTP/1.1\r\nHost: x\r\n\r", true)]
    [InlineData("GET / HTTP/1.1\r", false)]                  // CR whose LF has not arrived
    [InlineData("GET / HTTP/1.1\r", true)]
    public void Strict_IncompleteCrlfPrefix_StillReturnsFalse(string raw, bool multi)
    {
        var (ok, bytesRead) = ParseRequest(raw, multi, Strict);

        Assert.False(ok);
        Assert.Equal(-1, bytesRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Strict_BareLfInBodyAfterCompleteHeader_IsNotTheHeadersBusiness(bool multi)
    {
        const string header = "POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 3\r\n\r\n";
        var (ok, bytesRead) = ParseRequest(header + "a\nb", multi, Strict);

        Assert.True(ok);
        Assert.Equal(header.Length - 1, bytesRead);
    }

    // ================================================================
    // Default (strict): the table from the issue — responses
    // ================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Strict_Response_CrlfThroughout_Parses(bool multi)
    {
        var (ok, _) = ParseResponse("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nhi", multi, Strict);

        Assert.True(ok);
        Assert.Equal(200, _response.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Strict_Response_CrlfBlockWithOneBareLf_Throws(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => ParseResponse("HTTP/1.1 200 OK\r\nContent-Length: 2\nX: y\r\n\r\nhi", multi, Strict));
    }

    [Theory]
    [InlineData("HTTP/1.1 200 OK\nContent-Length: 2\n\nhi", false)]
    [InlineData("HTTP/1.1 200 OK\nContent-Length: 2\n\nhi", true)]
    [InlineData("HTTP/1.1 200 OK\nContent-Le", false)]
    [InlineData("HTTP/1.1 200 OK\nContent-Le", true)]
    public void Strict_Response_BareLf_ThrowsInsteadOfIncomplete(string raw, bool multi)
    {
        var ex = Assert.Throws<HttpParseException>(() => ParseResponse(raw, multi, Strict));
        Assert.Equal(400, ex.StatusCode);
    }

    // ================================================================
    // Segment boundaries: the byte before an LF can be in the previous segment
    // ================================================================

    [Fact]
    public void Strict_CrlfSplitAcrossSegments_IsNotABareLf()
    {
        var (ok, bytesRead) = ParseRequestSegments(Strict, "GET / HTTP/1.1\r", "\nHost: x\r\n");

        Assert.False(ok);
        Assert.Equal(-1, bytesRead);
    }

    [Fact]
    public void Strict_CrlfSplitAcrossAnEmptySegment_IsNotABareLf()
    {
        var (ok, _) = ParseRequestSegments(Strict, "GET / HTTP/1.1\r", "", "\nHost: x\r\n");
        Assert.False(ok);
    }

    [Fact]
    public void Strict_BareLfOpeningASegment_Throws()
    {
        Assert.Throws<HttpParseException>(
            () => ParseRequestSegments(Strict, "GET / HTTP/1.1", "\nHost: x"));
    }

    [Fact]
    public void Strict_BareLfClosingASegment_Throws()
    {
        Assert.Throws<HttpParseException>(
            () => ParseRequestSegments(Strict, "GET / HTTP/1.1\r\nHost: x\n", "Accept: */*"));
    }

    [Fact]
    public void Strict_CompleteCrlfMessageSplitInsideItsTerminator_Parses()
    {
        var (ok, bytesRead) = ParseRequestSegments(Strict, "GET / HTTP/1.1\r\nHost: x\r", "\n\r", "\n");

        Assert.True(ok);
        Assert.Equal("GET / HTTP/1.1\r\nHost: x\r\n\r\n".Length - 1, bytesRead);
    }

    // ================================================================
    // AllowBareLf: a bare-LF message is parsed
    // ================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lenient_BareLfRequest_Parses(bool multi)
    {
        const string raw = "GET /a?b=1 HTTP/1.1\nHost: x\nAccept: */*\n\n";
        var (ok, bytesRead) = ParseRequest(raw, multi, Lenient);

        Assert.True(ok);
        Assert.Equal(raw.Length - 1, bytesRead);
        AssertAscii.Equal("GET", _request.Method);
        AssertAscii.Equal("/a", _request.Path);
        AssertAscii.Equal("HTTP/1.1", _request.Version);
        Assert.Equal(1, _request.QueryParameters.Count);
        AssertAscii.Equal("b", _request.QueryParameters[0].Key);
        AssertAscii.Equal("1", _request.QueryParameters[0].Value);
        Assert.Equal(2, _request.Headers.Count);
        AssertAscii.Equal("Host", _request.Headers[0].Key);
        AssertAscii.Equal("x", _request.Headers[0].Value);
        AssertAscii.Equal("Accept", _request.Headers[1].Key);
        AssertAscii.Equal("*/*", _request.Headers[1].Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lenient_BareLfRequest_ConsumesOnlyTheHeaderBlock(bool multi)
    {
        const string header = "POST / HTTP/1.1\nHost: x\nContent-Length: 2\n\n";
        var (ok, bytesRead) = ParseRequest(header + "hi", multi, Lenient);

        Assert.True(ok);
        Assert.Equal(header.Length - 1, bytesRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lenient_BareLfMessage_BodyHoldingCrlfCrlf_DoesNotMoveTheHeaderEnd(bool multi)
    {
        const string header = "POST / HTTP/1.1\nHost: x\nContent-Length: 6\n\n";
        var (ok, bytesRead) = ParseRequest(header + "ab\r\n\r\n", multi, Lenient);

        Assert.True(ok);
        Assert.Equal(header.Length - 1, bytesRead);
        Assert.Equal(2, _request.Headers.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lenient_BareLfResponse_Parses(bool multi)
    {
        const string header = "HTTP/1.1 200 OK\nContent-Length: 2\n\n";
        var (ok, bytesRead) = ParseResponse(header + "hi", multi, Lenient);

        Assert.True(ok);
        Assert.Equal(header.Length - 1, bytesRead);
        Assert.Equal(200, _response.Status);
        AssertAscii.Equal("HTTP/1.1", _response.Version);
        AssertAscii.Equal("OK", _response.ReasonPhrase);
        Assert.Equal(1, _response.Headers.Count);
        AssertAscii.Equal("Content-Length", _response.Headers[0].Key);
        AssertAscii.Equal("2", _response.Headers[0].Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lenient_BareLfResponse_NoHeaders_Parses(bool multi)
    {
        var (ok, bytesRead) = ParseResponse("HTTP/1.1 204 No Content\n\n", multi, Lenient);

        Assert.True(ok);
        Assert.Equal("HTTP/1.1 204 No Content\n\n".Length - 1, bytesRead);
        Assert.Equal(204, _response.Status);
        Assert.Equal(0, _response.Headers.Count);
    }

    [Theory]
    [InlineData("GET / HTTP/1.1\nHo", false)]
    [InlineData("GET / HTTP/1.1\nHo", true)]
    [InlineData("GET / HTTP/1.1\nHost: x\n", false)]
    [InlineData("GET / HTTP/1.1\nHost: x\n", true)]
    [InlineData("GET / HTTP/1.1", false)]                    // no line terminator yet
    [InlineData("GET / HTTP/1.1", true)]
    public void Lenient_IncompleteBareLfPrefix_ReturnsFalse(string raw, bool multi)
    {
        var (ok, bytesRead) = ParseRequest(raw, multi, Lenient);

        Assert.False(ok);
        Assert.Equal(-1, bytesRead);
    }

    [Fact]
    public void Lenient_BareLfTerminatorSplitAcrossSegments_Parses()
    {
        var (ok, bytesRead) = ParseRequestSegments(Lenient, "GET / HTTP/1.1\nHost: x\n", "\nBODY");

        Assert.True(ok);
        Assert.Equal("GET / HTTP/1.1\nHost: x\n\n".Length - 1, bytesRead);
    }

    [Fact]
    public void Lenient_FirstLfOpeningASegment_AfterNonCr_IsABareLfMessage()
    {
        var (ok, _) = ParseRequestSegments(Lenient, "GET / HTTP/1.1", "\nHost: x\n\n");
        Assert.True(ok);
    }

    [Fact]
    public void Lenient_FirstLfOpeningASegment_AfterCr_IsACrlfMessage()
    {
        var (ok, _) = ParseRequestSegments(Lenient, "GET / HTTP/1.1\r", "\nHost: x\r\n\r\n");
        Assert.True(ok);
    }

    // ================================================================
    // AllowBareLf: mixed line endings are still rejected
    // ================================================================

    [Theory]
    [InlineData("GET / HTTP/1.1\nHost: x\r\n\r\n", false)]  // bare-LF start line, CRLF after
    [InlineData("GET / HTTP/1.1\nHost: x\r\n\r\n", true)]
    [InlineData("GET / HTTP/1.1\nHost: x\n\r\n", false)]    // bare-LF lines, CRLF empty line
    [InlineData("GET / HTTP/1.1\nHost: x\n\r\n", true)]
    [InlineData("GET / HTTP/1.1\nHost: x\r", false)]        // incomplete, but a CR already arrived
    [InlineData("GET / HTTP/1.1\nHost: x\r", true)]
    [InlineData("GET / HTTP/1.1\r\nHost: x\n\n", false)]    // CRLF start line, bare LF after
    [InlineData("GET / HTTP/1.1\r\nHost: x\n\n", true)]
    [InlineData("GET / HTTP/1.1\r\nHost: x\n", false)]      // the same, still incomplete
    [InlineData("GET / HTTP/1.1\r\nHost: x\n", true)]
    public void Lenient_MixedLineEndings_Throws(string raw, bool multi)
    {
        var ex = Assert.Throws<HttpParseException>(() => ParseRequest(raw, multi, Lenient));
        Assert.Equal(400, ex.StatusCode);
    }

    [Theory]
    [InlineData("HTTP/1.1 200 OK\nContent-Length: 0\r\n\r\n", false)]
    [InlineData("HTTP/1.1 200 OK\nContent-Length: 0\r\n\r\n", true)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 0\n\n", false)]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 0\n\n", true)]
    public void Lenient_Response_MixedLineEndings_Throws(string raw, bool multi)
    {
        Assert.Throws<HttpParseException>(() => ParseResponse(raw, multi, Lenient));
    }

    // ================================================================
    // AllowBareLf: CRLF messages and every other rule are unchanged
    // ================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lenient_CrlfMessage_ParsesExactlyAsStrict(bool multi)
    {
        const string raw = "GET /p?q=1 HTTP/1.1\r\nHost: x\r\nAccept: */*\r\n\r\n";

        var (okStrict, readStrict) = ParseRequest(raw, multi, Strict);
        var strictHeaders = _request.Headers.Count;
        _request.Clear();
        var (okLenient, readLenient) = ParseRequest(raw, multi, Lenient);

        Assert.True(okStrict);
        Assert.True(okLenient);
        Assert.Equal(readStrict, readLenient);
        Assert.Equal(strictHeaders, _request.Headers.Count);
        AssertAscii.Equal("/p", _request.Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lenient_IncompleteCrlfPrefix_ReturnsFalse(bool multi)
    {
        var (ok, _) = ParseRequest("GET / HTTP/1.1\r\nHost: x\r\n", multi, Lenient);
        Assert.False(ok);
    }

    [Theory]
    [InlineData("GET / HTTP/1.1\nHost: x\n folded\n\n")]     // obs-fold
    [InlineData("GET / HTTP/1.1\nHost : x\n\n")]             // whitespace before colon
    [InlineData("GET / HTTP/1.1\nHost: x\nHost: y\n\n")]     // two Host headers
    [InlineData("GET / HTTP/1.1\n\n")]                       // no Host header
    [InlineData("GET / HTTP/1.1\nHost: x\nTransfer-Encoding: chunked\nContent-Length: 1\n\n")]
    public void Lenient_BareLfMessage_KeepsEveryOtherRule(string raw)
    {
        Assert.Throws<HttpParseException>(() => ParseRequest(raw, multi: false, Lenient));
        _request.Clear();
        Assert.Throws<HttpParseException>(() => ParseRequest(raw, multi: true, Lenient));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lenient_BareLfHeaderBlockOverLimit_Throws431(bool multi)
    {
        const string raw = "GET / HTTP/1.1\nHost: x\nX-Pad: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n\n";
        var tight = Lenient with { MaxTotalHeaderBytes = raw.Length - 1 };

        var ex = Assert.Throws<HttpParseException>(() => ParseRequest(raw, multi, tight));
        Assert.Equal(431, ex.StatusCode);

        _request.Clear();
        var exact = Lenient with { MaxTotalHeaderBytes = raw.Length };
        var (ok, _) = ParseRequest(raw, multi, exact);
        Assert.True(ok);
    }

    [Fact]
    public void AllowBareLf_IsOffByDefault()
    {
        Assert.False(ParserLimits.Default.AllowBareLf);
        Assert.False(default(ParserLimits).AllowBareLf);
    }

    // ================================================================
    // Helpers
    // ================================================================

    private (bool ok, int bytesRead) ParseRequest(string raw, bool multi, ParserLimits limits)
    {
        var bytes = Encoding.ASCII.GetBytes(raw);
        if (multi)
        {
            var seq = ThreeSegments(bytes);
            return (UltraHardenedParser.TryExtractFullHeaderValidated(ref seq, _request, in limits, out var b), b);
        }

        ReadOnlyMemory<byte> rom = bytes;
        return (UltraHardenedParser.TryExtractFullHeaderROM(ref rom, _request, in limits, out var b2), b2);
    }

    private (bool ok, int bytesRead) ParseResponse(string raw, bool multi, ParserLimits limits)
    {
        var bytes = Encoding.ASCII.GetBytes(raw);
        if (multi)
        {
            var seq = ThreeSegments(bytes);
            return (UltraHardenedParser.TryExtractFullResponseHeaderValidated(ref seq, _response, in limits, out var b), b);
        }

        ReadOnlyMemory<byte> rom = bytes;
        return (UltraHardenedParser.TryExtractFullResponseHeaderROM(ref rom, _response, in limits, out var b2), b2);
    }

    private (bool ok, int bytesRead) ParseRequestSegments(ParserLimits limits, params string[] parts)
    {
        var seq = Segments(parts.Select(p => Encoding.ASCII.GetBytes(p)).ToArray());
        Assert.False(seq.IsSingleSegment);
        return (UltraHardenedParser.TryExtractFullHeaderValidated(ref seq, _request, in limits, out var b), b);
    }

    private static ReadOnlySequence<byte> ThreeSegments(byte[] data)
    {
        if (data.Length < 3)
            return Segments(data, []);

        int s1 = data.Length / 3, s2 = 2 * data.Length / 3;
        return Segments(data[..s1], data[s1..s2], data[s2..]);
    }

    private static ReadOnlySequence<byte> Segments(params byte[][] parts)
    {
        var first = new BufferSegment(parts[0]);
        var last = first;
        for (int i = 1; i < parts.Length; i++)
            last = last.Append(parts[i]);

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }
}
