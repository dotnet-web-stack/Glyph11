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
