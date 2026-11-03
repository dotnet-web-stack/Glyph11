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
    /// <param name="consumed">
    /// On success, the length of the header block including its terminating CRLFCRLF: the body,
    /// if any, starts at <c>input.Slice(consumed)</c>. 0 when the method returns <c>false</c>.
    /// </param>
    /// <returns><c>true</c> if a complete header was parsed; <c>false</c> if more data is needed.</returns>
    public static bool TryParseRequestHeader(ref ReadOnlySequence<byte> input, BinaryRequest request, out int consumed)
    {
        if (input.IsSingleSegment)
        {
            ReadOnlyMemory<byte> singleMemorySegment = input.First;
            return TryParseRequestHeader(ref singleMemorySegment, request, out consumed);
        }

        // Check for header completeness before allocating
        var reader = new SequenceReader<byte>(input);
        if (!reader.TryReadTo(out ReadOnlySequence<byte> _, CrlfCrlf, advancePastDelimiter: true))
        {
            consumed = 0;
            return false;
        }

        // Linearize the header block only — never the pipelined body behind it.
        // reader.Consumed is the block's length, CRLFCRLF included.
        ReadOnlyMemory<byte> mem = input.Slice(0, reader.Consumed).ToArray();
        return TryParseRequestHeader(ref mem, request, out consumed);
    }
}
