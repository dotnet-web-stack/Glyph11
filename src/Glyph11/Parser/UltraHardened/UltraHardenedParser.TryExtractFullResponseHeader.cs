using System.Buffers;
using Glyph11.Protocol;

namespace Glyph11.Parser.UltraHardened;

public static partial class UltraHardenedParser
{
    /// <summary>
    /// Entry point for RESPONSE headers: combined parse + semantic validation with full security
    /// checks.
    /// <para>
    /// Single-segment input is dispatched to the zero-copy validated ROM path.
    /// Multi-segment input is checked for completeness (<c>\r\n\r\n</c>), then linearized
    /// via <c>ToArray()</c> and parsed through the validated ROM path.
    /// </para>
    /// </summary>
    /// <param name="input">Input buffer from the network layer.</param>
    /// <param name="response">Target to populate with parsed response data.</param>
    /// <param name="limits">Resource limits to enforce during parsing.</param>
    /// <param name="bytesReadCount">Bytes consumed on success, or -1 if incomplete.</param>
    /// <returns><c>true</c> if a complete header was parsed and validated; <c>false</c> if more data is needed.</returns>
    /// <exception cref="HttpParseException">Thrown on any protocol or semantic violation.</exception>
    /// <remarks>
    /// A client that already holds the response in one contiguous buffer should call
    /// <see cref="TryExtractFullResponseHeaderROM"/> directly and skip the linearizing copy — a
    /// response header spanning segments is the common case on a socket, not the rare one.
    /// </remarks>
    public static bool TryExtractFullResponseHeaderValidated(
        ref ReadOnlySequence<byte> input, BinaryResponse response,
        in ParserLimits limits, out int bytesReadCount)
    {
        if (input.IsSingleSegment)
        {
            ReadOnlyMemory<byte> singleMemorySegment = input.First;
            return TryExtractFullResponseHeaderROM(ref singleMemorySegment, response, in limits, out bytesReadCount);
        }

        // Check for header completeness before allocating
        var reader = new SequenceReader<byte>(input);
        if (!reader.TryReadTo(out ReadOnlySequence<byte> _, ParserConstants.CrlfCrlf, advancePastDelimiter: true))
        {
            bytesReadCount = -1;
            return false;
        }

        // Linearize: copy all segments into a single contiguous array, then parse via ROM
        ReadOnlyMemory<byte> mem = input.ToArray();
        return TryExtractFullResponseHeaderROM(ref mem, response, in limits, out bytesReadCount);
    }
}
