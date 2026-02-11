using Glyph11;
using Glyph11.Parser;
using Glyph11.Parser.Hardened;
using Glyph11.Protocol;
using Glyph11.Validation;

namespace Tests;

/// <summary>
/// Additional RequestSemantics edge-case coverage.
/// Core semantic tests live in HardenedParser.Semantics.cs.
/// </summary>
public class RequestSemanticsTests : IDisposable
{
    private readonly BinaryRequest _request = new();
    private static readonly ParserLimits Defaults = ParserLimits.Default;

    public void Dispose()
    {
        _request.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ParseRom(string raw)
    {
        ReadOnlyMemory<byte> rom = System.Text.Encoding.ASCII.GetBytes(raw);
        HardenedParser.TryExtractFullHeaderROM(ref rom, _request, in Defaults, out _);
    }

    // ---- HasDotSegments: simple path without dots ----

    [Fact]
    public void DotSegments_SimplePath_ReturnsFalse()
    {
        ParseRom("GET /simple HTTP/1.1\r\n\r\n");
        Assert.False(RequestSemantics.HasDotSegments(_request));
    }

    // ---- Non-ASCII in request-target: now rejected by parser ----

    [Fact]
    public void OverlongUtf8_E0_3Byte_RejectedByParser()
    {
        var header = "GET "u8.ToArray();
        var path = new byte[] { 0x2F, 0xE0, 0x80, 0xAF }; // overlong 3-byte
        var tail = " HTTP/1.1\r\n\r\n"u8.ToArray();
        var all = new byte[header.Length + path.Length + tail.Length];
        header.CopyTo(all, 0);
        path.CopyTo(all, header.Length);
        tail.CopyTo(all, header.Length + path.Length);

        ReadOnlyMemory<byte> rom = all;
        Assert.Throws<HttpParseException>(
            () => HardenedParser.TryExtractFullHeaderROM(ref rom, _request, in Defaults, out _));
    }

    [Fact]
    public void OverlongUtf8_F0_4Byte_RejectedByParser()
    {
        var header = "GET "u8.ToArray();
        var path = new byte[] { 0x2F, 0xF0, 0x80, 0x80, 0xAF }; // overlong 4-byte
        var tail = " HTTP/1.1\r\n\r\n"u8.ToArray();
        var all = new byte[header.Length + path.Length + tail.Length];
        header.CopyTo(all, 0);
        path.CopyTo(all, header.Length);
        tail.CopyTo(all, header.Length + path.Length);

        ReadOnlyMemory<byte> rom = all;
        Assert.Throws<HttpParseException>(
            () => HardenedParser.TryExtractFullHeaderROM(ref rom, _request, in Defaults, out _));
    }

    [Fact]
    public void OverlongUtf8_C1_RejectedByParser()
    {
        var header = "GET "u8.ToArray();
        var path = new byte[] { 0x2F, 0xC1, 0xAF };
        var tail = " HTTP/1.1\r\n\r\n"u8.ToArray();
        var all = new byte[header.Length + path.Length + tail.Length];
        header.CopyTo(all, 0);
        path.CopyTo(all, header.Length);
        tail.CopyTo(all, header.Length + path.Length);

        ReadOnlyMemory<byte> rom = all;
        Assert.Throws<HttpParseException>(
            () => HardenedParser.TryExtractFullHeaderROM(ref rom, _request, in Defaults, out _));
    }

    // ---- HasInvalidContentLengthFormat: comma-separated with OWS ----

    [Fact]
    public void ContentLengthFormat_CommaSeparatedWithOWS_Valid()
    {
        ParseRom("GET / HTTP/1.1\r\nContent-Length: 42, 42\r\n\r\n");
        Assert.False(RequestSemantics.HasInvalidContentLengthFormat(_request));
    }

    [Fact]
    public void ContentLengthFormat_CommaSeparatedWithTab_Valid()
    {
        ParseRom("GET / HTTP/1.1\r\nContent-Length: 42,\t42\r\n\r\n");
        Assert.False(RequestSemantics.HasInvalidContentLengthFormat(_request));
    }

    // ---- HasContentLengthWithLeadingZeros: comma-separated segments ----

    [Fact]
    public void LeadingZeros_CommaSeparatedWithLeadingZero()
    {
        ParseRom("GET / HTTP/1.1\r\nContent-Length: 42, 042\r\n\r\n");
        Assert.True(RequestSemantics.HasContentLengthWithLeadingZeros(_request));
    }

    [Fact]
    public void LeadingZeros_CommaSeparatedNoLeadingZero()
    {
        ParseRom("GET / HTTP/1.1\r\nContent-Length: 42, 42\r\n\r\n");
        Assert.False(RequestSemantics.HasContentLengthWithLeadingZeros(_request));
    }

    // ---- HasConflictingCommaSeparatedContentLength: OWS trimming ----

    [Fact]
    public void CommaSeparatedCL_WithOWSAroundValues_Same()
    {
        ParseRom("GET / HTTP/1.1\r\nContent-Length:  42 , 42 \r\n\r\n");
        Assert.False(RequestSemantics.HasConflictingCommaSeparatedContentLength(_request));
    }

    // ---- HasDoubleEncoding: no match when path is too short ----

    [Fact]
    public void DoubleEncoding_ShortPath()
    {
        ParseRom("GET /a HTTP/1.1\r\n\r\n");
        Assert.False(RequestSemantics.HasDoubleEncoding(_request));
    }

    // ---- HasEncodedNullByte: no match when path is too short ----

    [Fact]
    public void EncodedNull_ShortPath()
    {
        ParseRom("GET /a HTTP/1.1\r\n\r\n");
        Assert.False(RequestSemantics.HasEncodedNullByte(_request));
    }

    // ---- HasInvalidTransferEncoding: with surrounding OWS ----

    [Fact]
    public void InvalidTE_ChunkedWithLeadingAndTrailingOWS()
    {
        ParseRom("GET / HTTP/1.1\r\nTransfer-Encoding:  chunked \r\n\r\n");
        Assert.False(RequestSemantics.HasInvalidTransferEncoding(_request));
    }

    [Fact]
    public void InvalidTE_ChunkedWithTabOWS()
    {
        ParseRom("GET / HTTP/1.1\r\nTransfer-Encoding:\tchunked\t\r\n\r\n");
        Assert.False(RequestSemantics.HasInvalidTransferEncoding(_request));
    }

    // ---- No headers at all: semantics on empty header list ----

    [Fact]
    public void NoHeaders_NoConflicts()
    {
        ParseRom("GET / HTTP/1.1\r\n\r\n");
        Assert.False(RequestSemantics.HasConflictingContentLength(_request));
        Assert.False(RequestSemantics.HasTransferEncodingWithContentLength(_request));
        Assert.False(RequestSemantics.HasInvalidContentLengthFormat(_request));
        Assert.False(RequestSemantics.HasContentLengthWithLeadingZeros(_request));
        Assert.False(RequestSemantics.HasConflictingCommaSeparatedContentLength(_request));
        Assert.False(RequestSemantics.HasInvalidTransferEncoding(_request));
    }
}
