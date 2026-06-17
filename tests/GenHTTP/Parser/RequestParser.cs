using System.Buffers;
using GenHTTP.Types;
using Glyph11.Parser;
using Glyph11.Parser.UltraHardened;

namespace GenHTTP.Parser;

public static class RequestParser
{
    private static readonly ParserLimits Limits = ParserLimits.Default;

    public static bool TryParse(ReadOnlySequence<byte> buffer, Request into, out int bytesRead)
        => TryParse(buffer, into, in Limits, out bytesRead);

    public static bool TryParse(ReadOnlySequence<byte> buffer, Request into, in ParserLimits limits, out int bytesRead)
    {
        var raw = into.Source;

        // UltraHardenedParser enforces all structural and semantic checks during
        // parsing and throws HttpParseException on any violation.
        if (UltraHardenedParser.TryExtractFullHeaderValidated(ref buffer, raw, in limits, out bytesRead))
        {
            into.Apply();
            return true;
        }

        return false;
    }

}
