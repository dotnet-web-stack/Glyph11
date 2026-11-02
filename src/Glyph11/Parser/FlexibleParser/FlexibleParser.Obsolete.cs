using System.Buffers;
using Glyph11.Protocol;

namespace Glyph11.Parser.FlexibleParser;

// ---- Legacy entry points ----
// These report one less than the bytes consumed, the same convention as UltraHardenedParser's legacy
// entry points (see UltraHardenedParser.Obsolete.cs for why they were renamed rather than corrected in
// place). Unchanged in behaviour; slated for removal in a later release.
public static partial class FlexibleParser
{
    private const string ObsoleteMessage =
        "Reports one less than the bytes consumed. Use TryParseRequestHeader, whose 'consumed' is the " +
        "full header length: the body starts at consumed, not consumed + 1.";

    /// <summary>
    /// Zero-copy single-segment parse path.
    /// All parsed fields are <see cref="ReadOnlyMemory{T}"/> slices into <paramref name="input"/>.
    /// </summary>
    /// <param name="input">Contiguous buffer containing the full HTTP request header.</param>
    /// <param name="request">Target to populate with parsed method, path, headers, etc.</param>
    /// <param name="bytesReadCount">
    /// One less than the bytes consumed on success (the body starts at <c>bytesReadCount + 1</c>),
    /// or -1 if incomplete.
    /// </param>
    /// <returns><c>true</c> if a complete header was parsed; <c>false</c> if the <c>\r\n\r\n</c> terminator was not found.</returns>
    /// <remarks>
    /// Obsolete: use <see cref="TryParseRequestHeader(ref ReadOnlyMemory{byte}, BinaryRequest, out int)"/>,
    /// which reports the header block length itself — drop the <c>+ 1</c> when migrating.
    /// </remarks>
    [Obsolete(ObsoleteMessage)]
    public static bool TryExtractFullHeaderReadOnlyMemory(ref ReadOnlyMemory<byte> input, BinaryRequest request, out int bytesReadCount)
    {
        bool complete = TryParseRequestHeader(ref input, request, out int consumed);
        bytesReadCount = complete ? consumed - 1 : -1;
        return complete;
    }

    /// <summary>
    /// Entry point: tries to extract a complete HTTP/1.1 header block (request line + headers).
    /// <para>
    /// Single-segment input is dispatched to the zero-copy ROM path.
    /// Multi-segment input is checked for completeness (<c>\r\n\r\n</c>), then linearized
    /// via <c>ToArray()</c> and parsed through the ROM path.
    /// </para>
    /// </summary>
    /// <param name="input">Input buffer from the network layer.</param>
    /// <param name="request">Target to populate with parsed request data.</param>
    /// <param name="bytesReadCount">
    /// One less than the bytes consumed on success (the body starts at <c>bytesReadCount + 1</c>),
    /// or -1 if incomplete.
    /// </param>
    /// <returns><c>true</c> if a complete header was parsed; <c>false</c> if more data is needed.</returns>
    /// <remarks>
    /// Obsolete: use <see cref="TryParseRequestHeader(ref ReadOnlySequence{byte}, BinaryRequest, out int)"/>,
    /// which reports the header block length itself — drop the <c>+ 1</c> when migrating.
    /// </remarks>
    [Obsolete(ObsoleteMessage)]
    public static bool TryExtractFullHeader(ref ReadOnlySequence<byte> input, BinaryRequest request, out int bytesReadCount)
    {
        bool complete = TryParseRequestHeader(ref input, request, out int consumed);
        bytesReadCount = complete ? consumed - 1 : -1;
        return complete;
    }
}
