using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using Glyph11;
using Glyph11.Parser;
using Glyph11.Parser.FlexibleParser;
using Glyph11.Parser.UltraHardened;
using Glyph11.Protocol;
using Glyph11.Utils;

namespace Tests;

/// <summary>
/// Multi-segment input is linearized before parsing. Only the header block may be copied — a
/// pipelined body behind it never — and the header limit is checked before anything is copied
/// (issue #65).
/// </summary>
public class MultiSegmentHeaderCopyTests : IDisposable
{
    private const int BodySize = 8 * 1024 * 1024;

    private const string RequestHeader =
        "POST /upload?x=1 HTTP/1.1\r\nHost: example.com\r\nContent-Length: 8388608\r\n\r\n";

    private const string ResponseHeader =
        "HTTP/1.1 200 OK\r\nContent-Length: 8388608\r\n\r\n";

    private readonly BinaryRequest _request = new();
    private readonly BinaryResponse _response = new();

    public void Dispose()
    {
        _request.Dispose();
        _response.Dispose();
        GC.SuppressFinalize(this);
    }

    // ================================================================
    // UltraHardenedParser — requests
    // ================================================================

    [Fact]
    public void Request_PipelinedBody_IsNotCopied()
    {
        var limits = ParserLimits.Default;
        var seq = HeaderThenBody(RequestHeader, BodySize);

        WarmUp(() =>
        {
            var warm = HeaderThenBody(RequestHeader, 16);
            UltraHardenedParser.TryExtractFullHeaderValidated(ref warm, _request, in limits, out _);
        });
        _request.Clear();

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool ok = UltraHardenedParser.TryExtractFullHeaderValidated(ref seq, _request, in limits, out int bytesRead);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(ok);
        Assert.Equal(RequestHeader.Length - 1, bytesRead);
        AssertAscii.Equal("POST", _request.Method);
        AssertAscii.Equal("/upload", _request.Path);
        Assert.Equal(2, _request.Headers.Count);
        AssertAscii.Equal("Content-Length", _request.Headers[1].Key);

        Assert.Equal(RequestHeader.Length, BackingArrayLength(_request.Method));
        Assert.True(allocated < 64 * 1024, $"allocated {allocated} bytes for an {BodySize}-byte pipelined body");
    }

    [Theory]
    [InlineData(1)]   // terminator split as "\r" | "\n\r\n"
    [InlineData(2)]   // "\r\n" | "\r\n"
    [InlineData(3)]   // "\r\n\r" | "\n"
    public void Request_TerminatorSplitAcrossSegments_CopiesExactlyTheHeaderBlock(int splitInsideTerminator)
    {
        var limits = ParserLimits.Default;
        int cut = RequestHeader.Length - 4 + splitInsideTerminator;
        var bytes = Encoding.ASCII.GetBytes(RequestHeader + "BODYBODYBODY");
        var seq = Segments(bytes[..cut], bytes[cut..]);

        Assert.True(UltraHardenedParser.TryExtractFullHeaderValidated(ref seq, _request, in limits, out int bytesRead));
        Assert.Equal(RequestHeader.Length - 1, bytesRead);
        Assert.Equal(RequestHeader.Length, BackingArrayLength(_request.Path));
    }

    [Fact]
    public void Request_BodyStartingInTheTerminatorsSegment_IsNotCopied()
    {
        var limits = ParserLimits.Default;
        var head = Encoding.ASCII.GetBytes(RequestHeader);
        var seq = Segments(head[..20], [.. head[20..], .. "partial body"u8.ToArray()], new byte[4096]);

        Assert.True(UltraHardenedParser.TryExtractFullHeaderValidated(ref seq, _request, in limits, out int bytesRead));
        Assert.Equal(RequestHeader.Length - 1, bytesRead);
        Assert.Equal(RequestHeader.Length, BackingArrayLength(_request.Method));
    }

    [Fact]
    public void Request_HeaderOverLimit_Throws431_BeforeCopying()
    {
        var limits = ParserLimits.Default with { MaxTotalHeaderBytes = RequestHeader.Length - 1 };
        var seq = HeaderThenBody(RequestHeader, BodySize);

        WarmUp(() =>
        {
            var warm = HeaderThenBody(RequestHeader, 16);
            try { UltraHardenedParser.TryExtractFullHeaderValidated(ref warm, _request, in limits, out _); }
            catch (HttpParseException) { }
        });

        long before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Assert.Throws<HttpParseException>(
            () => UltraHardenedParser.TryExtractFullHeaderValidated(ref seq, _request, in limits, out _));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(431, ex.StatusCode);
        Assert.True(allocated < 1024 * 1024, $"allocated {allocated} bytes before rejecting");
    }

    [Fact]
    public void Request_HeaderExactlyAtLimit_Parses()
    {
        var limits = ParserLimits.Default with { MaxTotalHeaderBytes = RequestHeader.Length };
        var seq = HeaderThenBody(RequestHeader, 1024);

        Assert.True(UltraHardenedParser.TryExtractFullHeaderValidated(ref seq, _request, in limits, out int bytesRead));
        Assert.Equal(RequestHeader.Length - 1, bytesRead);
    }

    // ================================================================
    // UltraHardenedParser — responses
    // ================================================================

    [Fact]
    public void Response_Body_IsNotCopied()
    {
        var limits = ParserLimits.Default;
        var seq = HeaderThenBody(ResponseHeader, BodySize);

        WarmUp(() =>
        {
            var warm = HeaderThenBody(ResponseHeader, 16);
            UltraHardenedParser.TryExtractFullResponseHeaderValidated(ref warm, _response, in limits, out _);
        });
        _response.Clear();

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool ok = UltraHardenedParser.TryExtractFullResponseHeaderValidated(ref seq, _response, in limits, out int bytesRead);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(ok);
        Assert.Equal(ResponseHeader.Length - 1, bytesRead);
        Assert.Equal(200, _response.Status);
        AssertAscii.Equal("OK", _response.ReasonPhrase);

        Assert.Equal(ResponseHeader.Length, BackingArrayLength(_response.Version));
        Assert.True(allocated < 64 * 1024, $"allocated {allocated} bytes for an {BodySize}-byte body");
    }

    [Fact]
    public void Response_HeaderOverLimit_Throws431_BeforeCopying()
    {
        var limits = ParserLimits.Default with { MaxTotalHeaderBytes = ResponseHeader.Length - 1 };
        var seq = HeaderThenBody(ResponseHeader, BodySize);

        WarmUp(() =>
        {
            var warm = HeaderThenBody(ResponseHeader, 16);
            try { UltraHardenedParser.TryExtractFullResponseHeaderValidated(ref warm, _response, in limits, out _); }
            catch (HttpParseException) { }
        });

        long before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Assert.Throws<HttpParseException>(
            () => UltraHardenedParser.TryExtractFullResponseHeaderValidated(ref seq, _response, in limits, out _));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(431, ex.StatusCode);
        Assert.True(allocated < 1024 * 1024, $"allocated {allocated} bytes before rejecting");
    }

    // ================================================================
    // FlexibleParser
    // ================================================================

    [Fact]
    public void Flexible_PipelinedBody_IsNotCopied()
    {
        var seq = HeaderThenBody(RequestHeader, BodySize);

        WarmUp(() =>
        {
            var warm = HeaderThenBody(RequestHeader, 16);
            FlexibleParser.TryExtractFullHeader(ref warm, _request, out _);
        });
        _request.Clear();

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool ok = FlexibleParser.TryExtractFullHeader(ref seq, _request, out int bytesRead);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(ok);
        Assert.Equal(RequestHeader.Length - 1, bytesRead);
        AssertAscii.Equal("/upload", _request.Path);
        Assert.Equal(2, _request.Headers.Count);

        Assert.Equal(RequestHeader.Length, BackingArrayLength(_request.Method));
        Assert.True(allocated < 64 * 1024, $"allocated {allocated} bytes for an {BodySize}-byte pipelined body");
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static void WarmUp(Action action)
    {
        // First calls JIT and initialize statics; keep that out of the allocation measurements.
        action();
        action();
    }

    private static int BackingArrayLength(ReadOnlyMemory<byte> slice)
    {
        Assert.True(MemoryMarshal.TryGetArray(slice, out ArraySegment<byte> segment));
        return segment.Array!.Length;
    }

    /// <summary>
    /// The header split across two segments, then <paramref name="bodySize"/> body bytes in
    /// 1 MiB segments.
    /// </summary>
    private static ReadOnlySequence<byte> HeaderThenBody(string header, int bodySize)
    {
        var head = Encoding.ASCII.GetBytes(header);
        var parts = new List<byte[]> { head[..(head.Length / 2)], head[(head.Length / 2)..] };

        for (int remaining = bodySize; remaining > 0; remaining -= 1024 * 1024)
            parts.Add(new byte[Math.Min(remaining, 1024 * 1024)]);

        return Segments([.. parts]);
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
