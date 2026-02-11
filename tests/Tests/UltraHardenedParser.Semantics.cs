using Glyph11;
using Glyph11.Parser.Hardened;
using Glyph11.Parser.UltraHardened;

namespace Tests;

public partial class UltraHardenedParserTests
{
    // ================================================================
    // CONNECT method rejection — RFC 9110 §9.3.6
    // ================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_ConnectMethod(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("CONNECT host:443 HTTP/1.1\r\nHost: host:443\r\n\r\n", multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepts_GetMethod_NotConnect(bool multi)
    {
        var (ok, _) = Parse("GET / HTTP/1.1\r\nHost: x\r\n\r\n", multi);
        Assert.True(ok);
    }

    // ================================================================
    // Asterisk-form — RFC 9112 §3.2.4
    // ================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_AsteriskFormWithGet(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET * HTTP/1.1\r\nHost: x\r\n\r\n", multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_AsteriskFormWithPost(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("POST * HTTP/1.1\r\nHost: x\r\n\r\n", multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepts_AsteriskFormWithOptions(bool multi)
    {
        var (ok, _) = Parse("OPTIONS * HTTP/1.1\r\nHost: x\r\n\r\n", multi);
        Assert.True(ok);
        AssertAscii.Equal("*", _request.Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepts_NormalPathNotAsterisk(bool multi)
    {
        var (ok, _) = Parse("GET / HTTP/1.1\r\nHost: x\r\n\r\n", multi);
        Assert.True(ok);
    }

    // ================================================================
    // Path semantics — fragment (#)
    // ================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_FragmentInPath(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET /path#frag HTTP/1.1\r\nHost: x\r\n\r\n", multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepts_PathWithoutFragment(bool multi)
    {
        var (ok, _) = Parse("GET /path HTTP/1.1\r\nHost: x\r\n\r\n", multi);
        Assert.True(ok);
    }

    // ================================================================
    // Path semantics — backslash
    // ================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_BackslashInPath(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET /api\\..\\etc HTTP/1.1\r\nHost: x\r\n\r\n", multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepts_ForwardSlashesOnly(bool multi)
    {
        var (ok, _) = Parse("GET /api/users HTTP/1.1\r\nHost: x\r\n\r\n", multi);
        Assert.True(ok);
    }

    // ================================================================
    // Path semantics — double encoding (%25)
    // ================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_DoubleEncodingInPath(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET /api/%252e%252e HTTP/1.1\r\nHost: x\r\n\r\n", multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepts_SingleEncodedPath(bool multi)
    {
        var (ok, _) = Parse("GET /api/%2e%2e HTTP/1.1\r\nHost: x\r\n\r\n", multi);
        Assert.True(ok);
    }

    // ================================================================
    // Path semantics — encoded null byte (%00)
    // ================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_EncodedNullByteInPath(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET /file.txt%00.jpg HTTP/1.1\r\nHost: x\r\n\r\n", multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepts_NoEncodedNullByte(bool multi)
    {
        var (ok, _) = Parse("GET /file.txt HTTP/1.1\r\nHost: x\r\n\r\n", multi);
        Assert.True(ok);
    }

    // ================================================================
    // Path semantics — dot segments
    // ================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_DotSegment_ParentTraversal(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET /api/../etc/passwd HTTP/1.1\r\nHost: x\r\n\r\n", multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_DotSegment_CurrentDir(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET /api/./users HTTP/1.1\r\nHost: x\r\n\r\n", multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_DotSegment_TrailingDotDot(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET /api/.. HTTP/1.1\r\nHost: x\r\n\r\n", multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_DotSegment_TrailingDot(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET /api/. HTTP/1.1\r\nHost: x\r\n\r\n", multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepts_DotsInFilename(bool multi)
    {
        var (ok, _) = Parse("GET /api/file.tar.gz HTTP/1.1\r\nHost: x\r\n\r\n", multi);
        Assert.True(ok);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepts_DotSuffix(bool multi)
    {
        var (ok, _) = Parse("GET /api/test... HTTP/1.1\r\nHost: x\r\n\r\n", multi);
        Assert.True(ok);
    }

    // ================================================================
    // Path semantics — overlong UTF-8 (rejected by IsValidRequestTarget first)
    // ================================================================

    [Fact]
    public void Throws_OverlongUtf8_C0()
    {
        // 0xC0 0xAF: overlong encoding of '/' — rejected at request-target validation
        var header = "GET "u8.ToArray();
        var path = new byte[] { 0x2F, 0xC0, 0xAF };
        var tail = " HTTP/1.1\r\nHost: x\r\n\r\n"u8.ToArray();
        var all = new byte[header.Length + path.Length + tail.Length];
        header.CopyTo(all, 0);
        path.CopyTo(all, header.Length);
        tail.CopyTo(all, header.Length + path.Length);

        ReadOnlyMemory<byte> rom = all;
        Assert.Throws<HttpParseException>(
            () => UltraHardenedParser.TryExtractFullHeaderROM(ref rom, _request, Defaults, out _));
    }

    [Fact]
    public void Throws_OverlongUtf8_C1()
    {
        var header = "GET "u8.ToArray();
        var path = new byte[] { 0x2F, 0xC1, 0x9C };
        var tail = " HTTP/1.1\r\nHost: x\r\n\r\n"u8.ToArray();
        var all = new byte[header.Length + path.Length + tail.Length];
        header.CopyTo(all, 0);
        path.CopyTo(all, header.Length);
        tail.CopyTo(all, header.Length + path.Length);

        ReadOnlyMemory<byte> rom = all;
        Assert.Throws<HttpParseException>(
            () => UltraHardenedParser.TryExtractFullHeaderROM(ref rom, _request, Defaults, out _));
    }

    // ================================================================
    // Host header — exactly one required (RFC 9112 §3.2)
    // ================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_MissingHostHeader(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET / HTTP/1.1\r\nAccept: */*\r\n\r\n", multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_NoHeaders_MissingHost(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET / HTTP/1.1\r\n\r\n", multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_DuplicateHostHeaders(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET / HTTP/1.1\r\nHost: a.com\r\nHost: b.com\r\n\r\n", multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepts_ExactlyOneHost(bool multi)
    {
        var (ok, _) = Parse("GET / HTTP/1.1\r\nHost: example.com\r\n\r\n", multi);
        Assert.True(ok);
    }

    // ================================================================
    // Host header format — RFC 9110 §7.2
    // ================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_HostWithUserinfo(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET / HTTP/1.1\r\nHost: user@example.com\r\n\r\n", multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_HostWithPath(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET / HTTP/1.1\r\nHost: example.com/path\r\n\r\n", multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepts_HostWithPort(bool multi)
    {
        var (ok, _) = Parse("GET / HTTP/1.1\r\nHost: example.com:8080\r\n\r\n", multi);
        Assert.True(ok);
    }

    // ================================================================
    // Content-Length format — RFC 9110 §8.6
    // ================================================================

    [Theory]
    [InlineData("42", false), InlineData("42", true)]
    [InlineData("0", false), InlineData("0", true)]
    [InlineData("123456789", false), InlineData("123456789", true)]
    public void Accepts_ValidContentLength(string value, bool multi)
    {
        var (ok, _) = Parse($"GET / HTTP/1.1\r\nHost: x\r\nContent-Length: {value}\r\n\r\n", multi);
        Assert.True(ok);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_NonDigitContentLength(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET / HTTP/1.1\r\nHost: x\r\nContent-Length: abc\r\n\r\n", multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_NegativeContentLength(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET / HTTP/1.1\r\nHost: x\r\nContent-Length: -1\r\n\r\n", multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_EmptyContentLength(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET / HTTP/1.1\r\nHost: x\r\nContent-Length:\r\n\r\n", multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_ContentLengthWithSpaces(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET / HTTP/1.1\r\nHost: x\r\nContent-Length: 1 2\r\n\r\n", multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_ContentLengthTrailingComma(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET / HTTP/1.1\r\nHost: x\r\nContent-Length: 42,\r\n\r\n", multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_ContentLengthCommaOnly(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET / HTTP/1.1\r\nHost: x\r\nContent-Length: ,\r\n\r\n", multi));
    }

    // ================================================================
    // Content-Length leading zeros
    // ================================================================

    [Theory]
    [InlineData("007", false), InlineData("007", true)]
    [InlineData("00", false), InlineData("00", true)]
    [InlineData("01", false), InlineData("01", true)]
    [InlineData("0200", false), InlineData("0200", true)]
    public void Throws_ContentLengthWithLeadingZeros(string value, bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse($"GET / HTTP/1.1\r\nHost: x\r\nContent-Length: {value}\r\n\r\n", multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepts_SingleZeroContentLength(bool multi)
    {
        var (ok, _) = Parse("GET / HTTP/1.1\r\nHost: x\r\nContent-Length: 0\r\n\r\n", multi);
        Assert.True(ok);
    }

    // ================================================================
    // Content-Length comma-separated conflicts — RFC 9112 §6.2
    // ================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepts_CommaSeparatedCL_Same(bool multi)
    {
        var (ok, _) = Parse("GET / HTTP/1.1\r\nHost: x\r\nContent-Length: 42, 42\r\n\r\n", multi);
        Assert.True(ok);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_CommaSeparatedCL_Different(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET / HTTP/1.1\r\nHost: x\r\nContent-Length: 42, 0\r\n\r\n", multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepts_CommaSeparatedCL_Zeros(bool multi)
    {
        var (ok, _) = Parse("GET / HTTP/1.1\r\nHost: x\r\nContent-Length: 0, 0\r\n\r\n", multi);
        Assert.True(ok);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_CommaSeparatedCL_LeadingZeroInSecond(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET / HTTP/1.1\r\nHost: x\r\nContent-Length: 42, 042\r\n\r\n", multi));
    }

    // ================================================================
    // Conflicting Content-Length headers — RFC 9110 §8.6
    // ================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepts_DuplicateSameCL(bool multi)
    {
        var raw =
            "GET / HTTP/1.1\r\n" +
            "Host: x\r\n" +
            "Content-Length: 10\r\n" +
            "Content-Length: 10\r\n" +
            "\r\n";
        var (ok, _) = Parse(raw, multi);
        Assert.True(ok);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_ConflictingCLHeaders(bool multi)
    {
        var raw =
            "GET / HTTP/1.1\r\n" +
            "Host: x\r\n" +
            "Content-Length: 10\r\n" +
            "Content-Length: 20\r\n" +
            "\r\n";
        Assert.Throws<HttpParseException>(() => Parse(raw, multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_ConflictingCLHeaders_CaseInsensitive(bool multi)
    {
        var raw =
            "GET / HTTP/1.1\r\n" +
            "Host: x\r\n" +
            "content-length: 10\r\n" +
            "Content-Length: 20\r\n" +
            "\r\n";
        Assert.Throws<HttpParseException>(() => Parse(raw, multi));
    }

    // ================================================================
    // Transfer-Encoding validation — RFC 9112 §6.1
    // ================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepts_ValidTE_Chunked(bool multi)
    {
        var (ok, _) = Parse("GET / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n", multi);
        Assert.True(ok);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepts_ValidTE_ChunkedCaseInsensitive(bool multi)
    {
        var (ok, _) = Parse("GET / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: Chunked\r\n\r\n", multi);
        Assert.True(ok);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepts_ValidTE_ChunkedWithOWS(bool multi)
    {
        var (ok, _) = Parse("GET / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding:  chunked \r\n\r\n", multi);
        Assert.True(ok);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_InvalidTE_Obfuscated(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: xchunked\r\n\r\n", multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_InvalidTE_Quoted(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: \"chunked\"\r\n\r\n", multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_InvalidTE_Identity(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: identity\r\n\r\n", multi));
    }

    // ================================================================
    // Transfer-Encoding + Content-Length — RFC 9112 §6.1
    // ================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_BothTEandCL(bool multi)
    {
        var raw =
            "GET / HTTP/1.1\r\n" +
            "Host: x\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "Content-Length: 10\r\n" +
            "\r\n";
        Assert.Throws<HttpParseException>(() => Parse(raw, multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_BothTEandCL_ReverseOrder(bool multi)
    {
        var raw =
            "GET / HTTP/1.1\r\n" +
            "Host: x\r\n" +
            "Content-Length: 10\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "\r\n";
        Assert.Throws<HttpParseException>(() => Parse(raw, multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_BothTEandCL_CaseInsensitive(bool multi)
    {
        var raw =
            "GET / HTTP/1.1\r\n" +
            "Host: x\r\n" +
            "transfer-encoding: chunked\r\n" +
            "content-length: 10\r\n" +
            "\r\n";
        Assert.Throws<HttpParseException>(() => Parse(raw, multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepts_OnlyCL_NoTE(bool multi)
    {
        var (ok, _) = Parse("GET / HTTP/1.1\r\nHost: x\r\nContent-Length: 10\r\n\r\n", multi);
        Assert.True(ok);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepts_OnlyTE_NoCL(bool multi)
    {
        var (ok, _) = Parse("GET / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n", multi);
        Assert.True(ok);
    }

    // ================================================================
    // Content-Length header name case insensitivity
    // ================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accepts_MixedCaseCL(bool multi)
    {
        var (ok, _) = Parse("GET / HTTP/1.1\r\nHost: x\r\ncontent-LENGTH: 10\r\n\r\n", multi);
        Assert.True(ok);
        AssertHeader(_request.Headers, 1, "content-LENGTH", "10");
    }

    // ================================================================
    // Structural validation carried over (smoke tests)
    // ================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_ObsFoldWithSpace(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET / HTTP/1.1\r\nHost: x\r\n continued\r\n\r\n", multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_SpaceBeforeColon(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET / HTTP/1.1\r\nHost : localhost\r\n\r\n", multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_DoubleSpaceAfterMethod(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET  / HTTP/1.1\r\nHost: x\r\n\r\n", multi));
    }

    [Theory]
    [InlineData("HTTP/2.0", false), InlineData("HTTP/2.0", true)]
    [InlineData("http/1.1", false), InlineData("http/1.1", true)]
    public void Throws_InvalidHttpVersion(string version, bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse($"GET / {version}\r\nHost: x\r\n\r\n", multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_HeaderNameWithControlChar(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET / HTTP/1.1\r\nHost: x\r\nBad\x00Name: val\r\n\r\n", multi));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Throws_HeaderValueWithNullByte(bool multi)
    {
        Assert.Throws<HttpParseException>(
            () => Parse("GET / HTTP/1.1\r\nHost: x\r\nKey: val\x00ue\r\n\r\n", multi));
    }
}
