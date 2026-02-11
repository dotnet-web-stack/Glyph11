using System.Buffers;
using GenHTTP.Types;
using Glyph11;
using Glyph11.Parser;
using Glyph11.Parser.Hardened;
using Glyph11.Validation;

namespace GenHTTP.Parser;

public static class RequestParser
{
    private static readonly ParserLimits Limits = ParserLimits.Default;

    public static bool TryParse(ReadOnlySequence<byte> buffer, Request into, out int bytesRead)
        => TryParse(buffer, into, in Limits, out bytesRead);

    public static bool TryParse(ReadOnlySequence<byte> buffer, Request into, in ParserLimits limits, out int bytesRead)
    {
        var raw = into.Source;

        if (HardenedParser.TryExtractFullHeader(ref buffer, raw, in limits, out bytesRead))
        {
            if (RequestSemantics.HasTransferEncodingWithContentLength(raw))
                throw new HttpParseException("Request smuggling: TE + CL.");

            if (RequestSemantics.HasConflictingContentLength(raw))
                throw new HttpParseException("Conflicting Content-Length values.");

            if (RequestSemantics.HasInvalidHostHeaderCount(raw))
                throw new HttpParseException("Invalid Host header count.");

            if (RequestSemantics.HasDotSegments(raw))
                throw new HttpParseException("Path traversal detected.");

            into.Apply();
            return true;
        }

        return false;
    }

}
