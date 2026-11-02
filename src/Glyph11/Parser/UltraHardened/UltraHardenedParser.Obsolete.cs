using System.Buffers;
using Glyph11.Protocol;

namespace Glyph11.Parser.UltraHardened;

// ---- Legacy entry points ----
// These report one less than the bytes consumed, a convention every caller compensated for with
// "bytesRead + 1". Correcting it in place would have broken those callers silently: the first body
// byte would be read as header and a Content-Length body would arrive one byte short. So the
// correctly-specified methods got new names (TryParseRequestHeader / TryParseResponseHeader) and these
// stay, unchanged in behaviour, with an [Obsolete] warning that makes the migration visible at
// compile time. Slated for removal in a later release.
public static partial class UltraHardenedParser
{
    private const string ObsoleteRequestMessage =
        "Reports one less than the bytes consumed. Use TryParseRequestHeader, whose 'consumed' is the " +
        "full header length: the body starts at consumed, not consumed + 1.";

    private const string ObsoleteResponseMessage =
        "Reports one less than the bytes consumed. Use TryParseResponseHeader, whose 'consumed' is the " +
        "full header length: the body starts at consumed, not consumed + 1.";

    /// <summary>
    /// Combined parse + semantic validation — single-segment hot path.
    /// Performs a full structural parse plus every semantic check (request smuggling,
    /// path traversal, Host rules, ...), fused into one pass over headers and one pass
    /// over the path for better cache locality and throughput.
    /// <para>
    /// Returns <c>false</c> if incomplete; throws <see cref="HttpParseException"/> if
    /// structurally or semantically invalid.
    /// </para>
    /// </summary>
    /// <param name="input">Contiguous buffer holding the request header (and possibly more).</param>
    /// <param name="request">Target to populate with parsed request data.</param>
    /// <param name="limits">Resource limits to enforce during parsing.</param>
    /// <param name="bytesReadCount">
    /// One less than the bytes consumed on success (the body starts at <c>bytesReadCount + 1</c>),
    /// or -1 if incomplete.
    /// </param>
    /// <returns><c>true</c> if a complete header was parsed and validated; <c>false</c> if more data is needed.</returns>
    /// <exception cref="HttpParseException">Thrown on any protocol or semantic violation.</exception>
    /// <remarks>
    /// Obsolete: use <see cref="TryParseRequestHeader(ref ReadOnlyMemory{byte}, BinaryRequest, in ParserLimits, out int)"/>,
    /// which reports the header block length itself — drop the <c>+ 1</c> when migrating.
    /// </remarks>
    [Obsolete(ObsoleteRequestMessage)]
    public static bool TryExtractFullHeaderROM(
        ref ReadOnlyMemory<byte> input, BinaryRequest request,
        in ParserLimits limits, out int bytesReadCount)
    {
        bool complete = TryParseRequestHeader(ref input, request, in limits, out int consumed);
        bytesReadCount = complete ? consumed - 1 : -1;
        return complete;
    }

    /// <summary>
    /// Entry point: combined parse + semantic validation with full security checks.
    /// <para>
    /// Single-segment input is dispatched to the zero-copy validated ROM path.
    /// Multi-segment input is checked for completeness (<c>\r\n\r\n</c>), then linearized
    /// via <c>ToArray()</c> and parsed through the validated ROM path.
    /// </para>
    /// </summary>
    /// <param name="input">Input buffer from the network layer.</param>
    /// <param name="request">Target to populate with parsed request data.</param>
    /// <param name="limits">Resource limits to enforce during parsing.</param>
    /// <param name="bytesReadCount">
    /// One less than the bytes consumed on success (the body starts at <c>bytesReadCount + 1</c>),
    /// or -1 if incomplete.
    /// </param>
    /// <returns><c>true</c> if a complete header was parsed and validated; <c>false</c> if more data is needed.</returns>
    /// <exception cref="HttpParseException">Thrown on any protocol or semantic violation.</exception>
    /// <remarks>
    /// Obsolete: use <see cref="TryParseRequestHeader(ref ReadOnlySequence{byte}, BinaryRequest, in ParserLimits, out int)"/>,
    /// which reports the header block length itself — drop the <c>+ 1</c> when migrating.
    /// </remarks>
    [Obsolete(ObsoleteRequestMessage)]
    public static bool TryExtractFullHeaderValidated(
        ref ReadOnlySequence<byte> input, BinaryRequest request,
        in ParserLimits limits, out int bytesReadCount)
    {
        bool complete = TryParseRequestHeader(ref input, request, in limits, out int consumed);
        bytesReadCount = complete ? consumed - 1 : -1;
        return complete;
    }

    /// <summary>
    /// Combined parse + semantic validation of a RESPONSE header — single-segment hot path.
    /// <para>
    /// Returns <c>false</c> if incomplete; throws <see cref="HttpParseException"/> if
    /// structurally or semantically invalid.
    /// </para>
    /// </summary>
    /// <param name="input">Contiguous buffer holding the response header (and possibly more).</param>
    /// <param name="response">Target to populate with parsed response data.</param>
    /// <param name="limits">Resource limits to enforce during parsing.</param>
    /// <param name="bytesReadCount">
    /// One less than the bytes consumed on success (the body starts at <c>bytesReadCount + 1</c>),
    /// or -1 if incomplete.
    /// </param>
    /// <returns><c>true</c> if a complete header was parsed and validated; <c>false</c> if more data is needed.</returns>
    /// <exception cref="HttpParseException">Thrown on any protocol or semantic violation.</exception>
    /// <remarks>
    /// Obsolete: use <see cref="TryParseResponseHeader(ref ReadOnlyMemory{byte}, BinaryResponse, in ParserLimits, out int)"/>,
    /// which reports the header block length itself — drop the <c>+ 1</c> when migrating.
    /// </remarks>
    [Obsolete(ObsoleteResponseMessage)]
    public static bool TryExtractFullResponseHeaderROM(
        ref ReadOnlyMemory<byte> input, BinaryResponse response,
        in ParserLimits limits, out int bytesReadCount)
    {
        bool complete = TryParseResponseHeader(ref input, response, in limits, out int consumed);
        bytesReadCount = complete ? consumed - 1 : -1;
        return complete;
    }

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
    /// <param name="bytesReadCount">
    /// One less than the bytes consumed on success (the body starts at <c>bytesReadCount + 1</c>),
    /// or -1 if incomplete.
    /// </param>
    /// <returns><c>true</c> if a complete header was parsed and validated; <c>false</c> if more data is needed.</returns>
    /// <exception cref="HttpParseException">Thrown on any protocol or semantic violation.</exception>
    /// <remarks>
    /// Obsolete: use <see cref="TryParseResponseHeader(ref ReadOnlySequence{byte}, BinaryResponse, in ParserLimits, out int)"/>,
    /// which reports the header block length itself — drop the <c>+ 1</c> when migrating.
    /// </remarks>
    [Obsolete(ObsoleteResponseMessage)]
    public static bool TryExtractFullResponseHeaderValidated(
        ref ReadOnlySequence<byte> input, BinaryResponse response,
        in ParserLimits limits, out int bytesReadCount)
    {
        bool complete = TryParseResponseHeader(ref input, response, in limits, out int consumed);
        bytesReadCount = complete ? consumed - 1 : -1;
        return complete;
    }
}
