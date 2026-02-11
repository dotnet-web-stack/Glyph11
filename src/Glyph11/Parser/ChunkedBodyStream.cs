namespace Glyph11.Parser;

/// <summary>
/// Result of a single <see cref="ChunkedBodyStream.TryReadChunk"/> call.
/// </summary>
public enum ChunkResult : byte
{
    /// <summary>Incomplete data — wait for more bytes from the pipe.</summary>
    NeedMoreData,

    /// <summary>A complete chunk's decoded payload is available.</summary>
    Chunk,

    /// <summary>Terminal chunk (and any trailers) consumed — body is done.</summary>
    Completed,
}

/// <summary>
/// Stateful streaming parser for chunked transfer-encoding (RFC 9112 §7.1).
/// Validates chunks incrementally so the caller can advance a PipeReader
/// after each call without re-parsing from byte 0.
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

    private Phase _phase;          // starts at ChunkSize
    private long _chunkSize;       // parsed size for current chunk
    private long _remaining;       // bytes left in ChunkData phase
    private int _digitCount;       // hex digits seen in ChunkSize
    private int _extensionBytes;   // extension bytes (4096 limit)

    /// <summary>
    /// Reads the next chunk from <paramref name="input"/>.
    /// Call in a loop after each PipeReader.ReadAsync.
    /// </summary>
    /// <param name="input">The unprocessed bytes from the pipe.</param>
    /// <param name="bytesConsumed">Total bytes consumed from <paramref name="input"/> by this call.</param>
    /// <param name="dataOffset">Offset within <paramref name="input"/> where chunk payload starts (valid when result is <see cref="ChunkResult.Chunk"/>).</param>
    /// <param name="dataLength">Length of chunk payload bytes (valid when result is <see cref="ChunkResult.Chunk"/>).</param>
    /// <returns>
    /// <see cref="ChunkResult.Chunk"/>: payload at input[dataOffset..dataOffset+dataLength], advance by bytesConsumed.<br/>
    /// <see cref="ChunkResult.Completed"/>: terminal chunk consumed, body is done.<br/>
    /// <see cref="ChunkResult.NeedMoreData"/>: incomplete, wait for more from pipe.
    /// </returns>
    /// <exception cref="HttpParseException">Thrown on malformed chunked encoding.</exception>
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
                        continue;
                    }

                    _remaining = _chunkSize;
                    _phase = Phase.ChunkData;
                    dataOffset = pos;
                    continue;
                }

                case Phase.ChunkData:
                {
                    int available = input.Length - pos;
                    if (available < _remaining)
                    {
                        this = snapshot;
                        return ChunkResult.NeedMoreData;
                    }

                    // All chunk data is present
                    dataOffset = pos;
                    dataLength = (int)_chunkSize;
                    pos += (int)_remaining;
                    _remaining = 0;
                    _phase = Phase.DataCrlf;
                    continue;
                }

                case Phase.DataCrlf:
                {
                    if (pos + 1 >= input.Length)
                    {
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

                    bytesConsumed = pos;
                    return ChunkResult.Chunk;
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

        // Ran out of input
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
