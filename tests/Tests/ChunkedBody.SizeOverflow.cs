using System.Buffers;
using System.Globalization;
using System.Numerics;
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

    // The rows of issue #62's table (and the bare minimum, 2^63).
    public static TheoryData<string> OverflowingBodies() => new()
    {
        "8000000000000005\r\nhello\r\n0\r\n\r\n",
        "8000000000000000\r\n\r\n0\r\n\r\n",
        "fffffffffffffffe\r\n5\r\nhello\r\n0\r\n\r\n",
        "FFFFFFFFFFFFFFFF\r\n",
        "8000000000000000",        // refused as soon as the 16th digit lands
        "8000000000000000;ext=1\r\n",
    };

    public static TheoryData<int> Segmentations() => new() { 1, 2, 3, 7, int.MaxValue };

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

    // ---- ... but a 17th digit is refused even when the value would fit (leading zeros) ----

    [Fact]
    public void SeventeenDigits_AreRefused_EvenWhenTheValueFits()
    {
        var input = B("00000000000000005\r\nhello\r\n0\r\n\r\n");

        Assert.Equal(Overflow, Assert.Throws<HttpParseException>(() => ReadAllSpan(input)).Message);
        foreach (var segments in new[] { 1, 3, input.Length })
            Assert.Equal(Overflow, Assert.Throws<HttpParseException>(() => ReadAllSequence(input, segments)).Message);
        Assert.Equal(Overflow, Assert.Throws<HttpParseException>(() => ChunkedBodyValidator.TryValidate(input, out _)).Message);
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

    // ---- any size: accepted exactly when it has at most 16 digits and fits a long ----
    // The expectation comes from BigInteger, independent of the parsers' arithmetic, and the cases
    // crowd around 2^63 and the 16-digit cap. Only the size line is sent, so an accepted size waits
    // for its data (NeedMoreData, or false from the validator) and a refused one throws at once.

    [Fact]
    public void AnySize_IsAcceptedExactlyWhenItFitsALong()
    {
        var rng = new Random(20261102);
        for (int i = 0; i < 2000; i++)
        {
            string size = RandomHexSize(rng);
            bool fits = size.Length <= 16
                && BigInteger.Parse("0" + size, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture) <= long.MaxValue;
            var input = B(size + "\r\n");

            if (fits)
            {
                Assert.True(new ChunkedBodyStream().TryReadChunk(input, out _, out _, out _) == ChunkResult.NeedMoreData, $"span: {size}");
                Assert.True(new ChunkedBodyStream().TryReadChunk(Segmented(input, 3), out _, out _) == ChunkResult.NeedMoreData, $"sequence: {size}");
                Assert.False(ChunkedBodyValidator.TryValidate(input, out _), $"validator: {size}");
            }
            else
            {
                Assert.Equal(Overflow, Assert.Throws<HttpParseException>(() => new ChunkedBodyStream().TryReadChunk(input, out _, out _, out _)).Message);
                Assert.Equal(Overflow, Assert.Throws<HttpParseException>(() => new ChunkedBodyStream().TryReadChunk(Segmented(input, 3), out _, out _)).Message);
                Assert.Equal(Overflow, Assert.Throws<HttpParseException>(() => ChunkedBodyValidator.TryValidate(input, out _)).Message);
            }
        }
    }

    private static string RandomHexSize(Random rng) => rng.Next(4) switch
    {
        0 => Hex(rng, rng.Next(1, 21)),                                   // 1 to 20 digits
        1 => (rng.Next(2) == 0 ? "7" : "8") + Hex(rng, 15),               // 16 digits either side of 2^63
        2 => new string('0', rng.Next(1, 6)) + Hex(rng, rng.Next(1, 17)), // leading zeros, past the cap too
        _ => "7fffffffffffff" + Hex(rng, rng.Next(1, 4)),                 // 15 to 17 digits at the edge
    };

    private static string Hex(Random rng, int count)
    {
        const string digits = "0123456789abcdefABCDEF";
        var chars = new char[count];
        for (int i = 0; i < count; i++)
            chars[i] = digits[rng.Next(digits.Length)];
        return new string(chars);
    }
}
