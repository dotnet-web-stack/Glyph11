using System.Runtime.CompilerServices;
using Glyph11.Protocol;

namespace Glyph11.Parser.Hardened;

public static partial class HardenedParser
{
    /// <summary>
    /// Hot path — single-segment parse with full security validation.
    /// Returns false if incomplete; throws HttpParseException if structurally invalid.
    /// </summary>
    [SkipLocalsInit]
    public static bool TryExtractFullHeaderROM(
        ref ReadOnlyMemory<byte> input, BinaryRequest request,
        in ParserLimits limits, out int bytesReadCount)
    {
        bytesReadCount = -1;
        var span = input.Span;

        int headerEnd = span.IndexOf(ParserConstants.CrlfCrlf);
        if (headerEnd < 0) return false;

        int totalHeaderBytes = headerEnd + 4;
        if (totalHeaderBytes > limits.MaxTotalHeaderBytes)
            throw new HttpParseException("Total header size exceeds limit.", statusCode: 431);

        // ---- Request line: METHOD SP URL SP VERSION CRLF ----

        int requestLineEnd = span.IndexOf(ParserConstants.Crlf);
        if (requestLineEnd < 0)
            throw new HttpParseException("Invalid HTTP/1.1 request line.");

        var requestLine = span[..requestLineEnd];

        // ---- Reject bare LF in request line — RFC 9112 §2.2 ----
        if (requestLine.IndexOf((byte)'\n') >= 0)
            throw new HttpParseException("Bare LF detected; only CRLF line endings are allowed.");

        int firstSpace = requestLine.IndexOf(ParserConstants.Space);
        if (firstSpace < 0)
            throw new HttpParseException("Invalid request line: missing method.");

        int secondSpaceRel = requestLine[(firstSpace + 1)..].IndexOf(ParserConstants.Space);
        if (secondSpaceRel < 0)
            throw new HttpParseException("Invalid request line: missing version.");

        int secondSpace = firstSpace + 1 + secondSpaceRel;

        // ---- Reject multiple spaces in request line — RFC 9112 §3 ----
        // After the method, only one SP is allowed before the URL, and one before the version.
        if (secondSpace > firstSpace + 1 && requestLine[firstSpace + 1] == ParserConstants.Space)
            throw new HttpParseException("Multiple spaces in request line.");
        if (secondSpace + 1 < requestLine.Length && requestLine[secondSpace + 1] == ParserConstants.Space)
            throw new HttpParseException("Multiple spaces in request line.");

        // --- Method ---
        var methodSpan = requestLine[..firstSpace];
        if (methodSpan.Length == 0 || methodSpan.Length > limits.MaxMethodLength)
            throw new HttpParseException("Method length exceeds limit.");
        if (!ParserConstants.IsValidToken(methodSpan))
            throw new HttpParseException("Method contains invalid token characters.");

        request.Method = input[..firstSpace];

        // --- URL ---
        int urlStart = firstSpace + 1;
        int urlLen = secondSpace - urlStart;
        if (urlLen > limits.MaxUrlLength)
            throw new HttpParseException("URL length exceeds limit.", statusCode: 431);

        var urlSpan = requestLine.Slice(urlStart, urlLen);

        // --- Validate request-target for control characters (NUL, etc.) ---
        if (!ParserConstants.IsValidRequestTarget(urlSpan))
            throw new HttpParseException("Request-target contains invalid characters.");

        // --- Version ---
        var versionSpan = requestLine[(secondSpace + 1)..];
        if (!ParserConstants.IsValidHttpVersion(versionSpan))
            throw new HttpParseException("Invalid HTTP version.");

        request.Version = input.Slice(secondSpace + 1, versionSpan.Length);

        // --- Path + Query ---
        int queryStartIndex = urlSpan.IndexOf(ParserConstants.Question);
        if (queryStartIndex >= 0)
        {
            request.Path = input.Slice(urlStart, queryStartIndex);

            int queryAbsStart = urlStart + queryStartIndex + 1;
            int queryLen = urlLen - (queryStartIndex + 1);
            var query = span.Slice(queryAbsStart, queryLen);

            int paramCount = 0;
            int cur = 0;
            while (cur < query.Length)
            {
                int pairAbsStart = queryAbsStart + cur;

                int amp = query[cur..].IndexOf(ParserConstants.QuerySeparator);
                int pairLen = (amp < 0) ? (query.Length - cur) : amp;

                var pair = query.Slice(cur, pairLen);
                int eq = pair.IndexOf(ParserConstants.Equal);

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
        }

        // ---- Headers ----

        int lineStart = requestLineEnd + 2;
        int headerCount = 0;

        while (true)
        {
            int lineLen = span[lineStart..].IndexOf(ParserConstants.Crlf);
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

            int colon = line.IndexOf(ParserConstants.Colon);

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
            if (!ParserConstants.IsValidToken(nameSpan))
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
            if (!ParserConstants.IsValidFieldValue(valueSpan))
                throw new HttpParseException("Header value contains invalid characters.");

            if (++headerCount > limits.MaxHeaderCount)
                throw new HttpParseException("Header count exceeds limit.", statusCode: 431);

            request.Headers.Add(
                input.Slice(lineStart, colon),
                input.Slice(valAbsStart, valLen));

            lineStart += lineLen + 2;
        }

        bytesReadCount += totalHeaderBytes;
        return true;
    }
}
