using System.Text;
using Glyph11;
using Glyph11.Parser;

namespace Tests;

public class ChunkedBodyStreamTests
{
    private static byte[] B(string s) => Encoding.ASCII.GetBytes(s);

    // ==== Single-call (parity with TryValidate) ====

    [Fact]
    public void SingleCall_ValidSingleChunk()
    {
        var input = B("5\r\nHello\r\n0\r\n\r\n");
        var stream = new ChunkedBodyStream();

        var r1 = stream.TryReadChunk(input, out var c1, out var off1, out var len1);
        Assert.Equal(ChunkResult.Chunk, r1);
        Assert.Equal(5, len1);
        Assert.Equal("Hello", Encoding.ASCII.GetString(input.AsSpan(off1, len1)));

        var remaining = input.AsSpan(c1);
        var r2 = stream.TryReadChunk(remaining, out var c2, out _, out _);
        Assert.Equal(ChunkResult.Completed, r2);
        Assert.Equal(remaining.Length, c2);
    }

    [Fact]
    public void SingleCall_ValidMultiChunk()
    {
        var input = B("5\r\nHello\r\n6\r\n World\r\n0\r\n\r\n");
        var stream = new ChunkedBodyStream();

        var r1 = stream.TryReadChunk(input, out var c1, out var off1, out var len1);
        Assert.Equal(ChunkResult.Chunk, r1);
        Assert.Equal("Hello", Encoding.ASCII.GetString(input.AsSpan(off1, len1)));

        var span2 = input.AsSpan(c1);
        var r2 = stream.TryReadChunk(span2, out var c2, out var off2, out var len2);
        Assert.Equal(ChunkResult.Chunk, r2);
        Assert.Equal(" World", Encoding.ASCII.GetString(span2.Slice(off2, len2)));

        var span3 = span2[c2..];
        var r3 = stream.TryReadChunk(span3, out var c3, out _, out _);
        Assert.Equal(ChunkResult.Completed, r3);
        Assert.Equal(span3.Length, c3);
    }

    [Fact]
    public void SingleCall_EmptyBody()
    {
        var input = B("0\r\n\r\n");
        var stream = new ChunkedBodyStream();

        var r = stream.TryReadChunk(input, out var consumed, out _, out _);
        Assert.Equal(ChunkResult.Completed, r);
        Assert.Equal(input.Length, consumed);
    }

    [Fact]
    public void SingleCall_WithExtension()
    {
        var input = B("5;name=value\r\nHello\r\n0\r\n\r\n");
        var stream = new ChunkedBodyStream();

        var r1 = stream.TryReadChunk(input, out var c1, out var off1, out var len1);
        Assert.Equal(ChunkResult.Chunk, r1);
        Assert.Equal("Hello", Encoding.ASCII.GetString(input.AsSpan(off1, len1)));

        var remaining = input.AsSpan(c1);
        var r2 = stream.TryReadChunk(remaining, out _, out _, out _);
        Assert.Equal(ChunkResult.Completed, r2);
    }

    [Fact]
    public void SingleCall_WithTrailers()
    {
        var input = B("0\r\nTrailer: value\r\n\r\n");
        var stream = new ChunkedBodyStream();

        var r = stream.TryReadChunk(input, out var consumed, out _, out _);
        Assert.Equal(ChunkResult.Completed, r);
        Assert.Equal(input.Length, consumed);
    }

    [Fact]
    public void SingleCall_Malformed_Throws()
    {
        var input = B("-5\r\nHello\r\n0\r\n\r\n");
        var stream = new ChunkedBodyStream();

        Assert.Throws<HttpParseException>(() =>
            stream.TryReadChunk(input, out _, out _, out _));
    }

    // ==== Data slice correctness ====

    [Fact]
    public void DataSlice_CorrectOffsetAndLength()
    {
        var input = B("5\r\nHello\r\n0\r\n\r\n");
        var stream = new ChunkedBodyStream();

        stream.TryReadChunk(input, out _, out var off, out var len);

        // Verify the offset points exactly past "5\r\n"
        Assert.Equal(3, off);
        Assert.Equal(5, len);

        // Verify actual bytes
        var payload = input.AsSpan(off, len);
        Assert.Equal(B("Hello"), payload.ToArray());
    }

    [Fact]
    public void DataSlice_MultiChunk_EachSliceCorrect()
    {
        var input = B("3\r\nFoo\r\n4\r\nBar!\r\n0\r\n\r\n");
        var stream = new ChunkedBodyStream();

        // First chunk
        var r1 = stream.TryReadChunk(input, out var c1, out var off1, out var len1);
        Assert.Equal(ChunkResult.Chunk, r1);
        Assert.Equal("Foo", Encoding.ASCII.GetString(input.AsSpan(off1, len1)));

        // Second chunk (on the remaining span)
        var span2 = input.AsSpan(c1);
        var r2 = stream.TryReadChunk(span2, out var c2, out var off2, out var len2);
        Assert.Equal(ChunkResult.Chunk, r2);
        Assert.Equal("Bar!", Encoding.ASCII.GetString(span2.Slice(off2, len2)));

        // Terminal
        var span3 = span2[c2..];
        var r3 = stream.TryReadChunk(span3, out _, out _, out _);
        Assert.Equal(ChunkResult.Completed, r3);
    }

    // ==== Incremental ====

    [Fact]
    public void Incremental_ChunkSizeAcrossCalls()
    {
        // "1A\r\n" + 26 bytes + "\r\n" + "0\r\n\r\n"
        var data = new string('X', 26);
        var full = B($"1A\r\n{data}\r\n0\r\n\r\n");

        var stream = new ChunkedBodyStream();

        // First call: just "1" — not enough
        var part1 = B("1");
        var r1 = stream.TryReadChunk(part1, out var c1, out _, out _);
        Assert.Equal(ChunkResult.NeedMoreData, r1);
        Assert.Equal(0, c1);

        // Second call: full input
        var r2 = stream.TryReadChunk(full, out var c2, out var off2, out var len2);
        Assert.Equal(ChunkResult.Chunk, r2);
        Assert.Equal(26, len2);
        Assert.Equal(data, Encoding.ASCII.GetString(full.AsSpan(off2, len2)));
    }

    [Fact]
    public void Incremental_ChunkDataAcrossCalls()
    {
        var stream = new ChunkedBodyStream();

        // Send size line but only partial data
        var part1 = B("5\r\nHe");
        var r1 = stream.TryReadChunk(part1, out var c1, out _, out _);
        Assert.Equal(ChunkResult.NeedMoreData, r1);
        Assert.Equal(0, c1);

        // Now send the full chunk
        var part2 = B("5\r\nHello\r\n0\r\n\r\n");
        var r2 = stream.TryReadChunk(part2, out var c2, out var off2, out var len2);
        Assert.Equal(ChunkResult.Chunk, r2);
        Assert.Equal("Hello", Encoding.ASCII.GetString(part2.AsSpan(off2, len2)));
    }

    [Fact]
    public void Incremental_MultiChunkByteByByte()
    {
        var full = B("3\r\nFoo\r\n2\r\nHi\r\n0\r\n\r\n");
        var stream = new ChunkedBodyStream();

        var chunks = new List<string>();
        int globalPos = 0;

        while (globalPos < full.Length)
        {
            // Feed one byte at a time, but present the full remaining buffer
            // since NeedMoreData means "don't advance, get more data"
            // We simulate by growing the window by 1 byte each iteration.
            var window = full.AsSpan(0, globalPos + 1);

            // We need to re-present from where the parser last returned NeedMoreData.
            // Since bytesConsumed is 0 on NeedMoreData, we just grow window.
            // But after Chunk, we advance. Track total consumed.
            break; // See byte-at-a-time strategy below.
        }

        // Better approach: accumulate bytes and always present full unconsumed buffer
        var buffer = new List<byte>();
        stream = new ChunkedBodyStream();
        chunks.Clear();
        bool completed = false;

        foreach (byte b in full)
        {
            buffer.Add(b);
            var span = buffer.ToArray().AsSpan();

        retry:
            var r = stream.TryReadChunk(span, out var consumed, out var off, out var len);
            if (r == ChunkResult.Chunk)
            {
                chunks.Add(Encoding.ASCII.GetString(span.Slice(off, len)));
                buffer.RemoveRange(0, consumed);
                span = buffer.ToArray().AsSpan();
                if (span.Length > 0) goto retry;
            }
            else if (r == ChunkResult.Completed)
            {
                completed = true;
                break;
            }
            // NeedMoreData → continue adding bytes
        }

        Assert.True(completed);
        Assert.Equal(2, chunks.Count);
        Assert.Equal("Foo", chunks[0]);
        Assert.Equal("Hi", chunks[1]);
    }

    [Fact]
    public void Incremental_TerminalChunkSplitFromTrailers()
    {
        var stream = new ChunkedBodyStream();

        // First: "0\r\n" — terminal chunk size, but no trailing CRLF yet
        var part1 = B("0\r\n");
        var r1 = stream.TryReadChunk(part1, out var c1, out _, out _);
        Assert.Equal(ChunkResult.NeedMoreData, r1);
        Assert.Equal(0, c1);

        // Second: full "0\r\n\r\n"
        var part2 = B("0\r\n\r\n");
        var r2 = stream.TryReadChunk(part2, out var c2, out _, out _);
        Assert.Equal(ChunkResult.Completed, r2);
        Assert.Equal(part2.Length, c2);
    }

    // ==== Error mid-stream ====

    [Fact]
    public void Incremental_MalformedAfterValidChunks()
    {
        var stream = new ChunkedBodyStream();

        // First chunk is valid
        var part1 = B("5\r\nHello\r\n");
        var r1 = stream.TryReadChunk(part1, out var c1, out _, out _);
        Assert.Equal(ChunkResult.Chunk, r1);

        // Second chunk is malformed (negative size)
        var part2 = B("-3\r\nFoo\r\n0\r\n\r\n");
        Assert.Throws<HttpParseException>(() =>
            stream.TryReadChunk(part2, out _, out _, out _));
    }
}
