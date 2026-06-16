using System.Buffers;
using System.Text;
using Glyph11;
using Glyph11.Parser;
using Glyph11.Utils;

namespace Tests;

public class ChunkedBodyStreamSequenceTests
{
    private static byte[] B(string s) => Encoding.ASCII.GetBytes(s);

    /// <summary>Build a multi-segment ReadOnlySequence from <paramref name="data"/> split into ~<paramref name="segments"/> pieces.</summary>
    private static ReadOnlySequence<byte> Segmented(byte[] data, int segments)
    {
        if (segments <= 1 || data.Length <= 1)
            return new ReadOnlySequence<byte>(data);

        int size = Math.Max(1, data.Length / segments);
        BufferSegment? first = null, last = null;
        for (int pos = 0; pos < data.Length; pos += size)
        {
            int len = Math.Min(size, data.Length - pos);
            var mem = data.AsMemory(pos, len);
            if (first is null) { first = new BufferSegment(mem); last = first; }
            else last = last!.Append(mem);
        }
        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    /// <summary>Oracle: drive the existing span-based parser over a complete buffer.</summary>
    private static (byte[] body, int consumed, bool completed) ParseSpan(byte[] input)
    {
        var s = new ChunkedBodyStream();
        var body = new List<byte>();
        int offset = 0;
        while (true)
        {
            var span = input.AsSpan(offset);
            var r = s.TryReadChunk(span, out var consumed, out var dOff, out var dLen);
            if (r == ChunkResult.Chunk) { body.AddRange(span.Slice(dOff, dLen).ToArray()); offset += consumed; }
            else if (r == ChunkResult.Completed) { offset += consumed; return (body.ToArray(), offset, true); }
            else return (body.ToArray(), offset, false);
        }
    }

    /// <summary>New: drive the sequence parser, re-presenting the remaining (multi-segment) buffer each call.</summary>
    private static (byte[] body, long consumed, bool completed) ParseSeq(byte[] input, int segments)
    {
        var s = new ChunkedBodyStream();
        var body = new List<byte>();
        long offset = 0;
        while (true)
        {
            var seq = Segmented(input, segments).Slice(offset);
            var r = s.TryReadChunk(seq, out var consumed, out var data);
            if (r == ChunkResult.Chunk) { body.AddRange(data.ToArray()); offset += consumed; }
            else if (r == ChunkResult.Completed) { offset += consumed; return (body.ToArray(), offset, true); }
            else return (body.ToArray(), offset, false);
        }
    }

    public static IEnumerable<object[]> ValidBodies()
    {
        yield return new object[] { "5\r\nHello\r\n0\r\n\r\n" };
        yield return new object[] { "5\r\nHello\r\n6\r\n World\r\n0\r\n\r\n" };
        yield return new object[] { "1\r\nX\r\n0\r\n\r\n" };
        yield return new object[] { "5;ext=1\r\nHello\r\n0\r\n\r\n" };
        yield return new object[] { "5\r\nHello\r\n0\r\nTrailer: value\r\n\r\n" };
        yield return new object[] { "5\r\nHello\r\n0\r\nA: 1\r\nB: 2\r\n\r\n" };
        yield return new object[] { "20\r\n" + new string('Z', 32) + "\r\n0\r\n\r\n" };
        yield return new object[] { "a\r\n0123456789\r\n3\r\nabc\r\n0\r\n\r\n" };
    }

    [Theory]
    [MemberData(nameof(ValidBodies))]
    public void Sequence_MatchesSpan_AcrossSegmentations(string raw)
    {
        var input = B(raw);
        var oracle = ParseSpan(input);
        Assert.True(oracle.completed);

        foreach (var segments in new[] { 1, 2, 3, 7, input.Length })
        {
            var got = ParseSeq(input, segments);
            Assert.True(got.completed, $"segments={segments}");
            Assert.Equal((long)oracle.consumed, got.consumed);
            Assert.Equal(oracle.body, got.body);
        }
    }

    [Fact]
    public void Sequence_ChunkDataSpanningSegmentBoundary()
    {
        var input = B("5\r\nHello\r\n0\r\n\r\n");
        // every byte in its own segment → the 5-byte payload spans 5 segments
        var got = ParseSeq(input, input.Length);
        Assert.True(got.completed);
        Assert.Equal("Hello", Encoding.ASCII.GetString(got.body));
    }

    [Fact]
    public void Sequence_NeedMoreData_RollsBack_ThenCompletes()
    {
        var full = B("5\r\nHello\r\n0\r\n\r\n");
        var s = new ChunkedBodyStream();

        // present a prefix mid-chunk → NeedMoreData, state rolled back, nothing consumed
        var r1 = s.TryReadChunk(Segmented(full[..7], 3), out var c1, out _);
        Assert.Equal(ChunkResult.NeedMoreData, r1);
        Assert.Equal(0, c1);

        // re-present the full body → the chunk parses cleanly
        var r2 = s.TryReadChunk(Segmented(full, 4), out _, out var data2);
        Assert.Equal(ChunkResult.Chunk, r2);
        Assert.Equal("Hello", Encoding.ASCII.GetString(data2.ToArray()));
    }

    [Theory]
    [InlineData("G\r\n\r\n")]                 // invalid hex digit
    [InlineData("5\r\nHello\nX")]             // bare LF after chunk data
    [InlineData(" 5\r\nHello\r\n0\r\n\r\n")]  // leading whitespace in size
    [InlineData("-5\r\n\r\n")]                // negative size
    [InlineData("0x5\r\n\r\n")]               // hex prefix
    public void Sequence_RejectsMalformed(string raw)
    {
        var input = B(raw);
        Assert.Throws<HttpParseException>(() =>
        {
            var s = new ChunkedBodyStream();
            long offset = 0;
            for (int i = 0; i < 32; i++)
            {
                var r = s.TryReadChunk(Segmented(input, 3).Slice(offset), out var consumed, out _);
                if (r is ChunkResult.Completed or ChunkResult.NeedMoreData) break;
                offset += consumed;
            }
        });
    }
}
