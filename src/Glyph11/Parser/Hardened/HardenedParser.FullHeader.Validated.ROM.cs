using System.Runtime.CompilerServices;
using Glyph11.Protocol;

namespace Glyph11.Parser.Hardened;

public static partial class HardenedParser
{
    // ---- Semantic validation constants (TransferEncodingName, ChunkedValue
    //       and AsciiEqualsIgnoreCase live in HardenedParser.DetectBodyFraming.cs) ----
    private static ReadOnlySpan<byte> ContentLengthName => "content-length"u8;
    private static ReadOnlySpan<byte> HostHeaderName => "host"u8;
    private static ReadOnlySpan<byte> OptionsMethodName => "options"u8;
    private static ReadOnlySpan<byte> ConnectMethodName => "connect"u8;

    /// <summary>
    /// Combined parse + semantic validation — single-segment hot path.
    /// Equivalent to calling <see cref="TryExtractFullHeaderROM"/> followed by every
    /// <see cref="Glyph11.Validation.RequestSemantics"/> check, but fused into one pass
    /// over headers and one pass over the path for better cache locality and throughput.
    /// <para>
    /// Returns <c>false</c> if incomplete; throws <see cref="HttpParseException"/> if
    /// structurally or semantically invalid.
    /// </para>
    /// </summary>
    [SkipLocalsInit]
    public static bool TryExtractFullHeaderValidatedROM(
        ref ReadOnlyMemory<byte> input, BinaryRequest request,
        in ParserLimits limits, out int bytesReadCount)
    {
        bytesReadCount = -1;
        var span = input.Span;

        int headerEnd = span.IndexOf(CrlfCrlf);
        if (headerEnd < 0) return false;

        int totalHeaderBytes = headerEnd + 4;
        if (totalHeaderBytes > limits.MaxTotalHeaderBytes)
            throw new HttpParseException("Total header size exceeds limit.", statusCode: 431);

        // ---- Request line: METHOD SP URL SP VERSION CRLF ----

        int requestLineEnd = span.IndexOf(Crlf);
        if (requestLineEnd < 0)
            throw new HttpParseException("Invalid HTTP/1.1 request line.");

        var requestLine = span[..requestLineEnd];

        // ---- Reject bare LF in request line — RFC 9112 §2.2 ----
        if (requestLine.IndexOf((byte)'\n') >= 0)
            throw new HttpParseException("Bare LF detected; only CRLF line endings are allowed.");

        int firstSpace = requestLine.IndexOf(Space);
        if (firstSpace < 0)
            throw new HttpParseException("Invalid request line: missing method.");

        int secondSpaceRel = requestLine[(firstSpace + 1)..].IndexOf(Space);
        if (secondSpaceRel < 0)
            throw new HttpParseException("Invalid request line: missing version.");

        int secondSpace = firstSpace + 1 + secondSpaceRel;

        // ---- Reject multiple spaces in request line — RFC 9112 §3 ----
        if (secondSpace > firstSpace + 1 && requestLine[firstSpace + 1] == Space)
            throw new HttpParseException("Multiple spaces in request line.");
        if (secondSpace + 1 < requestLine.Length && requestLine[secondSpace + 1] == Space)
            throw new HttpParseException("Multiple spaces in request line.");

        // --- Method ---
        var methodSpan = requestLine[..firstSpace];
        if (methodSpan.Length == 0 || methodSpan.Length > limits.MaxMethodLength)
            throw new HttpParseException("Method length exceeds limit.");
        if (!IsValidToken(methodSpan))
            throw new HttpParseException("Method contains invalid token characters.");

        request.Method = input[..firstSpace];

        // ---- Semantic: CONNECT method — RFC 9110 §9.3.6 ----
        if (AsciiEqualsIgnoreCase(methodSpan, ConnectMethodName))
            throw new HttpParseException("CONNECT method is not allowed on origin servers.");

        // --- URL ---
        int urlStart = firstSpace + 1;
        int urlLen = secondSpace - urlStart;
        if (urlLen > limits.MaxUrlLength)
            throw new HttpParseException("URL length exceeds limit.", statusCode: 431);

        var urlSpan = requestLine.Slice(urlStart, urlLen);

        // --- Validate request-target for control characters (NUL, etc.) ---
        if (!IsValidRequestTarget(urlSpan))
            throw new HttpParseException("Request-target contains invalid characters.");

        // --- Version ---
        var versionSpan = requestLine[(secondSpace + 1)..];
        if (!IsValidHttpVersion(versionSpan))
            throw new HttpParseException("Invalid HTTP version.");

        request.Version = input.Slice(secondSpace + 1, versionSpan.Length);

        // --- Path + Query ---
        int queryStartIndex = urlSpan.IndexOf(Question);
        ReadOnlySpan<byte> pathSpan;

        if (queryStartIndex >= 0)
        {
            request.Path = input.Slice(urlStart, queryStartIndex);
            pathSpan = span.Slice(urlStart, queryStartIndex);

            int queryAbsStart = urlStart + queryStartIndex + 1;
            int queryLen = urlLen - (queryStartIndex + 1);
            var query = span.Slice(queryAbsStart, queryLen);

            int paramCount = 0;
            int cur = 0;
            while (cur < query.Length)
            {
                int pairAbsStart = queryAbsStart + cur;

                int amp = query[cur..].IndexOf(QuerySeparator);
                int pairLen = (amp < 0) ? (query.Length - cur) : amp;

                var pair = query.Slice(cur, pairLen);
                int eq = pair.IndexOf(Equal);

                if (eq > 0)
                {
                    if (++paramCount > limits.MaxQueryParameterCount)
                        throw new HttpParseException("Query parameter count exceeds limit.", statusCode: 431);

                    request.QueryParameters.Add(
                        input.Slice(pairAbsStart, eq),
                        input.Slice(pairAbsStart + eq + 1, pairLen - (eq + 1)));
                }

                cur += pairLen + (amp < 0 ? 0 : 1);
            }
        }
        else
        {
            request.Path = input.Slice(urlStart, urlLen);
            pathSpan = urlSpan;
        }

        // ---- Semantic: single-pass path validation ----
        ValidatePathSemantics(pathSpan);

        // ---- Semantic: asterisk-form — RFC 9112 §3.2.4 ----
        if (pathSpan.Length == 1 && pathSpan[0] == (byte)'*')
        {
            if (!AsciiEqualsIgnoreCase(methodSpan, OptionsMethodName))
                throw new HttpParseException("Asterisk-form request-target is only valid for OPTIONS.");
        }

        // ---- Headers (structural parse + inline semantic checks) ----

        int lineStart = requestLineEnd + 2;
        int headerCount = 0;

        // Semantic state tracked across headers
        bool hasCL = false;
        bool hasTE = false;
        int hostCount = 0;
        ReadOnlySpan<byte> firstCLValue = default;

        while (true)
        {
            int lineLen = span[lineStart..].IndexOf(Crlf);
            if (lineLen < 0)
                throw new HttpParseException("Invalid headers.");

            if (lineLen == 0)
                break;

            var line = span.Slice(lineStart, lineLen);

            // ---- Reject bare LF in header line — RFC 9112 §2.2 ----
            if (line.IndexOf((byte)'\n') >= 0)
                throw new HttpParseException("Bare LF detected; only CRLF line endings are allowed.");

            // ---- Reject obs-fold (line starting with SP/HTAB) — RFC 9112 §5.2 ----
            if (line[0] == (byte)' ' || line[0] == (byte)'\t')
                throw new HttpParseException("Obsolete line folding (obs-fold) is not allowed.");

            int colon = line.IndexOf(Colon);

            if (colon <= 0)
                throw new HttpParseException(colon == 0
                    ? "Header name is empty."
                    : "Malformed header line: missing colon.");

            // ---- Reject whitespace between field-name and colon — RFC 9112 §5.1 ----
            if (line[colon - 1] == (byte)' ' || line[colon - 1] == (byte)'\t')
                throw new HttpParseException("Whitespace between header name and colon is not allowed.");

            // Validate header name
            var nameSpan = line[..colon];
            if (nameSpan.Length > limits.MaxHeaderNameLength)
                throw new HttpParseException("Header name length exceeds limit.", statusCode: 431);
            if (!IsValidToken(nameSpan))
                throw new HttpParseException("Header name contains invalid token characters.");

            // Trim leading OWS from value
            int valAbsStart = lineStart + colon + 1;
            while (valAbsStart < lineStart + lineLen)
            {
                byte b = span[valAbsStart];
                if (b != (byte)' ' && b != (byte)'\t') break;
                valAbsStart++;
            }

            int valLen = (lineStart + lineLen) - valAbsStart;

            // Validate header value
            var valueSpan = span.Slice(valAbsStart, valLen);
            if (valLen > limits.MaxHeaderValueLength)
                throw new HttpParseException("Header value length exceeds limit.", statusCode: 431);
            if (!IsValidFieldValue(valueSpan))
                throw new HttpParseException("Header value contains invalid characters.");

            if (++headerCount > limits.MaxHeaderCount)
                throw new HttpParseException("Header count exceeds limit.", statusCode: 431);

            request.Headers.Add(
                input.Slice(lineStart, colon),
                input.Slice(valAbsStart, valLen));

            // ---- Inline semantic checks keyed by header name ----
            // Length pre-check avoids the full case-insensitive compare for most headers.

            if (nameSpan.Length == 14 && AsciiEqualsIgnoreCase(nameSpan, ContentLengthName))
            {
                // RFC 9110 §8.6 — validate format (syntax, leading zeros, overflow)
                if (!SemIsValidContentLengthValue(valueSpan))
                    throw new HttpParseException("Invalid Content-Length format.");

                // RFC 9112 §6.2 — comma-separated values must all be identical
                if (SemHasConflictingCommaSeparatedCL(valueSpan))
                    throw new HttpParseException("Conflicting comma-separated Content-Length values.");

                // RFC 9110 §8.6 — multiple CL headers must have identical values
                if (hasCL)
                {
                    if (!valueSpan.SequenceEqual(firstCLValue))
                        throw new HttpParseException("Conflicting Content-Length headers.");
                }
                else
                {
                    firstCLValue = valueSpan;
                    hasCL = true;
                }
            }
            else if (nameSpan.Length == 17 && AsciiEqualsIgnoreCase(nameSpan, TransferEncodingName))
            {
                hasTE = true;

                // RFC 9112 §6.1 — only "chunked" is accepted
                var trimmed = SemTrimOWS(valueSpan);
                if (!AsciiEqualsIgnoreCase(trimmed, ChunkedValue))
                    throw new HttpParseException("Invalid Transfer-Encoding value; only 'chunked' is accepted.");
            }
            else if (nameSpan.Length == 4 && AsciiEqualsIgnoreCase(nameSpan, HostHeaderName))
            {
                hostCount++;

                // RFC 9110 §7.2 — Host must be host:port only
                if (valueSpan.IndexOf((byte)'@') >= 0 || valueSpan.IndexOf((byte)'/') >= 0)
                    throw new HttpParseException("Invalid Host header: contains '@' or '/'.");
            }

            lineStart += lineLen + 2;
        }

        // ---- Post-loop cross-header semantic checks ----

        // RFC 9112 §6.1 — TE + CL together is a smuggling vector
        if (hasTE && hasCL)
            throw new HttpParseException("Both Transfer-Encoding and Content-Length are present.");

        // RFC 9112 §3.2 — HTTP/1.1 requires exactly one Host header
        if (hostCount != 1)
            throw new HttpParseException("Request must have exactly one Host header.");

        bytesReadCount += totalHeaderBytes;
        return true;
    }

    // =====================================================================
    //  Semantic validation helpers
    //  Prefixed with "Sem" to avoid collisions in this partial class.
    // =====================================================================

    /// <summary>
    /// Single-pass path validation covering: fragment (#), backslash, double encoding (%25),
    /// encoded null byte (%00), dot segments, and overlong UTF-8 sequences.
    /// </summary>
    private static void ValidatePathSemantics(ReadOnlySpan<byte> path)
    {
        for (int i = 0; i < path.Length; i++)
        {
            byte b = path[i];

            if (b == (byte)'#')
                throw new HttpParseException("Fragment indicator (#) in request-target.");

            if (b == (byte)'\\')
                throw new HttpParseException("Backslash in request path.");

            if (b == (byte)'%' && i + 2 < path.Length)
            {
                byte h1 = path[i + 1], h2 = path[i + 2];
                if (h1 == (byte)'2' && h2 == (byte)'5')
                    throw new HttpParseException("Double-encoded percent (%25) in path.");
                if (h1 == (byte)'0' && h2 == (byte)'0')
                    throw new HttpParseException("Encoded null byte (%00) in path.");
            }

            if (b == (byte)'/')
            {
                int remaining = path.Length - i - 1;
                if (remaining >= 1 && path[i + 1] == (byte)'.')
                {
                    // "/." at end or "/./"
                    if (remaining == 1 || path[i + 2] == (byte)'/')
                        throw new HttpParseException("Dot segment in request path.");
                    // "/.." at end or "/../"
                    if (path[i + 2] == (byte)'.' && (remaining == 2 || path[i + 3] == (byte)'/'))
                        throw new HttpParseException("Dot segment in request path.");
                }
            }

            // Overlong UTF-8 (RFC 3629 §3).
            // Note: in the hardened parser these bytes (>0x7E) are already rejected by
            // IsValidRequestTarget, but included for semantic completeness.
            if (b == 0xC0 || b == 0xC1)
                throw new HttpParseException("Overlong UTF-8 sequence in path.");
            if (b == 0xE0 && i + 1 < path.Length && path[i + 1] < 0xA0)
                throw new HttpParseException("Overlong UTF-8 sequence in path.");
            if (b == 0xF0 && i + 1 < path.Length && path[i + 1] < 0x90)
                throw new HttpParseException("Overlong UTF-8 sequence in path.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> SemTrimOWS(ReadOnlySpan<byte> value)
    {
        int start = 0;
        while (start < value.Length && (value[start] == (byte)' ' || value[start] == (byte)'\t'))
            start++;
        int end = value.Length;
        while (end > start && (value[end - 1] == (byte)' ' || value[end - 1] == (byte)'\t'))
            end--;
        return value[start..end];
    }

    /// <summary>
    /// Validates Content-Length value syntax: 1*DIGIT with optional comma-separated duplicates.
    /// Rejects empty, non-digit, leading zeros, overflow, and trailing commas.
    /// </summary>
    private static bool SemIsValidContentLengthValue(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty) return false;

        const int MaxDigits = 20; // ulong.MaxValue = 18446744073709551615

        int pos = 0;
        while (pos < value.Length)
        {
            // Skip OWS before each element
            while (pos < value.Length && (value[pos] == (byte)' ' || value[pos] == (byte)'\t'))
                pos++;

            if (pos >= value.Length) return false;

            byte b = value[pos];
            if (b < (byte)'0' || b > (byte)'9') return false;

            // Reject leading zeros: "0" is ok, "00" or "007" is not
            int digitStart = pos;
            if (value[pos] == (byte)'0')
            {
                pos++;
                if (pos < value.Length && value[pos] >= (byte)'0' && value[pos] <= (byte)'9')
                    return false;
            }
            else
            {
                pos++;
                while (pos < value.Length && value[pos] >= (byte)'0' && value[pos] <= (byte)'9')
                    pos++;
            }

            // Reject values that overflow ulong
            int digitCount = pos - digitStart;
            if (digitCount > MaxDigits)
                return false;
            if (digitCount == MaxDigits)
            {
                ReadOnlySpan<byte> ulongMax = "18446744073709551615"u8;
                for (int j = 0; j < MaxDigits; j++)
                {
                    if (value[digitStart + j] > ulongMax[j]) return false;
                    if (value[digitStart + j] < ulongMax[j]) break;
                }
            }

            // Skip OWS after the number
            while (pos < value.Length && (value[pos] == (byte)' ' || value[pos] == (byte)'\t'))
                pos++;

            if (pos >= value.Length) return true;
            if (value[pos] != (byte)',') return false;
            pos++;
        }

        return false; // trailing comma with nothing after
    }

    /// <summary>
    /// Returns true if a single Content-Length value contains comma-separated segments
    /// that are not all identical (smuggling vector — RFC 9112 §6.2).
    /// </summary>
    private static bool SemHasConflictingCommaSeparatedCL(ReadOnlySpan<byte> value)
    {
        if (value.IndexOf((byte)',') < 0)
            return false;

        ReadOnlySpan<byte> firstSegment = default;
        int start = 0;
        while (start < value.Length)
        {
            // Skip OWS
            while (start < value.Length && (value[start] == (byte)' ' || value[start] == (byte)'\t'))
                start++;

            int end = start;
            while (end < value.Length && value[end] != (byte)',')
                end++;

            // Trim trailing OWS
            int trimEnd = end;
            while (trimEnd > start && (value[trimEnd - 1] == (byte)' ' || value[trimEnd - 1] == (byte)'\t'))
                trimEnd--;

            var segment = value[start..trimEnd];

            if (firstSegment.Length == 0)
                firstSegment = segment;
            else if (!segment.SequenceEqual(firstSegment))
                return true;

            start = end + 1;
        }

        return false;
    }
}
