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
    /// Every Content-Length field, and every element of a list in one, must state the same length:
    /// this reads the length <see cref="BodyFramingDetector.DetectBodyFraming"/> frames by, by the
    /// same rule, so the two cannot disagree.
    /// </summary>
    /// <exception cref="HttpParseException">
    /// The Content-Length fields — or the elements of a list in one — state different lengths. The
    /// hardened parser already rejects these; this catches them for any other parser.
    /// </exception>
    public static long ParseContentLength(BinaryRequest request)
    {
        var headers = request.Headers;
        long contentLength = ContentLengthFields.Absent;

        for (int i = 0; i < headers.Count; i++)
        {
            if (!AsciiEqualsIgnoreCase(headers[i].Key.Span, ContentLengthName))
                continue;

            ContentLengthFields.Fold(ref contentLength, headers[i].Value.Span);
        }

        return contentLength >= 0 ? contentLength : -1;
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
