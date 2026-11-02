using System.Buffers;
using Glyph11.Protocol;

namespace Glyph11.Parser.FlexibleParser;

public static partial class FlexibleParser
{
    /// <summary>
    /// Entry point: tries to extract a complete HTTP/1.1 header block (request line + headers).
    /// <para>
    /// Single-segment input is dispatched to the zero-copy ROM path.
    /// Multi-segment input is checked for completeness (<c>\r\n\r\n</c>), then its header block
    /// alone is linearized (a pipelined body is never copied) and parsed through the ROM path.
    /// </para>
    /// </summary>
    /// <param name="input">Input buffer from the network layer.</param>
    /// <param name="request">Target to populate with parsed request data.</param>
    /// <param name="bytesReadCount">Bytes consumed on success, or -1 if incomplete.</param>
    /// <returns><c>true</c> if a complete header was parsed; <c>false</c> if more data is needed.</returns>
    public static bool TryExtractFullHeader(ref ReadOnlySequence<byte> input, BinaryRequest request, out int bytesReadCount)
    {
        if (input.IsSingleSegment)
        {
            ReadOnlyMemory<byte> singleMemorySegment = input.First;
            return TryExtractFullHeaderReadOnlyMemory(ref singleMemorySegment, request, out bytesReadCount);
        }

        // Check for header completeness before allocating
        var reader = new SequenceReader<byte>(input);
        if (!reader.TryReadTo(out ReadOnlySequence<byte> _, CrlfCrlf, advancePastDelimiter: true))
        {
            bytesReadCount = -1;
            return false;
        }

        // Linearize the header block only — never the pipelined body behind it.
        // reader.Consumed is the block's length, CRLFCRLF included.
        ReadOnlyMemory<byte> mem = input.Slice(0, reader.Consumed).ToArray();
        return TryExtractFullHeaderReadOnlyMemory(ref mem, request, out bytesReadCount);
    }
}
