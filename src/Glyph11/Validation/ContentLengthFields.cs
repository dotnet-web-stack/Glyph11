namespace Glyph11.Validation;

/// <summary>
/// Decides a message's Content-Length from all of its Content-Length fields, for every framing
/// helper — so <see cref="BodyFramingDetector"/> and <see cref="ContentLengthBodyReader"/> cannot
/// frame the same message differently.
/// </summary>
/// <remarks>
/// The hardened parser rejects conflicting Content-Length fields before a helper ever sees them.
/// The helpers are parser-agnostic, though, so for a message from a parser that does not
/// (<see cref="Parser.FlexibleParser.FlexibleParser"/>, Glyph11.Pico) this is the only check left:
/// one layer framing by the first field and another by the last is the textbook Content-Length
/// desync.
/// </remarks>
internal static class ContentLengthFields
{
    /// <summary>No Content-Length field has been seen.</summary>
    internal const long Absent = -2;

    /// <summary>A Content-Length field was seen, but it states no decimal length.</summary>
    internal const long Invalid = -1;

    /// <summary>
    /// Folds one Content-Length field value into <paramref name="length"/>, which starts at
    /// <see cref="Absent"/>. Every element of the field — it may be a comma-separated list of
    /// duplicates (RFC 9110 §8.6) — and every earlier field must state the same length; identical
    /// duplicates are accepted. An element that is not a decimal length counts as a value of its
    /// own: it conflicts with any real length, and on its own leaves <see cref="Invalid"/>.
    /// </summary>
    /// <exception cref="HttpParseException">Two fields or list elements state different lengths.</exception>
    internal static void Fold(ref long length, ReadOnlySpan<byte> fieldValue)
    {
        bool sawElement = false;

        while (true)
        {
            int comma = fieldValue.IndexOf((byte)',');
            var element = BodyFramingDetector.TrimOws(comma < 0 ? fieldValue : fieldValue[..comma]);

            // RFC 9110 §5.6.1 — empty list elements are ignored ("5," is 5).
            if (!element.IsEmpty)
            {
                sawElement = true;
                Merge(ref length, BodyFramingDetector.ParseContentLengthDigits(element));
            }

            if (comma < 0)
                break;

            fieldValue = fieldValue[(comma + 1)..];
        }

        // A field with no element at all ("Content-Length:", or only commas) states no length.
        if (!sawElement)
            Merge(ref length, Invalid);
    }

    private static void Merge(ref long length, long element)
    {
        if (length == Absent)
            length = element;
        else if (length != element)
            throw new HttpParseException("Conflicting Content-Length headers.");
    }
}
