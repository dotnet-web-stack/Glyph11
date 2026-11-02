using System.Buffers;
using System.Text;
using Glyph11;
using Glyph11.Parser;
using Glyph11.Utils;
using Glyph11.Validation;

namespace Tests;

/// <summary>
/// Resource ceilings of chunked decoding: chunk data is handed out as it arrives (so buffering is
/// bounded by the read window, not by the declared chunk size), and the trailer section is capped
/// by <see cref="ParserLimits.MaxTotalTrailerBytes"/>.
/// </summary>
public class ChunkedBodyStreamLimitsTests
{
    private const int DefaultTrailerCap = 16_384;

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

    // ---- drivers: behave like a PipeReader loop — only unconsumed bytes stay buffered ----

    private sealed class Feed
    {
        public readonly List<byte> Body = new();
        public int MaxBuffered;
        public int Fed;
        public int Leftover;
    }

    private static Feed FeedSpan(ChunkedBodyStream stream, byte[] input, Func<int> readSize)
    {
        var f = new Feed();
        var buffer = new List<byte>();
        while (true)
        {
            if (buffer.Count > 0)
            {
                var span = buffer.ToArray();
                var r = stream.TryReadChunk(span, out var consumed, out var off, out var len);
                if (r == ChunkResult.Chunk)
                {
                    Assert.True(len > 0, "a Chunk result must carry data");
                    f.Body.AddRange(span.AsSpan(off, len).ToArray());
                    buffer.RemoveRange(0, consumed);
                    continue;
                }
                if (r == ChunkResult.Completed)
                {
                    buffer.RemoveRange(0, consumed);
                    f.Leftover = buffer.Count + (input.Length - f.Fed);
                    return f;
                }
                Assert.Equal(0, consumed); // NeedMoreData consumes nothing
            }

            if (f.Fed == input.Length)
                throw new Xunit.Sdk.XunitException("input ended before the body completed");

            int n = Math.Min(readSize(), input.Length - f.Fed);
            buffer.AddRange(input.AsSpan(f.Fed, n).ToArray());
            f.Fed += n;
            f.MaxBuffered = Math.Max(f.MaxBuffered, buffer.Count);
        }
    }

    private static Feed FeedSequence(ChunkedBodyStream stream, byte[] input, Func<int> readSize, int segments = 3)
    {
        var f = new Feed();
        var buffer = new List<byte>();
        while (true)
        {
            if (buffer.Count > 0)
            {
                var seq = Segmented(buffer.ToArray(), segments);
                var r = stream.TryReadChunk(seq, out var consumed, out var data);
                if (r == ChunkResult.Chunk)
                {
                    Assert.True(data.Length > 0, "a Chunk result must carry data");
                    f.Body.AddRange(data.ToArray());
                    buffer.RemoveRange(0, (int)consumed);
                    continue;
                }
                if (r == ChunkResult.Completed)
                {
                    buffer.RemoveRange(0, (int)consumed);
                    f.Leftover = buffer.Count + (input.Length - f.Fed);
                    return f;
                }
                Assert.Equal(0, consumed);
            }

            if (f.Fed == input.Length)
                throw new Xunit.Sdk.XunitException("input ended before the body completed");

            int n = Math.Min(readSize(), input.Length - f.Fed);
            buffer.AddRange(input.AsSpan(f.Fed, n).ToArray());
            f.Fed += n;
            f.MaxBuffered = Math.Max(f.MaxBuffered, buffer.Count);
        }
    }

    private static Func<int> Fixed(int n) => () => n;

    private static Func<int> RandomReads(int seed, int max)
    {
        var rng = new Random(seed);
        return () => rng.Next(1, max + 1);
    }

    /// <summary>Encodes <paramref name="pieces"/> as chunks; returns the wire bytes and the decoded body.</summary>
    private static (byte[] wire, byte[] body) Chunked(IEnumerable<string> pieces, string trailers = "")
    {
        var wire = new StringBuilder();
        var body = new StringBuilder();
        foreach (var p in pieces)
        {
            wire.Append(p.Length.ToString("x")).Append("\r\n").Append(p).Append("\r\n");
            body.Append(p);
        }
        wire.Append("0\r\n").Append(trailers).Append("\r\n");
        return (B(wire.ToString()), B(body.ToString()));
    }

    public static TheoryData<string[], string> Bodies()
    {
        var rng = new Random(42);
        var many = Enumerable.Range(0, 20)
            .Select(i => new string((char)('a' + i), rng.Next(1, 300))).ToArray();

        return new TheoryData<string[], string>
        {
            { ["Hello"], "" },
            { ["Hello", " World"], "" },
            { ["0123456789", "abc"], "A: 1\r\nB: 2\r\n" },
            { [new string('Z', 1000)], "Trailer: value\r\n" },
            { many, "" },
        };
    }

    // =====================================================================
    //  (a) chunk data is handed out as it arrives
    // =====================================================================

    [Theory]
    [MemberData(nameof(Bodies))]
    public void Span_ByteAtATime_ReassemblesBody(string[] pieces, string trailers)
    {
        var (wire, body) = Chunked(pieces, trailers);
        var f = FeedSpan(new ChunkedBodyStream(), wire, Fixed(1));
        Assert.Equal(body, f.Body.ToArray());
        Assert.Equal(0, f.Leftover);
    }

    [Theory]
    [MemberData(nameof(Bodies))]
    public void Sequence_ByteAtATime_ReassemblesBody(string[] pieces, string trailers)
    {
        var (wire, body) = Chunked(pieces, trailers);
        var f = FeedSequence(new ChunkedBodyStream(), wire, Fixed(1));
        Assert.Equal(body, f.Body.ToArray());
        Assert.Equal(0, f.Leftover);
    }

    [Theory]
    [MemberData(nameof(Bodies))]
    public void Span_RandomSplits_ReassembleBody(string[] pieces, string trailers)
    {
        var (wire, body) = Chunked(pieces, trailers);
        for (int seed = 0; seed < 25; seed++)
        {
            var f = FeedSpan(new ChunkedBodyStream(), wire, RandomReads(seed, 40));
            Assert.Equal(body, f.Body.ToArray());
            Assert.Equal(0, f.Leftover);
        }
    }

    [Theory]
    [MemberData(nameof(Bodies))]
    public void Sequence_RandomSplits_ReassembleBody(string[] pieces, string trailers)
    {
        var (wire, body) = Chunked(pieces, trailers);
        for (int seed = 0; seed < 25; seed++)
        {
            var f = FeedSequence(new ChunkedBodyStream(), wire, RandomReads(seed, 40), segments: 1 + seed % 5);
            Assert.Equal(body, f.Body.ToArray());
            Assert.Equal(0, f.Leftover);
        }
    }

    [Fact]
    public void LargeChunk_BufferingIsBoundedByTheReadWindow()
    {
        // One 1 MiB chunk read 4 KiB at a time: nothing waits for the whole chunk.
        var (wire, body) = Chunked(new[] { new string('Q', 1 << 20) });
        const int read = 4096;

        var span = FeedSpan(new ChunkedBodyStream(), wire, Fixed(read));
        Assert.Equal(body, span.Body.ToArray());
        Assert.True(span.MaxBuffered <= read + 16, $"span buffered {span.MaxBuffered} bytes");

        var seq = FeedSequence(new ChunkedBodyStream(), wire, Fixed(read));
        Assert.Equal(body, seq.Body.ToArray());
        Assert.True(seq.MaxBuffered <= read + 16, $"sequence buffered {seq.MaxBuffered} bytes");
    }

    [Fact]
    public void Span_HugeDeclaredChunk_HandsOutWhatArrived()
    {
        var stream = new ChunkedBodyStream();

        var first = B("FFFFFFFF\r\nabc");
        Assert.Equal(ChunkResult.Chunk, stream.TryReadChunk(first, out var c1, out var off1, out var len1));
        Assert.Equal("abc", Encoding.ASCII.GetString(first.AsSpan(off1, len1)));
        Assert.Equal(first.Length, c1);

        var next = B("def");
        Assert.Equal(ChunkResult.Chunk, stream.TryReadChunk(next, out var c2, out var off2, out var len2));
        Assert.Equal("def", Encoding.ASCII.GetString(next.AsSpan(off2, len2)));
        Assert.Equal(next.Length, c2);
    }

    [Fact]
    public void Sequence_HugeDeclaredChunk_HandsOutWhatArrived()
    {
        var stream = new ChunkedBodyStream();

        var first = B("FFFFFFFF\r\nabc");
        Assert.Equal(ChunkResult.Chunk, stream.TryReadChunk(Segmented(first, 4), out var c1, out var d1));
        Assert.Equal("abc", Encoding.ASCII.GetString(d1.ToArray()));
        Assert.Equal(first.Length, c1);

        var next = B("def");
        Assert.Equal(ChunkResult.Chunk, stream.TryReadChunk(Segmented(next, 3), out var c2, out var d2));
        Assert.Equal("def", Encoding.ASCII.GetString(d2.ToArray()));
        Assert.Equal(next.Length, c2);
    }

    [Fact]
    public void SizeLineWithoutData_NeedsMoreData_NothingConsumed()
    {
        // never an empty Chunk: with no data yet, the size line is re-presented
        var input = B("5\r\n");

        var span = new ChunkedBodyStream();
        Assert.Equal(ChunkResult.NeedMoreData, span.TryReadChunk(input, out var c1, out _, out _));
        Assert.Equal(0, c1);

        var seq = new ChunkedBodyStream();
        Assert.Equal(ChunkResult.NeedMoreData, seq.TryReadChunk(Segmented(input, 3), out var c2, out _));
        Assert.Equal(0, c2);
    }

    [Theory]
    [InlineData("5\r\nHello")]       // the CRLF has not started
    [InlineData("5\r\nHello\r")]     // half of it is here
    public void Span_DataCompleteBeforeItsCrlf_IsHandedOut_ThenCrlfChecked(string first)
    {
        var stream = new ChunkedBodyStream();
        var input1 = B(first);

        Assert.Equal(ChunkResult.Chunk, stream.TryReadChunk(input1, out var c1, out var off, out var len));
        Assert.Equal("Hello", Encoding.ASCII.GetString(input1.AsSpan(off, len)));
        Assert.Equal(8, c1); // the data, not a partial CRLF

        // the CRLF alone: checked, then wait for the next chunk without consuming
        Assert.Equal(ChunkResult.NeedMoreData, stream.TryReadChunk(B("\r"), out var c2, out _, out _));
        Assert.Equal(0, c2);

        var rest = B("\r\n0\r\n\r\n");
        Assert.Equal(ChunkResult.Completed, stream.TryReadChunk(rest, out var c3, out _, out _));
        Assert.Equal(rest.Length, c3);
    }

    [Theory]
    [InlineData("5\r\nHello")]
    [InlineData("5\r\nHello\r")]
    public void Sequence_DataCompleteBeforeItsCrlf_IsHandedOut_ThenCrlfChecked(string first)
    {
        var stream = new ChunkedBodyStream();

        Assert.Equal(ChunkResult.Chunk, stream.TryReadChunk(Segmented(B(first), 3), out var c1, out var data));
        Assert.Equal("Hello", Encoding.ASCII.GetString(data.ToArray()));
        Assert.Equal(8, c1);

        Assert.Equal(ChunkResult.NeedMoreData, stream.TryReadChunk(new ReadOnlySequence<byte>(B("\r")), out var c2, out _));
        Assert.Equal(0, c2);

        var rest = B("\r\n0\r\n\r\n");
        Assert.Equal(ChunkResult.Completed, stream.TryReadChunk(Segmented(rest, 2), out var c3, out _));
        Assert.Equal(rest.Length, c3);
    }

    [Fact]
    public void DataHandedOutEarly_BadCrlfStillRejected()
    {
        var span = new ChunkedBodyStream();
        Assert.Equal(ChunkResult.Chunk, span.TryReadChunk(B("5\r\nHello"), out _, out _, out _));
        var ex1 = Assert.Throws<HttpParseException>(() => span.TryReadChunk(B("XY0\r\n\r\n"), out _, out _, out _));
        Assert.Equal("Missing CRLF after chunk data.", ex1.Message);

        var seq = new ChunkedBodyStream();
        Assert.Equal(ChunkResult.Chunk, seq.TryReadChunk(Segmented(B("5\r\nHello"), 2), out _, out _));
        var ex2 = Assert.Throws<HttpParseException>(() => seq.TryReadChunk(Segmented(B("XY0\r\n\r\n"), 2), out _, out _));
        Assert.Equal("Missing CRLF after chunk data.", ex2.Message);
    }

    // =====================================================================
    //  (b) the trailer section is capped
    // =====================================================================

    /// <summary>
    /// A complete chunked body whose trailer section (field lines incl. their CRLFs, not the final
    /// empty line) is exactly <paramref name="sectionBytes"/> long.
    /// </summary>
    private static byte[] TrailerBody(int sectionBytes, bool finalLf = true)
    {
        var sb = new StringBuilder("0\r\n");
        int left = sectionBytes;
        while (left > 0)
        {
            int line = left >= 105 ? 100 : left;   // keep the last line >= 5 bytes ("T: " + CRLF)
            if (line < 5) throw new ArgumentOutOfRangeException(nameof(sectionBytes));
            sb.Append("T: ").Append('a', line - 5).Append("\r\n");
            left -= line;
        }
        sb.Append(finalLf ? "\r\n" : "\r");
        return B(sb.ToString());
    }

    private static void AssertTrailerLimit(Action act)
    {
        var ex = Assert.Throws<HttpParseException>(act);
        Assert.Equal("Chunked trailer section exceeds limit.", ex.Message);
        Assert.Equal(431, ex.StatusCode);
    }

    private static ChunkResult ReadSpan(ChunkedBodyStream stream, byte[] input)
        => stream.TryReadChunk(input, out _, out _, out _);

    private static ChunkResult ReadSequence(ChunkedBodyStream stream, byte[] input, int segments)
        => stream.TryReadChunk(Segmented(input, segments), out _, out _);

    [Fact]
    public void Default_Is16KiB()
    {
        Assert.Equal(DefaultTrailerCap, ParserLimits.Default.MaxTotalTrailerBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(DefaultTrailerCap - 1)]
    [InlineData(DefaultTrailerCap)]
    public void AtOrUnderDefaultCap_Completes(int sectionBytes)
    {
        var input = TrailerBody(sectionBytes);

        Assert.Equal(ChunkResult.Completed, ReadSpan(new ChunkedBodyStream(), input));
        foreach (var segments in new[] { 1, 3, 17 })
            Assert.Equal(ChunkResult.Completed, ReadSequence(new ChunkedBodyStream(), input, segments));

        Assert.True(ChunkedBodyValidator.TryValidate(input, out var consumed));
        Assert.Equal(input.Length, consumed);
    }

    [Theory]
    [InlineData(DefaultTrailerCap + 1)]
    [InlineData(DefaultTrailerCap * 4)]
    public void OverDefaultCap_IsRejected(int sectionBytes)
    {
        var input = TrailerBody(sectionBytes);

        AssertTrailerLimit(() => ReadSpan(new ChunkedBodyStream(), input));
        AssertTrailerLimit(() => ReadSpan(default, input));   // default(struct) applies the default cap too
        foreach (var segments in new[] { 1, 3, 17 })
            AssertTrailerLimit(() => ReadSequence(new ChunkedBodyStream(), input, segments));

        AssertTrailerLimit(() => ChunkedBodyValidator.TryValidate(input, out _));
    }

    [Fact]
    public void CustomLimit_IsHonoured()
    {
        var limits = ParserLimits.Default with { MaxTotalTrailerBytes = 64 };
        var atCap = TrailerBody(64);
        var overCap = TrailerBody(65);

        Assert.Equal(ChunkResult.Completed, ReadSpan(new ChunkedBodyStream(limits), atCap));
        Assert.Equal(ChunkResult.Completed, ReadSequence(new ChunkedBodyStream(limits), atCap, 3));
        Assert.True(ChunkedBodyValidator.TryValidate(atCap, limits, out _));

        AssertTrailerLimit(() => ReadSpan(new ChunkedBodyStream(limits), overCap));
        AssertTrailerLimit(() => ReadSequence(new ChunkedBodyStream(limits), overCap, 3));
        AssertTrailerLimit(() => ChunkedBodyValidator.TryValidate(overCap, limits, out _));
    }

    [Fact]
    public void ZeroLimit_RefusesAnyTrailerButAllowsNone()
    {
        var limits = ParserLimits.Default with { MaxTotalTrailerBytes = 0 };
        var none = B("5\r\nHello\r\n0\r\n\r\n");
        var one = B("0\r\nA: b\r\n\r\n");

        var f = FeedSpan(new ChunkedBodyStream(limits), none, Fixed(none.Length));
        Assert.Equal("Hello", Encoding.ASCII.GetString(f.Body.ToArray()));
        Assert.True(ChunkedBodyValidator.TryValidate(none, limits, out _));

        AssertTrailerLimit(() => ReadSpan(new ChunkedBodyStream(limits), one));
        AssertTrailerLimit(() => ReadSequence(new ChunkedBodyStream(limits), one, 2));
        AssertTrailerLimit(() => ChunkedBodyValidator.TryValidate(one, limits, out _));
    }

    [Fact]
    public void IncompleteSectionUnderCap_StillWaits()
    {
        var input = B("0\r\nT: " + new string('a', 100));

        var span = new ChunkedBodyStream();
        Assert.Equal(ChunkResult.NeedMoreData, span.TryReadChunk(input, out var c1, out _, out _));
        Assert.Equal(0, c1);

        var seq = new ChunkedBodyStream();
        Assert.Equal(ChunkResult.NeedMoreData, seq.TryReadChunk(Segmented(input, 3), out var c2, out _));
        Assert.Equal(0, c2);

        Assert.False(ChunkedBodyValidator.TryValidate(input, out _));
    }

    [Fact]
    public void SectionAtCap_FinalCrlfSplit_IsNotRefused()
    {
        // The trailer lines are exactly at the cap and only the CR of the final empty line has
        // arrived: that CR is not trailer content, so this must wait, not be refused.
        var partial = TrailerBody(DefaultTrailerCap, finalLf: false);
        var full = TrailerBody(DefaultTrailerCap);

        var span = new ChunkedBodyStream();
        Assert.Equal(ChunkResult.NeedMoreData, ReadSpan(span, partial));
        Assert.Equal(ChunkResult.Completed, ReadSpan(span, full));

        var seq = new ChunkedBodyStream();
        Assert.Equal(ChunkResult.NeedMoreData, ReadSequence(seq, partial, 3));
        Assert.Equal(ChunkResult.Completed, ReadSequence(seq, full, 3));

        Assert.False(ChunkedBodyValidator.TryValidate(partial, out _));
        Assert.True(ChunkedBodyValidator.TryValidate(full, out _));
    }

    [Fact]
    public void EndlessTrailerLine_IsRejectedNotBuffered()
    {
        // one trailer line that never ends — no CRLF anywhere
        var input = B("0\r\nT: " + new string('a', DefaultTrailerCap + 10));

        AssertTrailerLimit(() => ReadSpan(new ChunkedBodyStream(), input));
        AssertTrailerLimit(() => ReadSequence(new ChunkedBodyStream(), input, 5));
        AssertTrailerLimit(() => ChunkedBodyValidator.TryValidate(input, out _));
    }

    [Fact]
    public void EndlessTrailerLines_AreRejectedOnceThePresentedBytesPassTheCap()
    {
        // "A: b\r\n" forever, never the final empty line — read 512 bytes at a time, the way a
        // PipeReader loop re-presents an incomplete trailer section.
        var sb = new StringBuilder("5\r\nHello\r\n0\r\n");
        for (int i = 0; i < 10_000; i++) sb.Append("A: b\r\n");
        var input = B(sb.ToString());
        const int read = 512;

        foreach (var useSequence in new[] { false, true })
        {
            var stream = new ChunkedBodyStream();
            int fed = 0;
            var ex = Assert.Throws<HttpParseException>(() =>
            {
                var buffer = new List<byte>();
                while (true)
                {
                    int n = Math.Min(read, input.Length - fed);
                    if (n == 0) return; // fell off the end without a decision: the test fails
                    buffer.AddRange(input.AsSpan(fed, n).ToArray());
                    fed += n;

                    while (buffer.Count > 0)
                    {
                        ChunkResult r;
                        int consumed;
                        if (useSequence)
                        {
                            r = stream.TryReadChunk(Segmented(buffer.ToArray(), 3), out long c, out _);
                            consumed = (int)c;
                        }
                        else
                        {
                            r = stream.TryReadChunk(buffer.ToArray(), out consumed, out _, out _);
                        }
                        if (r != ChunkResult.Chunk) break;
                        buffer.RemoveRange(0, consumed);
                    }
                }
            });

            Assert.Equal(431, ex.StatusCode);
            // refused within one read of crossing the cap, long before the 60 KB stream ends
            Assert.InRange(fed, DefaultTrailerCap, DefaultTrailerCap + 13 + read);
        }
    }

    [Fact]
    public void TrailerLimit_IsALimitViolation()
    {
        var ex = Assert.Throws<HttpParseException>(() => ReadSpan(new ChunkedBodyStream(), TrailerBody(DefaultTrailerCap + 1)));
        Assert.True(ex.IsLimitViolation);
    }
}
