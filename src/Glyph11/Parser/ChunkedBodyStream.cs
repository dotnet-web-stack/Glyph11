using System.Buffers;

namespace Glyph11.Parser;

/// <summary>
/// Result of a single <see cref="ChunkedBodyStream.TryReadChunk"/> call.
/// </summary>
public enum ChunkResult : byte
{
    /// <summary>Incomplete data — wait for more bytes from the pipe.</summary>
    NeedMoreData,

    /// <summary>
    /// Decoded payload bytes are available: a whole chunk, or the part of one that has arrived so
    /// far (never empty). Chunk boundaries carry no meaning, so append the bytes and call again.
    /// </summary>
    Chunk,

    /// <summary>Terminal chunk (and any trailers) consumed — body is done.</summary>
    Completed,
}

/// <summary>
/// Stateful streaming parser for chunked transfer-encoding (RFC 9112 §7.1).
/// Validates chunks incrementally so the caller can advance a PipeReader
/// after each call without re-parsing from byte 0.
/// <para>
/// Chunk data is handed out as it arrives rather than once the whole chunk is buffered, so the
/// caller's buffer is bounded by its read window, not by the size a peer declares. The trailer
/// section is capped by <see cref="ParserLimits.MaxTotalTrailerBytes"/>.
/// </para>
/// </summary>
public struct ChunkedBodyStream
{
    private const int MaxChunkExtensionBytes = 4096;

    private enum Phase : byte
    {
        ChunkSize,
        Extension,
        HeaderCrlf,
        ChunkData,
        DataCrlf,
        Trailers,
        Complete,
    }

    // The limit fields sit in the padding after the byte-sized phase, keeping the state at the
    // 32 bytes it had: every call copies it (the NeedMoreData rollback snapshot).
    private Phase _phase;          // starts at ChunkSize
    private bool _hasLimits;       // false for new ChunkedBodyStream(): ParserLimits.Default applies
    private int _maxTrailerBytes;  // trailer section cap, when constructed with limits
    private long _chunkSize;       // parsed size for current chunk
    private long _remaining;       // bytes left in ChunkData phase
    private int _digitCount;       // hex digits seen in ChunkSize
    private int _extensionBytes;   // extension bytes (4096 limit)

    /// <summary>
    /// Creates a decoder that enforces <paramref name="limits"/> (today:
    /// <see cref="ParserLimits.MaxTotalTrailerBytes"/>). <c>new ChunkedBodyStream()</c> applies
    /// <see cref="ParserLimits.Default"/>.
    /// </summary>
    public ChunkedBodyStream(in ParserLimits limits) : this()
    {
        _maxTrailerBytes = limits.MaxTotalTrailerBytes;
        _hasLimits = true;
    }

    private readonly int MaxTrailerBytes
        => _hasLimits ? _maxTrailerBytes : ParserLimits.Default.MaxTotalTrailerBytes;

    /// <summary>
    /// Reads the next chunk from <paramref name="input"/>.
    /// Call in a loop after each PipeReader.ReadAsync.
    /// </summary>
    /// <param name="input">The unprocessed bytes from the pipe.</param>
    /// <param name="bytesConsumed">Total bytes consumed from <paramref name="input"/> by this call.</param>
    /// <param name="dataOffset">Offset within <paramref name="input"/> where chunk payload starts (valid when result is <see cref="ChunkResult.Chunk"/>).</param>
    /// <param name="dataLength">Length of chunk payload bytes (valid when result is <see cref="ChunkResult.Chunk"/>).</param>
    /// <returns>
    /// <see cref="ChunkResult.Chunk"/>: payload at input[dataOffset..dataOffset+dataLength] — a whole chunk or the
    /// part of one that has arrived, never empty — advance by bytesConsumed.<br/>
    /// <see cref="ChunkResult.Completed"/>: terminal chunk consumed, body is done.<br/>
    /// <see cref="ChunkResult.NeedMoreData"/>: incomplete, nothing consumed — wait for more from pipe and
    /// re-present the same bytes.
    /// </returns>
    /// <exception cref="HttpParseException">
    /// Thrown on malformed chunked encoding, or (status 431) when the trailer section exceeds
    /// <see cref="ParserLimits.MaxTotalTrailerBytes"/>.
    /// </exception>
    public ChunkResult TryReadChunk(
        ReadOnlySpan<byte> input,
        out int bytesConsumed,
        out int dataOffset,
        out int dataLength)
    {
        bytesConsumed = 0;
        dataOffset = 0;
        dataLength = 0;
        int pos = 0;
        int trailerStart = 0;   // where the trailer section begins; it is always parsed within one call

        // Snapshot state so we can roll back on NeedMoreData.
        // The caller re-presents the same bytes, so partial state must not persist.
        var snapshot = this;

        while (pos < input.Length)
        {
            switch (_phase)
            {
                case Phase.ChunkSize:
                {
                    // Reject leading whitespace (only at start of size, i.e. no digits yet)
                    if (_digitCount == 0 && (input[pos] == (byte)' ' || input[pos] == (byte)'\t'))
                        throw new HttpParseException("Leading whitespace in chunk size.");

                    // Reject negative sign
                    if (_digitCount == 0 && input[pos] == (byte)'-')
                        throw new HttpParseException("Negative chunk size.");

                    // Reject 0x prefix
                    if (_digitCount == 1 && _chunkSize == 0 &&
                        (input[pos] == (byte)'x' || input[pos] == (byte)'X'))
                        throw new HttpParseException("Hex prefix in chunk size.");

                    if (IsHexDigit(input[pos]))
                    {
                        if (_digitCount >= 16)
                            throw new HttpParseException("Chunk size overflow.");

                        _chunkSize = (_chunkSize << 4) | (uint)HexVal(input[pos]);
                        _digitCount++;
                        pos++;
                        continue;
                    }

                    // Non-hex character
                    if (_digitCount == 0)
                        throw new HttpParseException("Invalid character in chunk size.");

                    // Reject underscore after digits
                    if (input[pos] == (byte)'_')
                        throw new HttpParseException("Underscore in chunk size.");

                    if (input[pos] == (byte)';')
                    {
                        _extensionBytes = 0;
                        _phase = Phase.Extension;
                        pos++; // skip ';'
                        continue;
                    }

                    if (input[pos] == (byte)'\n')
                        throw new HttpParseException("Bare LF after chunk size.");

                    if (input[pos] == (byte)'\r')
                    {
                        _phase = Phase.HeaderCrlf;
                        pos++;
                        continue;
                    }

                    throw new HttpParseException("Missing CRLF after chunk size.");
                }

                case Phase.Extension:
                {
                    if (_extensionBytes > MaxChunkExtensionBytes)
                        throw new HttpParseException("Chunk extension too large.");

                    if (input[pos] == 0)
                        throw new HttpParseException("NUL byte in chunk extension.");

                    if (input[pos] == (byte)'\n')
                        throw new HttpParseException("Bare LF in chunk extension.");

                    if (input[pos] == (byte)'\r')
                    {
                        if (_extensionBytes == 0)
                            throw new HttpParseException("Bare semicolon with no chunk extension name.");

                        _phase = Phase.HeaderCrlf;
                        pos++;
                        continue;
                    }

                    _extensionBytes++;
                    pos++;
                    continue;
                }

                case Phase.HeaderCrlf:
                {
                    // CR was already consumed, expect LF
                    if (input[pos] != (byte)'\n')
                        throw new HttpParseException("Missing CRLF after chunk size.");

                    pos++;

                    if (_chunkSize == 0)
                    {
                        // Terminal chunk — move to trailers
                        _phase = Phase.Trailers;
                        trailerStart = pos;
                        continue;
                    }

                    _remaining = _chunkSize;
                    _phase = Phase.ChunkData;
                    dataOffset = pos;
                    continue;
                }

                case Phase.ChunkData:
                {
                    int available = input.Length - pos;   // >= 1: the loop only runs while input remains
                    if (available < _remaining)
                    {
                        // Hand out what has arrived instead of waiting for the whole chunk: the
                        // caller's buffer is then bounded by its read window, not by whatever size
                        // the peer declared. The rest of the chunk follows on later calls.
                        dataOffset = pos;
                        dataLength = available;
                        _remaining -= available;
                        bytesConsumed = input.Length;
                        return ChunkResult.Chunk;
                    }

                    // The rest of the chunk's data is present
                    dataOffset = pos;
                    dataLength = (int)_remaining;
                    pos += (int)_remaining;
                    _remaining = 0;
                    _phase = Phase.DataCrlf;
                    continue;
                }

                case Phase.DataCrlf:
                {
                    if (pos + 1 >= input.Length)
                    {
                        // The CRLF after the data has not arrived. Data read by this call is
                        // handed out anyway — the CRLF is checked on the next call.
                        if (dataLength > 0)
                        {
                            bytesConsumed = pos;
                            return ChunkResult.Chunk;
                        }

                        this = snapshot;
                        return ChunkResult.NeedMoreData;
                    }

                    if (input[pos] == (byte)'\n' && (pos == 0 || input[pos - 1] != (byte)'\r'))
                        throw new HttpParseException("Bare LF after chunk data.");

                    if (input[pos] != (byte)'\r' || input[pos + 1] != (byte)'\n')
                        throw new HttpParseException("Missing CRLF after chunk data.");

                    pos += 2;

                    // Reset per-chunk state
                    _phase = Phase.ChunkSize;
                    _chunkSize = 0;
                    _digitCount = 0;
                    _extensionBytes = 0;

                    if (dataLength > 0)
                    {
                        bytesConsumed = pos;
                        return ChunkResult.Chunk;
                    }

                    // The data was handed out by an earlier call: go on to the next chunk.
                    continue;
                }

                case Phase.Trailers:
                {
                    // Check for empty line = end of trailers
                    if (pos + 1 < input.Length &&
                        input[pos] == (byte)'\r' && input[pos + 1] == (byte)'\n')
                    {
                        pos += 2;
                        _phase = Phase.Complete;
                        bytesConsumed = pos;
                        return ChunkResult.Completed;
                    }

                    if (input[pos] == (byte)'\n')
                        throw new HttpParseException("Bare LF in chunked trailer.");

                    // Read a trailer line until CRLF
                    while (pos < input.Length)
                    {
                        if (input[pos] == (byte)'\n' && (pos == 0 || input[pos - 1] != (byte)'\r'))
                            throw new HttpParseException("Bare LF in chunked trailer.");

                        if (pos + 1 < input.Length &&
                            input[pos] == (byte)'\r' && input[pos + 1] == (byte)'\n')
                        {
                            pos += 2;
                            // Back to top of Trailers to check for another trailer or empty line
                            break;
                        }

                        pos++;
                    }

                    // The trailer section is re-presented from its start until it completes, so
                    // cap the bytes seen so far — finished or not — or an endless section is
                    // buffered forever. A CR that ends the input may be half of the final CRLF,
                    // so it is not counted yet.
                    int seen = pos - trailerStart;
                    if (pos >= input.Length && input[pos - 1] == (byte)'\r')
                        seen--;
                    if (seen > MaxTrailerBytes)
                        throw new HttpParseException("Chunked trailer section exceeds limit.", statusCode: 431);

                    if (pos >= input.Length)
                    {
                        this = snapshot;
                        return ChunkResult.NeedMoreData;
                    }

                    continue;
                }

                case Phase.Complete:
                {
                    bytesConsumed = 0;
                    return ChunkResult.Completed;
                }
            }
        }

        // Ran out of input. A chunk whose data is all here but whose CRLF is not is still
        // handed out (the CRLF is checked on the next call); anything else waits.
        if (dataLength > 0)
        {
            bytesConsumed = pos;
            return ChunkResult.Chunk;
        }

        this = snapshot;
        return ChunkResult.NeedMoreData;
    }

    /// <summary>
    /// Multi-segment overload of <see cref="TryReadChunk(ReadOnlySpan{byte}, out int, out int, out int)"/>.
    /// Reads the next chunk directly from a <see cref="ReadOnlySequence{T}"/> — <b>no linearization</b>.
    /// On <see cref="ChunkResult.Chunk"/>, <paramref name="chunkData"/> is the decoded payload as a
    /// (possibly multi-segment) slice of <paramref name="input"/> — a whole chunk or the part of one
    /// that has arrived, never empty; copy it out before advancing the reader past
    /// <paramref name="bytesConsumed"/>.
    /// </summary>
    /// <exception cref="HttpParseException">
    /// Thrown on malformed chunked encoding, or (status 431) when the trailer section exceeds
    /// <see cref="ParserLimits.MaxTotalTrailerBytes"/>.
    /// </exception>
    public ChunkResult TryReadChunk(
        in ReadOnlySequence<byte> input,
        out long bytesConsumed,
        out ReadOnlySequence<byte> chunkData)
    {
        bytesConsumed = 0;
        chunkData = default;

        var reader = new SequenceReader<byte>(input);
        var snapshot = this;                 // roll back on NeedMoreData — caller re-presents the bytes
        ReadOnlySequence<byte> pending = default;
        bool hasPending = false;          // cheaper to test than pending.IsEmpty, once per chunk
        long trailerStart = 0;               // the trailer section is always parsed within one call

        while (!reader.End)
        {
            switch (_phase)
            {
                case Phase.ChunkSize:
                {
                    reader.TryPeek(out byte b);

                    if (_digitCount == 0 && (b == (byte)' ' || b == (byte)'\t'))
                        throw new HttpParseException("Leading whitespace in chunk size.");
                    if (_digitCount == 0 && b == (byte)'-')
                        throw new HttpParseException("Negative chunk size.");
                    if (_digitCount == 1 && _chunkSize == 0 && (b == (byte)'x' || b == (byte)'X'))
                        throw new HttpParseException("Hex prefix in chunk size.");

                    if (IsHexDigit(b))
                    {
                        if (_digitCount >= 16)
                            throw new HttpParseException("Chunk size overflow.");
                        _chunkSize = (_chunkSize << 4) | (uint)HexVal(b);
                        _digitCount++;
                        reader.Advance(1);
                        continue;
                    }

                    if (_digitCount == 0)
                        throw new HttpParseException("Invalid character in chunk size.");
                    if (b == (byte)'_')
                        throw new HttpParseException("Underscore in chunk size.");
                    if (b == (byte)';')
                    {
                        _extensionBytes = 0;
                        _phase = Phase.Extension;
                        reader.Advance(1);
                        continue;
                    }
                    if (b == (byte)'\n')
                        throw new HttpParseException("Bare LF after chunk size.");
                    if (b == (byte)'\r')
                    {
                        _phase = Phase.HeaderCrlf;
                        reader.Advance(1);
                        continue;
                    }
                    throw new HttpParseException("Missing CRLF after chunk size.");
                }

                case Phase.Extension:
                {
                    reader.TryPeek(out byte b);
                    if (_extensionBytes > MaxChunkExtensionBytes)
                        throw new HttpParseException("Chunk extension too large.");
                    if (b == 0)
                        throw new HttpParseException("NUL byte in chunk extension.");
                    if (b == (byte)'\n')
                        throw new HttpParseException("Bare LF in chunk extension.");
                    if (b == (byte)'\r')
                    {
                        if (_extensionBytes == 0)
                            throw new HttpParseException("Bare semicolon with no chunk extension name.");
                        _phase = Phase.HeaderCrlf;
                        reader.Advance(1);
                        continue;
                    }
                    _extensionBytes++;
                    reader.Advance(1);
                    continue;
                }

                case Phase.HeaderCrlf:
                {
                    reader.TryPeek(out byte b);
                    if (b != (byte)'\n')
                        throw new HttpParseException("Missing CRLF after chunk size.");
                    reader.Advance(1);

                    if (_chunkSize == 0)
                    {
                        _phase = Phase.Trailers;
                        trailerStart = reader.Consumed;
                        continue;
                    }
                    _remaining = _chunkSize;
                    _phase = Phase.ChunkData;
                    continue;
                }

                case Phase.ChunkData:
                {
                    if (reader.Remaining < _remaining)
                    {
                        // Hand out what has arrived (at least one byte: the loop runs only while
                        // input remains) instead of buffering the whole declared chunk.
                        long available = reader.Remaining;
                        chunkData = reader.UnreadSequence;
                        reader.Advance(available);
                        _remaining -= available;
                        bytesConsumed = reader.Consumed;
                        return ChunkResult.Chunk;
                    }
                    pending = reader.UnreadSequence.Slice(0, _remaining);
                    hasPending = true;
                    reader.Advance(_remaining);
                    _remaining = 0;
                    _phase = Phase.DataCrlf;
                    continue;
                }

                case Phase.DataCrlf:
                {
                    if (reader.Remaining < 2)
                    {
                        // The CRLF after the data has not arrived: hand out data read by this call
                        // anyway — the CRLF is checked on the next call.
                        if (hasPending)
                        {
                            bytesConsumed = reader.Consumed;
                            chunkData = pending;
                            return ChunkResult.Chunk;
                        }

                        this = snapshot;
                        return ChunkResult.NeedMoreData;
                    }
                    reader.TryPeek(out byte b);
                    if (b == (byte)'\n')
                        throw new HttpParseException("Bare LF after chunk data.");
                    if (!reader.IsNext(ParserConstants.Crlf, advancePast: true))
                        throw new HttpParseException("Missing CRLF after chunk data.");

                    _phase = Phase.ChunkSize;
                    _chunkSize = 0;
                    _digitCount = 0;
                    _extensionBytes = 0;

                    if (hasPending)
                    {
                        bytesConsumed = reader.Consumed;
                        chunkData = pending;
                        return ChunkResult.Chunk;
                    }

                    // The data was handed out by an earlier call: go on to the next chunk.
                    continue;
                }

                case Phase.Trailers:
                {
                    // empty line = end of trailers
                    if (reader.IsNext(ParserConstants.Crlf, advancePast: true))
                    {
                        _phase = Phase.Complete;
                        bytesConsumed = reader.Consumed;
                        return ChunkResult.Completed;
                    }

                    reader.TryPeek(out byte b);
                    if (b == (byte)'\n')
                        throw new HttpParseException("Bare LF in chunked trailer.");

                    // a trailer line, up to CRLF; a bare LF inside it is invalid
                    if (!reader.TryReadTo(out ReadOnlySequence<byte> line, ParserConstants.Crlf, advancePastDelimiter: true))
                    {
                        // Re-presented from the start of the section until it completes: cap the
                        // bytes seen so far, or an endless trailer line is buffered forever. A CR
                        // that ends the input may be half of the final CRLF: not counted yet.
                        var unread = reader.UnreadSequence;
                        long seen = reader.Consumed - trailerStart + unread.Length;
                        if (unread.Slice(unread.Length - 1).FirstSpan[0] == (byte)'\r')
                            seen--;
                        if (seen > MaxTrailerBytes)
                            throw new HttpParseException("Chunked trailer section exceeds limit.", statusCode: 431);

                        this = snapshot;
                        return ChunkResult.NeedMoreData;
                    }
                    if (line.PositionOf((byte)'\n') is not null)
                        throw new HttpParseException("Bare LF in chunked trailer.");
                    if (reader.Consumed - trailerStart > MaxTrailerBytes)
                        throw new HttpParseException("Chunked trailer section exceeds limit.", statusCode: 431);
                    continue;
                }

                case Phase.Complete:
                {
                    bytesConsumed = 0;
                    return ChunkResult.Completed;
                }
            }
        }

        // Ran out of input. A chunk whose data is all here but whose CRLF is not is still
        // handed out (the CRLF is checked on the next call); anything else waits.
        if (hasPending)
        {
            bytesConsumed = reader.Consumed;
            chunkData = pending;
            return ChunkResult.Chunk;
        }

        this = snapshot;
        return ChunkResult.NeedMoreData;
    }

    private static bool IsHexDigit(byte b)
        => (b >= (byte)'0' && b <= (byte)'9') ||
           (b >= (byte)'a' && b <= (byte)'f') ||
           (b >= (byte)'A' && b <= (byte)'F');

    private static int HexVal(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => 0,
    };
}
