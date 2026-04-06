using Glyph11.Parser;
using Glyph11.Protocol;

namespace Glyph11.Validation;

/// <summary>
/// Inspects parsed headers to determine the body framing strategy.
/// Parser-agnostic — works with any parser tier (Flexible, Hardened, UltraHardened).
/// </summary>
public static class BodyFramingDetector
{
    /// <summary>
    /// Inspects the parsed headers in <paramref name="request"/> and returns the body
    /// framing kind (chunked, content-length, or none) without touching any body bytes.
    /// </summary>
    public static BodyFramingResult DetectBodyFraming(BinaryRequest request)
    {
        if (HasChunkedTE(request))
            return BodyFramingResult.ForChunked;

        long cl = ContentLengthBodyReader.ParseContentLength(request);
        if (cl > 0)
            return BodyFramingResult.ForContentLength(cl);

        return BodyFramingResult.NoBody;
    }

    public static bool HasChunkedTE(BinaryRequest request)
    {
        var headers = request.Headers;

        for (int i = 0; i < headers.Count; i++)
        {
            var name = headers[i].Key.Span;
            if (!ParserConstants.AsciiEqualsIgnoreCase(name, ParserConstants.TransferEncodingName))
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
            if (ParserConstants.AsciiEqualsIgnoreCase(trimmed, ParserConstants.ChunkedValue))
                return true;
        }

        return false;
    }
}
