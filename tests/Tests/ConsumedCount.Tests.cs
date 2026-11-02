using System.Buffers;
using System.Text;
using Glyph11.Parser;
using Glyph11.Parser.FlexibleParser;
using Glyph11.Parser.UltraHardened;
using Glyph11.Protocol;
using Glyph11.Utils;

namespace Tests;

/// <summary>
/// Pins what <c>consumed</c> means on the TryParse*Header entry points: the length of the header
/// block including its terminating CRLFCRLF, so whatever follows (a body, or the next pipelined
/// message) starts exactly at <c>consumed</c>. Also pins the obsolete TryExtract* entry points to
/// their legacy value, <c>consumed - 1</c> (and -1 when incomplete), so callers that still add 1 keep
/// framing correctly until they migrate.
/// </summary>
public class ConsumedCountTests
{
    /// <summary>How the input reaches the parser.</summary>
    public enum Shape
    {
        /// <summary>A contiguous <see cref="ReadOnlyMemory{T}"/>.</summary>
        Memory,
        /// <summary>A single-segment <see cref="ReadOnlySequence{T}"/>.</summary>
        SingleSegment,
        /// <summary>Three segments.</summary>
        ThreeSegments,
        /// <summary>One byte per segment: every boundary, including inside the CRLFCRLF.</summary>
        ByteSegments,
    }

    private static readonly Shape[] Shapes =
        [Shape.Memory, Shape.SingleSegment, Shape.ThreeSegments, Shape.ByteSegments];

    // (header block, what follows it in the same buffer)
    private static readonly (string Head, string Tail)[] RequestCorpus =
    [
        ("GET / HTTP/1.1\r\nHost: x\r\n\r\n", ""),
        ("GET /api/users?a=1&b=2 HTTP/1.1\r\nHost: example.com\r\nAccept: */*\r\n\r\n", ""),
        ("POST /submit HTTP/1.1\r\nHost: x\r\nContent-Length: 5\r\n\r\n", "hello"),
        ("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n", "5\r\nhello\r\n0\r\n\r\n"),
        ("OPTIONS * HTTP/1.1\r\nHost: x\r\n\r\n", ""),
        ("GET / HTTP/1.0\r\nHost: x\r\n\r\n", ""),
        // Pipelined: the next request must start exactly at consumed.
        ("GET /a HTTP/1.1\r\nHost: x\r\n\r\n", "GET /b HTTP/1.1\r\nHost: x\r\n\r\n"),
    ];

    private static readonly (string Head, string Tail)[] ResponseCorpus =
    [
        ("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\n", "hi"),
        ("HTTP/1.1 204 No Content\r\n\r\n", ""),
        ("HTTP/1.1 404 Not Found\r\nContent-Type: text/plain\r\nContent-Length: 9\r\n\r\n", "not found"),
        ("HTTP/1.0 200 OK\r\n\r\n", "body until close"),
        ("HTTP/1.1 200\r\nTransfer-Encoding: chunked\r\n\r\n", "2\r\nhi\r\n0\r\n\r\n"),
        // Pipelined: the next response must start exactly at consumed.
        ("HTTP/1.1 304 Not Modified\r\n\r\n", "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"),
    ];

    private static readonly string[] IncompleteRequests =
    [
        "",
        "GET / HTTP/1.1\r\nHost: x\r\n",
        "GET / HTTP/1.1\r\nHost: x\r\n\r",
    ];

    private static readonly string[] IncompleteResponses =
    [
        "",
        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n",
        "HTTP/1.1 200 OK\r\n\r",
    ];

    public static IEnumerable<object[]> Requests() =>
        from c in RequestCorpus from s in Shapes select new object[] { c.Head, c.Tail, s };

    public static IEnumerable<object[]> Responses() =>
        from c in ResponseCorpus from s in Shapes select new object[] { c.Head, c.Tail, s };

    public static IEnumerable<object[]> PartialRequests() =>
        from r in IncompleteRequests from s in Shapes select new object[] { r, s };

    public static IEnumerable<object[]> PartialResponses() =>
        from r in IncompleteResponses from s in Shapes select new object[] { r, s };

    // ================================================================
    // consumed is the header block length; what follows starts there
    // ================================================================

    [Theory]
    [MemberData(nameof(Requests))]
    public void UltraHardened_Request_ConsumedIsHeaderLength(string head, string rest, Shape shape)
    {
        bool ok = ParseRequest(Bytes(head + rest), shape, out int consumed, out string after);

        Assert.True(ok);
        Assert.Equal(head.Length, consumed);
        Assert.Equal(rest, after);
    }

    [Theory]
    [MemberData(nameof(Responses))]
    public void UltraHardened_Response_ConsumedIsHeaderLength(string head, string rest, Shape shape)
    {
        bool ok = ParseResponse(Bytes(head + rest), shape, out int consumed, out string after);

        Assert.True(ok);
        Assert.Equal(head.Length, consumed);
        Assert.Equal(rest, after);
    }

    [Theory]
    [MemberData(nameof(Requests))]
    public void Flexible_Request_ConsumedIsHeaderLength(string head, string rest, Shape shape)
    {
        bool ok = ParseFlexible(Bytes(head + rest), shape, out int consumed, out string after);

        Assert.True(ok);
        Assert.Equal(head.Length, consumed);
        Assert.Equal(rest, after);
    }

    [Fact]
    public void UltraHardened_Request_BodyIsReadableAtConsumed()
    {
        // The case the off-by-one used to corrupt: a Content-Length body read from consumed must be
        // the whole body, first byte included.
        ReadOnlyMemory<byte> input = Bytes("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 5\r\n\r\nhello");
        using var request = new BinaryRequest();

        Assert.True(UltraHardenedParser.TryParseRequestHeader(ref input, request, ParserLimits.Default, out int consumed));
        AssertAscii.Equal("hello", input.Slice(consumed, 5));
    }

    // ================================================================
    // Incomplete: false, consumed 0
    // ================================================================

    [Theory]
    [MemberData(nameof(PartialRequests))]
    public void UltraHardened_Request_Incomplete_ConsumedIsZero(string raw, Shape shape)
    {
        Assert.False(ParseRequest(Bytes(raw), shape, out int consumed, out _));
        Assert.Equal(0, consumed);
    }

    [Theory]
    [MemberData(nameof(PartialResponses))]
    public void UltraHardened_Response_Incomplete_ConsumedIsZero(string raw, Shape shape)
    {
        Assert.False(ParseResponse(Bytes(raw), shape, out int consumed, out _));
        Assert.Equal(0, consumed);
    }

    [Theory]
    [MemberData(nameof(PartialRequests))]
    public void Flexible_Request_Incomplete_ConsumedIsZero(string raw, Shape shape)
    {
        Assert.False(ParseFlexible(Bytes(raw), shape, out int consumed, out _));
        Assert.Equal(0, consumed);
    }

    // ================================================================
    // Obsolete entry points keep their legacy value: consumed - 1, or -1
    // ================================================================

    [Theory]
    [MemberData(nameof(Requests))]
    public void Legacy_UltraHardened_Request_IsConsumedMinusOne(string head, string rest, Shape shape)
    {
        byte[] bytes = Bytes(head + rest);
        Assert.True(ParseRequest(bytes, shape, out int consumed, out _));
        Assert.True(LegacyRequest(bytes, shape, out int bytesReadCount));
        Assert.Equal(consumed - 1, bytesReadCount);
    }

    [Theory]
    [MemberData(nameof(Responses))]
    public void Legacy_UltraHardened_Response_IsConsumedMinusOne(string head, string rest, Shape shape)
    {
        byte[] bytes = Bytes(head + rest);
        Assert.True(ParseResponse(bytes, shape, out int consumed, out _));
        Assert.True(LegacyResponse(bytes, shape, out int bytesReadCount));
        Assert.Equal(consumed - 1, bytesReadCount);
    }

    [Theory]
    [MemberData(nameof(Requests))]
    public void Legacy_Flexible_Request_IsConsumedMinusOne(string head, string rest, Shape shape)
    {
        byte[] bytes = Bytes(head + rest);
        Assert.True(ParseFlexible(bytes, shape, out int consumed, out _));
        Assert.True(LegacyFlexible(bytes, shape, out int bytesReadCount));
        Assert.Equal(consumed - 1, bytesReadCount);
    }

    [Theory]
    [MemberData(nameof(PartialRequests))]
    public void Legacy_Request_Incomplete_IsMinusOne(string raw, Shape shape)
    {
        Assert.False(LegacyRequest(Bytes(raw), shape, out int bytesReadCount));
        Assert.Equal(-1, bytesReadCount);

        Assert.False(LegacyFlexible(Bytes(raw), shape, out int flexibleCount));
        Assert.Equal(-1, flexibleCount);
    }

    [Theory]
    [MemberData(nameof(PartialResponses))]
    public void Legacy_Response_Incomplete_IsMinusOne(string raw, Shape shape)
    {
        Assert.False(LegacyResponse(Bytes(raw), shape, out int bytesReadCount));
        Assert.Equal(-1, bytesReadCount);
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);

    private static string Ascii(ReadOnlySpan<byte> b) => Encoding.ASCII.GetString(b);

    private static ReadOnlySequence<byte> ToSequence(byte[] data, Shape shape) => shape switch
    {
        Shape.SingleSegment => new ReadOnlySequence<byte>(data),
        Shape.ThreeSegments => Split(data, Math.Max(1, (data.Length + 2) / 3)),
        Shape.ByteSegments => Split(data, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    private static ReadOnlySequence<byte> Split(byte[] data, int size)
    {
        if (data.Length == 0)
            return ReadOnlySequence<byte>.Empty;

        var first = new BufferSegment(data.AsMemory(0, Math.Min(size, data.Length)));
        var last = first;
        for (int i = size; i < data.Length; i += size)
            last = last.Append(data.AsMemory(i, Math.Min(size, data.Length - i)));

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private static bool ParseRequest(byte[] bytes, Shape shape, out int consumed, out string after)
    {
        using var request = new BinaryRequest();
        bool ok;
        if (shape == Shape.Memory)
        {
            ReadOnlyMemory<byte> rom = bytes;
            ok = UltraHardenedParser.TryParseRequestHeader(ref rom, request, ParserLimits.Default, out consumed);
            after = ok ? Ascii(rom.Span[consumed..]) : "";
            return ok;
        }

        var seq = ToSequence(bytes, shape);
        ok = UltraHardenedParser.TryParseRequestHeader(ref seq, request, ParserLimits.Default, out consumed);
        after = ok ? Ascii(seq.Slice(consumed).ToArray()) : "";
        return ok;
    }

    private static bool ParseResponse(byte[] bytes, Shape shape, out int consumed, out string after)
    {
        using var response = new BinaryResponse();
        bool ok;
        if (shape == Shape.Memory)
        {
            ReadOnlyMemory<byte> rom = bytes;
            ok = UltraHardenedParser.TryParseResponseHeader(ref rom, response, ParserLimits.Default, out consumed);
            after = ok ? Ascii(rom.Span[consumed..]) : "";
            return ok;
        }

        var seq = ToSequence(bytes, shape);
        ok = UltraHardenedParser.TryParseResponseHeader(ref seq, response, ParserLimits.Default, out consumed);
        after = ok ? Ascii(seq.Slice(consumed).ToArray()) : "";
        return ok;
    }

    private static bool ParseFlexible(byte[] bytes, Shape shape, out int consumed, out string after)
    {
        using var request = new BinaryRequest();
        bool ok;
        if (shape == Shape.Memory)
        {
            ReadOnlyMemory<byte> rom = bytes;
            ok = FlexibleParser.TryParseRequestHeader(ref rom, request, out consumed);
            after = ok ? Ascii(rom.Span[consumed..]) : "";
            return ok;
        }

        var seq = ToSequence(bytes, shape);
        ok = FlexibleParser.TryParseRequestHeader(ref seq, request, out consumed);
        after = ok ? Ascii(seq.Slice(consumed).ToArray()) : "";
        return ok;
    }

#pragma warning disable CS0618 // the obsolete entry points are exactly what these helpers pin

    private static bool LegacyRequest(byte[] bytes, Shape shape, out int bytesReadCount)
    {
        using var request = new BinaryRequest();
        if (shape == Shape.Memory)
        {
            ReadOnlyMemory<byte> rom = bytes;
            return UltraHardenedParser.TryExtractFullHeaderROM(ref rom, request, ParserLimits.Default, out bytesReadCount);
        }

        var seq = ToSequence(bytes, shape);
        return UltraHardenedParser.TryExtractFullHeaderValidated(ref seq, request, ParserLimits.Default, out bytesReadCount);
    }

    private static bool LegacyResponse(byte[] bytes, Shape shape, out int bytesReadCount)
    {
        using var response = new BinaryResponse();
        if (shape == Shape.Memory)
        {
            ReadOnlyMemory<byte> rom = bytes;
            return UltraHardenedParser.TryExtractFullResponseHeaderROM(ref rom, response, ParserLimits.Default, out bytesReadCount);
        }

        var seq = ToSequence(bytes, shape);
        return UltraHardenedParser.TryExtractFullResponseHeaderValidated(ref seq, response, ParserLimits.Default, out bytesReadCount);
    }

    private static bool LegacyFlexible(byte[] bytes, Shape shape, out int bytesReadCount)
    {
        using var request = new BinaryRequest();
        if (shape == Shape.Memory)
        {
            ReadOnlyMemory<byte> rom = bytes;
            return FlexibleParser.TryExtractFullHeaderReadOnlyMemory(ref rom, request, out bytesReadCount);
        }

        var seq = ToSequence(bytes, shape);
        return FlexibleParser.TryExtractFullHeader(ref seq, request, out bytesReadCount);
    }

#pragma warning restore CS0618
}
