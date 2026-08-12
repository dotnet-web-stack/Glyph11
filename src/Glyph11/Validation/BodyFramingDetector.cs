using Glyph11.Parser;
using Glyph11.Protocol;

namespace Glyph11.Validation;

/// <summary>
/// Inspects parsed headers to determine the body framing strategy.
/// Parser-agnostic — works with any parser tier (Flexible, Hardened, UltraHardened).
/// </summary>
public static class BodyFramingDetector
{
    private static ReadOnlySpan<byte> TransferEncodingName => "transfer-encoding"u8;
    private static ReadOnlySpan<byte> ContentLengthName => "content-length"u8;
    private static ReadOnlySpan<byte> ChunkedValue => "chunked"u8;
    private static ReadOnlySpan<byte> HeadMethodName => "head"u8;
    private static ReadOnlySpan<byte> ConnectMethodName => "connect"u8;

    /// <summary>
    /// Inspects the parsed headers in <paramref name="request"/> and returns the body
    /// framing kind (chunked, content-length, or none) without touching any body bytes.
    /// Single pass over headers.
    /// </summary>
    public static BodyFramingResult DetectBodyFraming(BinaryRequest request)
    {
        var headers = request.Headers;
        ReadOnlySpan<byte> contentLengthValue = default;
        bool hasChunkedTE = false;

        for (int i = 0; i < headers.Count; i++)
        {
            var name = headers[i].Key.Span;

            if (name.Length == 17 && ParserConstants.AsciiEqualsIgnoreCase(name, TransferEncodingName))
            {
                var value = TrimOws(headers[i].Value.Span);
                if (ParserConstants.AsciiEqualsIgnoreCase(value, ChunkedValue))
                    hasChunkedTE = true;
            }
            else if (name.Length == 14 && ParserConstants.AsciiEqualsIgnoreCase(name, ContentLengthName))
            {
                contentLengthValue = TrimOws(headers[i].Value.Span);
            }
        }

        // Chunked takes priority over Content-Length (RFC 9112 §6.1)
        if (hasChunkedTE)
            return BodyFramingResult.ForChunked;

        if (!contentLengthValue.IsEmpty)
        {
            long cl = ParseContentLengthDigits(contentLengthValue);
            if (cl > 0)
                return BodyFramingResult.ForContentLength(cl);
        }

        return BodyFramingResult.NoBody;
    }

    /// <summary>
    /// Inspects the parsed headers in <paramref name="response"/> and returns the body framing
    /// kind. Single pass over headers.
    /// </summary>
    /// <param name="response">The parsed response header.</param>
    /// <param name="requestMethod">
    /// The method of the request that produced this response. It is REQUIRED, and not a
    /// convenience: a response to HEAD carries the Content-Length the body would have had and no
    /// body, so framing a response by its own headers alone reads the next response as this one's
    /// content. Pass the method exactly as sent.
    /// </param>
    /// <remarks>
    /// Precedence follows RFC 9112 §6: the status and method decide whether a body can exist at
    /// all, then Transfer-Encoding, then Content-Length, and a response with none of those runs
    /// until the connection closes.
    /// </remarks>
    public static BodyFramingResult DetectResponseBodyFraming(
        BinaryResponse response, ReadOnlySpan<byte> requestMethod)
    {
        int status = response.Status;

        // RFC 9112 §6.3 — these carry no body whatever the headers claim.
        if (status is >= 100 and < 200 || status == 204 || status == 304)
            return BodyFramingResult.NoBody;

        // A HEAD response is a GET response with the body removed: the framing headers describe a
        // body that is not there.
        if (ParserConstants.AsciiEqualsIgnoreCase(requestMethod, HeadMethodName))
            return BodyFramingResult.NoBody;

        // A 2xx to CONNECT means the tunnel is open and everything after the header is opaque
        // relay traffic, not an HTTP body.
        if (status is >= 200 and < 300 &&
            ParserConstants.AsciiEqualsIgnoreCase(requestMethod, ConnectMethodName))
            return BodyFramingResult.NoBody;

        var headers = response.Headers;
        ReadOnlySpan<byte> contentLengthValue = default;
        bool hasChunkedTE = false;

        for (int i = 0; i < headers.Count; i++)
        {
            var name = headers[i].Key.Span;

            if (name.Length == 17 && ParserConstants.AsciiEqualsIgnoreCase(name, TransferEncodingName))
            {
                var value = TrimOws(headers[i].Value.Span);
                if (ParserConstants.AsciiEqualsIgnoreCase(value, ChunkedValue))
                    hasChunkedTE = true;
            }
            else if (name.Length == 14 && ParserConstants.AsciiEqualsIgnoreCase(name, ContentLengthName))
            {
                contentLengthValue = TrimOws(headers[i].Value.Span);
            }
        }

        // Chunked takes priority over Content-Length (RFC 9112 §6.1)
        if (hasChunkedTE)
            return BodyFramingResult.ForChunked;

        if (!contentLengthValue.IsEmpty)
        {
            long cl = ParseContentLengthDigits(contentLengthValue);
            if (cl > 0)
                return BodyFramingResult.ForContentLength(cl);
            if (cl == 0)
                return BodyFramingResult.NoBody;
        }

        // No framing header at all. Unlike a request - which is simply bodyless here - a response
        // runs to end of connection (RFC 9112 §6.3), which is also the only framing HTTP/1.0
        // origins ever had.
        return BodyFramingResult.ForUntilClose;
    }

    private static ReadOnlySpan<byte> TrimOws(ReadOnlySpan<byte> value)
    {
        int start = 0;
        while (start < value.Length && (value[start] == (byte)' ' || value[start] == (byte)'\t'))
            start++;
        int end = value.Length;
        while (end > start && (value[end - 1] == (byte)' ' || value[end - 1] == (byte)'\t'))
            end--;
        return value[start..end];
    }

    private static long ParseContentLengthDigits(ReadOnlySpan<byte> value)
    {
        // Handle comma-separated: take first segment
        int comma = value.IndexOf((byte)',');
        if (comma >= 0)
            value = value[..comma];

        if (value.IsEmpty) return -1;

        long result = 0;
        for (int j = 0; j < value.Length; j++)
        {
            byte b = value[j];
            if (b < (byte)'0' || b > (byte)'9') return -1;
            result = result * 10 + (b - '0');
            if (result < 0) return -1; // overflow
        }

        return result;
    }
}
