using Glyph11.Protocol;

namespace Glyph11.Validation;

/// <summary>
/// Helpers for Content-Length-framed body reading.
/// </summary>
public static class ContentLengthBodyReader
{
    private static ReadOnlySpan<byte> ContentLengthName => "content-length"u8;

    /// <summary>
    /// Returns true if the buffer contains at least <paramref name="contentLength"/> bytes.
    /// </summary>
    public static bool HasCompleteBody(ReadOnlySpan<byte> body, long contentLength)
        => body.Length >= contentLength;

    /// <summary>
    /// Parses the Content-Length header value from the request. Returns -1 if absent or invalid.
    /// </summary>
    public static long ParseContentLength(BinaryRequest request)
    {
        var headers = request.Headers;

        for (int i = 0; i < headers.Count; i++)
        {
            if (!AsciiEqualsIgnoreCase(headers[i].Key.Span, ContentLengthName))
                continue;

            var value = headers[i].Value.Span;

            // Trim OWS
            int start = 0;
            while (start < value.Length && (value[start] == (byte)' ' || value[start] == (byte)'\t'))
                start++;
            int end = value.Length;
            while (end > start && (value[end - 1] == (byte)' ' || value[end - 1] == (byte)'\t'))
                end--;

            var trimmed = value[start..end];

            // Handle comma-separated: take first segment, without the OWS a list allows before its
            // comma - "5 ,5" is 5 (RFC 9110 §5.6.1), exactly as the validator read it.
            int comma = trimmed.IndexOf((byte)',');
            if (comma >= 0)
            {
                trimmed = trimmed[..comma];
                while (!trimmed.IsEmpty && (trimmed[^1] == (byte)' ' || trimmed[^1] == (byte)'\t'))
                    trimmed = trimmed[..^1];
            }

            // Parse digits
            long result = 0;
            if (trimmed.IsEmpty) return -1;
            for (int j = 0; j < trimmed.Length; j++)
            {
                byte b = trimmed[j];
                if (b < (byte)'0' || b > (byte)'9') return -1;
                result = result * 10 + (b - '0');
                if (result < 0) return -1; // overflow
            }

            return result;
        }

        return -1;
    }

    private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if ((a[i] | 0x20) != b[i]) return false;
        }
        return true;
    }
}
