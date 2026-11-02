using System.Buffers;
using System.Text;
using Glyph11;
using Glyph11.Parser;
using Glyph11.Utils;
using Glyph11.Validation;

namespace Tests;

/// <summary>
/// Chunk sizes of 2^63 or more do not fit the signed long the parsers accumulate into. They must be
/// refused as an overflow by every chunk parser — never truncated, re-read as framing, or surfaced as
/// an ArgumentOutOfRangeException.
/// </summary>
public class ChunkedBodySizeOverflowTests
{
    private const string Overflow = "Chunk size overflow.";

    private static byte[] B(string s) => Encoding.ASCII.GetBytes(s);

    private static ReadOnlySequence<byte> Segmented(byte[] data, int segments)
    {
        if (segments <= 1 || data.Length <= 1)
            return new ReadOnlySequence<byte>(data);

        int size = Math.Max(1, data.Length / segments);
        BufferSegment? first = null, last = null;
        for (int pos = 0; pos < data.Length; pos += size)
        {
            var mem = data.AsMemory(pos, Math.Min(size, data.Length - pos));
            if (first is null) { first = new BufferSegment(mem); last = first; }
            else last = last!.Append(mem);
        }
        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    /// <summary>Drives the span overload over a complete buffer.</summary>
    private static (string body, ChunkResult last) ReadAllSpan(byte[] input)
    {
        var stream = new ChunkedBodyStream();
        var body = new StringBuilder();
        int offset = 0;
        while (true)
        {
            var span = input.AsSpan(offset);
            var r = stream.TryReadChunk(span, out var consumed, out var off, out var len);
            if (r != ChunkResult.Chunk) return (body.ToString(), r);
            Assert.True(len >= 0, "a chunk length must never be negative");
            body.Append(Encoding.ASCII.GetString(span.Slice(off, len)));
            offset += consumed;
        }
    }

    /// <summary>Drives the sequence overload over a complete buffer split into segments.</summary>
    private static (string body, ChunkResult last) ReadAllSequence(byte[] input, int segments)
    {
        var stream = new ChunkedBodyStream();
        var body = new StringBuilder();
        long offset = 0;
        while (true)
        {
            var seq = Segmented(input, segments).Slice(offset);
            var r = stream.TryReadChunk(seq, out var consumed, out var data);
            if (r != ChunkResult.Chunk) return (body.ToString(), r);
            body.Append(Encoding.ASCII.GetString(data.ToArray()));
            offset += consumed;
        }
    }

    public static IEnumerable<object[]> OverflowingBodies()
    {
        // The rows of issue #62's table (and the bare minimum, 2^63).
        yield return new object[] { "8000000000000005\r\nhello\r\n0\r\n\r\n" };
        yield return new object[] { "8000000000000000\r\n\r\n0\r\n\r\n" };
        yield return new object[] { "fffffffffffffffe\r\n5\r\nhello\r\n0\r\n\r\n" };
        yield return new object[] { "FFFFFFFFFFFFFFFF\r\n" };
        yield return new object[] { "8000000000000000" };        // refused as soon as the 16th digit lands
        yield return new object[] { "8000000000000000;ext=1\r\n" };
    }

    public static IEnumerable<object[]> Segmentations()
    {
        foreach (var segments in new[] { 1, 2, 3, 7, int.MaxValue })
            yield return new object[] { segments };
    }

    // ---- span overload ----

    [Theory]
    [MemberData(nameof(OverflowingBodies))]
    public void Span_RefusesSizeAboveLongMax(string raw)
    {
        var ex = Assert.Throws<HttpParseException>(() => ReadAllSpan(B(raw)));
        Assert.Equal(Overflow, ex.Message);
    }

    // ---- sequence overload: an HttpParseException, never Slice's ArgumentOutOfRangeException ----

    [Theory]
    [MemberData(nameof(OverflowingBodies))]
    public void Sequence_RefusesSizeAboveLongMax(string raw)
    {
        var input = B(raw);
        foreach (var segments in new[] { 1, 2, 3, input.Length })
        {
            var ex = Assert.Throws<HttpParseException>(() => ReadAllSequence(input, segments));
            Assert.Equal(Overflow, ex.Message);
        }
    }

    // ---- validator ----

    [Theory]
    [MemberData(nameof(OverflowingBodies))]
    public void Validator_RefusesSizeAboveLongMax(string raw)
    {
        var ex = Assert.Throws<HttpParseException>(() => ChunkedBodyValidator.TryValidate(B(raw), out _));
        Assert.Equal(Overflow, ex.Message);
    }

    // ---- the first table row stays valid everywhere ----

    [Fact]
    public void SmallChunk_StillParses()
    {
        var input = B("5\r\nhello\r\n0\r\n\r\n");

        Assert.Equal(("hello", ChunkResult.Completed), ReadAllSpan(input));
        foreach (var segments in new[] { 1, 3, input.Length })
            Assert.Equal(("hello", ChunkResult.Completed), ReadAllSequence(input, segments));

        Assert.True(ChunkedBodyValidator.TryValidate(input, out var consumed));
        Assert.Equal(input.Length, consumed);
    }

    // ---- 16 digits are still fine while the value fits ----

    [Fact]
    public void SixteenDigits_BelowTheLimit_Parse()
    {
        var input = B("000000000000000a\r\n0123456789\r\n0\r\n\r\n");

        Assert.Equal(("0123456789", ChunkResult.Completed), ReadAllSpan(input));
        Assert.Equal(("0123456789", ChunkResult.Completed), ReadAllSequence(input, 3));

        Assert.True(ChunkedBodyValidator.TryValidate(input, out var consumed));
        Assert.Equal(input.Length, consumed);
    }

    // ---- long.MaxValue itself is a legal size: accept the size line and wait for the data ----
    // (The data has not arrived, so the call either waits or hands out the bytes that are there;
    //  what it must not do is refuse the size, complete the body, or report a negative length.)

    [Fact]
    public void LongMaxValue_Span_IsAcceptedAndAwaited()
    {
        var input = B("7fffffffffffffff\r\nhello\r\n0\r\n\r\n");
        var stream = new ChunkedBodyStream();

        var r = stream.TryReadChunk(input, out var consumed, out var off, out var len);

        Assert.True(r is ChunkResult.NeedMoreData or ChunkResult.Chunk, $"got {r}");
        Assert.InRange(len, 0, input.Length);
        Assert.InRange(off + len, 0, input.Length);
        Assert.InRange(consumed, 0, input.Length);
    }

    [Theory]
    [MemberData(nameof(Segmentations))]
    public void LongMaxValue_Sequence_IsAcceptedAndAwaited(int segments)
    {
        var input = B("7fffffffffffffff\r\nhello\r\n0\r\n\r\n");
        var stream = new ChunkedBodyStream();

        var r = stream.TryReadChunk(Segmented(input, segments), out var consumed, out var data);

        Assert.True(r is ChunkResult.NeedMoreData or ChunkResult.Chunk, $"got {r}");
        Assert.InRange(consumed, 0, input.Length);
        Assert.InRange(data.Length, 0, input.Length);
    }

    [Fact]
    public void LongMaxValue_Validator_IsIncomplete()
    {
        // Used to overflow pos + chunkSize and be refused as "Missing CRLF after chunk data.".
        var input = B("7fffffffffffffff\r\nhello\r\n0\r\n\r\n");

        Assert.False(ChunkedBodyValidator.TryValidate(input, out _));
    }

    [Fact]
    public void JustBelowLongMaxValue_Validator_IsIncomplete()
    {
        Assert.False(ChunkedBodyValidator.TryValidate(B("7ffffffffffffff0\r\nhello"), out _));
    }
}
