using System.Buffers;
using Glyph11.Protocol;

namespace Glyph11.Parser.UltraHardened;

public static partial class UltraHardenedParser
{
    /// <summary>
    /// Entry point: combined parse + semantic validation with full security checks.
    /// <para>
    /// Single-segment input is dispatched to the zero-copy validated ROM path.
    /// Multi-segment input is checked for completeness (<c>\r\n\r\n</c>) and against
    /// <see cref="ParserLimits.MaxTotalHeaderBytes"/>, then its header block alone is linearized
    /// (a pipelined body is never copied) and parsed through the validated ROM path.
    /// </para>
    /// </summary>
    /// <param name="input">Input buffer from the network layer.</param>
    /// <param name="request">Target to populate with parsed request data.</param>
    /// <param name="limits">Resource limits to enforce during parsing.</param>
    /// <param name="bytesReadCount">Bytes consumed on success, or -1 if incomplete.</param>
    /// <returns><c>true</c> if a complete header was parsed and validated; <c>false</c> if more data is needed.</returns>
    /// <exception cref="HttpParseException">Thrown on any protocol or semantic violation.</exception>
    public static bool TryExtractFullHeaderValidated(
        ref ReadOnlySequence<byte> input, BinaryRequest request,
        in ParserLimits limits, out int bytesReadCount)
    {
        if (input.IsSingleSegment)
        {
            ReadOnlyMemory<byte> singleMemorySegment = input.First;
            return TryExtractFullHeaderROM(ref singleMemorySegment, request, in limits, out bytesReadCount);
        }

        // Check for header completeness before allocating
        var reader = new SequenceReader<byte>(input);
        if (!reader.TryReadTo(out ReadOnlySequence<byte> _, ParserConstants.CrlfCrlf, advancePastDelimiter: true))
        {
            bytesReadCount = -1;
            return false;
        }

        // Linearize the header block only — never the pipelined body behind it — and only once it
        // is known to fit: reader.Consumed is the block's length, CRLFCRLF included.
        if (reader.Consumed > limits.MaxTotalHeaderBytes)
            throw new HttpParseException("Total header size exceeds limit.", statusCode: 431);

        ReadOnlyMemory<byte> mem = input.Slice(0, reader.Consumed).ToArray();
        return TryExtractFullHeaderROM(ref mem, request, in limits, out bytesReadCount);
    }
}
