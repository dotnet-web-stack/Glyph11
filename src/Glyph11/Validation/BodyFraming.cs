using Glyph11.Parser;
using Glyph11.Parser.UltraHardened;

namespace Glyph11.Validation;

/// <summary>
/// Describes the body framing strategy indicated by an HTTP/1.1 message's headers.
/// </summary>
public enum BodyFraming : byte
{
    /// <summary>No body (no Transfer-Encoding, no Content-Length, or Content-Length == 0).</summary>
    None,

    /// <summary>Known-size body — use <see cref="BodyFramingResult.ContentLength"/>.</summary>
    ContentLength,

    /// <summary>Chunked transfer-encoding — use <see cref="ChunkedBodyStream"/>.</summary>
    Chunked,
}

/// <summary>
/// Result of <see cref="HardenedParser.DetectBodyFraming"/>:
/// the framing kind plus the content length when applicable.
/// </summary>
public readonly struct BodyFramingResult
{
    /// <summary>The detected framing kind.</summary>
    public BodyFraming Framing { get; }

    /// <summary>
    /// The parsed Content-Length value. Meaningful only when
    /// <see cref="Framing"/> is <see cref="BodyFraming.ContentLength"/>.
    /// </summary>
    public long ContentLength { get; }

    private BodyFramingResult(BodyFraming framing, long contentLength)
    {
        Framing = framing;
        ContentLength = contentLength;
    }

    /// <summary>No body.</summary>
    public static BodyFramingResult NoBody => new(BodyFraming.None, 0);

    /// <summary>Content-Length-framed body.</summary>
    public static BodyFramingResult ForContentLength(long length) => new(BodyFraming.ContentLength, length);

    /// <summary>Chunked transfer-encoding.</summary>
    public static BodyFramingResult ForChunked => new(BodyFraming.Chunked, 0);
}
