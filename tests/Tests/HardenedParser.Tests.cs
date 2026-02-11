using System.Buffers;
using System.Text;
using Glyph11.Protocol;
using Glyph11.Parser.Hardened;
using Glyph11.Utils;

namespace Tests;

/// <summary>
/// Tests for HardenedParser (security-hardened parser) and RequestSemantics.
/// Each parsing test runs against both ROM and multi-segment paths via [Theory].
/// Split across partial files:
///   - HardenedParser.Parsing.cs     — core parsing
///   - HardenedParser.Validation.cs  — parse-time security checks
///   - HardenedParser.Limits.cs      — resource limits
///   - HardenedParser.Semantics.cs   — RequestSemantics
/// </summary>
public partial class HardenedParserTests : IDisposable
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
            return (HardenedParser.TryExtractFullHeader(ref seq, _request, in limits, out var b), b);
        }

        ReadOnlyMemory<byte> rom = bytes;
        return (HardenedParser.TryExtractFullHeaderROM(ref rom, _request, in limits, out var b2), b2);
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
