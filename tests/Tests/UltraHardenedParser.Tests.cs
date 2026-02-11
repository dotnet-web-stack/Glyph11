using System.Buffers;
using System.Text;
using Glyph11.Parser;
using Glyph11.Protocol;
using Glyph11.Parser.Hardened;
using Glyph11.Parser.UltraHardened;
using Glyph11.Utils;

namespace Tests;

/// <summary>
/// Tests for UltraHardenedParser (fused parse + semantic validation).
/// Structural parsing is identical to HardenedParser; the key difference
/// is that all RequestSemantics checks are enforced inline during parsing.
/// Split across partial files:
///   - UltraHardenedParser.Parsing.cs     — core parsing (valid requests need Host)
///   - UltraHardenedParser.Semantics.cs   — fused semantic checks (throw on violation)
///   - UltraHardenedParser.Limits.cs      — resource limits
///   - UltraHardenedParser.StatusCode.cs  — HTTP status codes on exceptions
/// </summary>
public partial class UltraHardenedParserTests : IDisposable
{
    private readonly BinaryRequest _request = new();
    private static readonly ParserLimits Defaults = ParserLimits.Default;

    public void Dispose()
    {
        _request.Dispose();
        GC.SuppressFinalize(this);
    }

    private (bool success, int bytesRead) Parse(string raw, bool multiSegment)
        => Parse(raw, multiSegment, Defaults);

    private (bool success, int bytesRead) Parse(string raw, bool multiSegment, ParserLimits limits)
    {
        var bytes = Encoding.ASCII.GetBytes(raw);

        if (multiSegment)
        {
            var seq = SplitIntoSegments(bytes);
            return (UltraHardenedParser.TryExtractFullHeaderValidated(ref seq, _request, in limits, out var b), b);
        }

        ReadOnlyMemory<byte> rom = bytes;
        return (UltraHardenedParser.TryExtractFullHeaderROM(ref rom, _request, in limits, out var b2), b2);
    }

    private static ReadOnlySequence<byte> SplitIntoSegments(byte[] data)
    {
        if (data.Length < 3)
        {
            var single = new BufferSegment(data);
            return new ReadOnlySequence<byte>(single, 0, single, single.Memory.Length);
        }

        int split1 = data.Length / 3;
        int split2 = 2 * data.Length / 3;

        var first = new BufferSegment(data.AsMemory(0, split1));
        var last = first.Append(data.AsMemory(split1, split2 - split1)).Append(data.AsMemory(split2));

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private static void AssertHeader(KeyValueList headers, int index, string expectedKey, string expectedValue)
    {
        var kv = headers[index];
        AssertAscii.Equal(expectedKey, kv.Key);
        AssertAscii.Equal(expectedValue, kv.Value);
    }

    private static void AssertQueryParam(KeyValueList query, int index, string expectedKey, string expectedValue)
    {
        var kv = query[index];
        AssertAscii.Equal(expectedKey, kv.Key);
        AssertAscii.Equal(expectedValue, kv.Value);
    }
}
