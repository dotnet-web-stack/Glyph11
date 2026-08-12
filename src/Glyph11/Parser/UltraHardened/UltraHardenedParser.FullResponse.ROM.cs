using System.Runtime.CompilerServices;
using Glyph11.Protocol;

namespace Glyph11.Parser.UltraHardened;

public static partial class UltraHardenedParser
{
    /// <summary>
    /// Combined parse + semantic validation of a RESPONSE header — single-segment hot path.
    /// <para>
    /// Returns <c>false</c> if incomplete; throws <see cref="HttpParseException"/> if
    /// structurally or semantically invalid.
    /// </para>
    /// </summary>
    /// <remarks>
    /// The header block is parsed by the same rules as a request's — field-lines are identical in
    /// both directions, so obs-fold, bare LF, whitespace before the colon and the
    /// Transfer-Encoding/Content-Length smuggling checks all carry over unchanged.
    /// <para>
    /// What does not carry over: there is no Host rule (that is a request requirement), no
    /// request-target, and the first line is <c>HTTP-version SP status-code SP [reason-phrase]</c>
    /// rather than <c>METHOD SP target SP HTTP-version</c>.
    /// </para>
    /// <para>
    /// Whether a body follows at all is not decidable from the response alone — HEAD and 304 are
    /// framed by the request that produced them. See
    /// <see cref="Validation.BodyFramingDetector.DetectResponseBodyFraming"/>.
    /// </para>
    /// </remarks>
    [SkipLocalsInit]
    public static bool TryExtractFullResponseHeaderROM(
        ref ReadOnlyMemory<byte> input, BinaryResponse response,
        in ParserLimits limits, out int bytesReadCount)
    {
        bytesReadCount = -1;
        var span = input.Span;

        int headerEnd = span.IndexOf(ParserConstants.CrlfCrlf);
        if (headerEnd < 0) return false;

        int totalHeaderBytes = headerEnd + 4;
        if (totalHeaderBytes > limits.MaxTotalHeaderBytes)
            throw new HttpParseException("Total header size exceeds limit.", statusCode: 431);

        // ---- Status line: HTTP-version SP status-code SP [ reason-phrase ] CRLF — RFC 9112 §4 ----

        int statusLineEnd = span.IndexOf(ParserConstants.Crlf);
        if (statusLineEnd < 0)
            throw new HttpParseException("Invalid HTTP/1.1 status line.");

        var statusLine = span[..statusLineEnd];

        // ---- Reject bare LF in status line — RFC 9112 §2.2 ----
        if (statusLine.IndexOf((byte)'\n') >= 0)
            throw new HttpParseException("Bare LF detected; only CRLF line endings are allowed.");

        int firstSpace = statusLine.IndexOf(ParserConstants.Space);
        if (firstSpace < 0)
            throw new HttpParseException("Invalid status line: missing status code.");

        // --- Version ---
        var versionSpan = statusLine[..firstSpace];
        if (!ParserConstants.IsValidHttpVersion(versionSpan))
            throw new HttpParseException("Invalid HTTP version.", 505);

        response.Version = input[..firstSpace];

        // --- Status code: exactly three digits — RFC 9112 §4 ---
        int codeStart = firstSpace + 1;
        if (codeStart + 3 > statusLine.Length)
            throw new HttpParseException("Invalid status line: truncated status code.");

        var codeSpan = statusLine.Slice(codeStart, 3);
        if (!ParserConstants.IsDigit(codeSpan[0]) ||
            !ParserConstants.IsDigit(codeSpan[1]) ||
            !ParserConstants.IsDigit(codeSpan[2]))
            throw new HttpParseException("Status code must be exactly three digits.");

        int status = ((codeSpan[0] - '0') * 100) + ((codeSpan[1] - '0') * 10) + (codeSpan[2] - '0');

        // A three-digit code below 100 is well-formed but not a status — 0xx has no meaning and
        // treating it as one lets a garbage first line pass as a response.
        if (status < 100)
            throw new HttpParseException("Status code must be in the range 100-599.");

        response.StatusCode = input.Slice(codeStart, 3);
        response.Status = status;

        // --- Reason phrase (optional) ---
        int afterCode = codeStart + 3;
        if (afterCode == statusLine.Length)
        {
            // "HTTP/1.1 200" with no trailing space. The grammar asks for the SP even when the
            // phrase is empty, but origins do send this and it is not a parsing ambiguity, so it
            // is accepted rather than turned into an interop failure.
            response.ReasonPhrase = default;
        }
        else
        {
            if (statusLine[afterCode] != ParserConstants.Space)
                throw new HttpParseException("Invalid status line: status code must be three digits.");

            int reasonStart = afterCode + 1;
            int reasonLen = statusLine.Length - reasonStart;

            if (reasonLen > limits.MaxReasonPhraseLength)
                throw new HttpParseException("Reason phrase length exceeds limit.", statusCode: 431);

            // reason-phrase = 1*( HTAB / SP / VCHAR / obs-text ) — the same character set a
            // field-value admits, which is what makes this check reusable. It is a charset check
            // and nothing more: the phrase is free text and carries no meaning to act on.
            var reasonSpan = statusLine.Slice(reasonStart, reasonLen);
            if (!ParserConstants.IsValidFieldValue(reasonSpan))
                throw new HttpParseException("Reason phrase contains invalid characters.");

            response.ReasonPhrase = input.Slice(reasonStart, reasonLen);
        }

        // ---- Headers (structural parse + inline semantic checks) ----

        int lineStart = statusLineEnd + 2;
        int headerCount = 0;

        // Semantic state tracked across headers
        bool hasCL = false;
        bool hasTE = false;
        ReadOnlySpan<byte> firstCLValue = default;

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

            response.Headers.Add(
                input.Slice(lineStart, colon),
                input.Slice(valAbsStart, valLen));

            // ---- Inline semantic checks keyed by header name ----
            // Length pre-check avoids the full case-insensitive compare for most headers.

            if (nameSpan.Length == 14 && ParserConstants.AsciiEqualsIgnoreCase(nameSpan, ContentLengthName))
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
            else if (nameSpan.Length == 17 && ParserConstants.AsciiEqualsIgnoreCase(nameSpan, ParserConstants.TransferEncodingName))
            {
                hasTE = true;

                // RFC 9112 §6.1 — only "chunked" is accepted
                var trimmed = SemTrimOWS(valueSpan);
                if (!ParserConstants.AsciiEqualsIgnoreCase(trimmed, ParserConstants.ChunkedValue))
                    throw new HttpParseException("Invalid Transfer-Encoding value; only 'chunked' is accepted.");
            }

            lineStart += lineLen + 2;
        }

        // ---- Post-loop cross-header semantic checks ----

        // RFC 9112 §6.1 — TE + CL together is a desync vector in this direction too: a proxy that
        // frames one way and a client that frames the other disagree on where the response ends,
        // and the remainder becomes the head of the next one.
        if (hasTE && hasCL)
            throw new HttpParseException("Both Transfer-Encoding and Content-Length are present.");

        bytesReadCount += totalHeaderBytes;
        return true;
    }
}
