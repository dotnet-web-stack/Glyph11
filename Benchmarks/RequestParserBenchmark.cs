using BenchmarkDotNet.Attributes;
using Glyph11.Parser;
using Glyph11.Parser.Hardened;
using Glyph11.Protocol;
using Glyph11.Validation;

namespace Benchmarks;

[MemoryDiagnoser]
public class RequestSemanticsBenchmark
{
    private static readonly ParserLimits Limits = ParserLimits.Default with { MaxTotalHeaderBytes = 64 * 1024, MaxHeaderCount = 200 };

    private readonly BinaryRequest _small = new();
    private readonly BinaryRequest _4k = new();
    private readonly BinaryRequest _32k = new();

    public RequestSemanticsBenchmark()
    {
        Parse(_small, BenchmarkData.BuildSmallHeader());
        Parse(_4k, BenchmarkData.BuildHeader(4096));
        Parse(_32k, BenchmarkData.BuildHeader(32768));
    }

    private static void Parse(BinaryRequest into, byte[] data)
    {
        ReadOnlyMemory<byte> rom = data;
        HardenedParser.TryExtractFullHeaderROM(ref rom, into, in Limits, out _);
    }

    // ---- HasConflictingContentLength ----

    [Benchmark] public bool ConflictingCL_Small() => RequestSemantics.HasConflictingContentLength(_small);
    [Benchmark] public bool ConflictingCL_4K() => RequestSemantics.HasConflictingContentLength(_4k);
    [Benchmark] public bool ConflictingCL_32K() => RequestSemantics.HasConflictingContentLength(_32k);

    // ---- HasTransferEncodingWithContentLength ----

    [Benchmark] public bool TEWithCL_Small() => RequestSemantics.HasTransferEncodingWithContentLength(_small);
    [Benchmark] public bool TEWithCL_4K() => RequestSemantics.HasTransferEncodingWithContentLength(_4k);
    [Benchmark] public bool TEWithCL_32K() => RequestSemantics.HasTransferEncodingWithContentLength(_32k);

    // ---- HasDotSegments ----

    [Benchmark] public bool DotSegments_Small() => RequestSemantics.HasDotSegments(_small);
    [Benchmark] public bool DotSegments_4K() => RequestSemantics.HasDotSegments(_4k);
    [Benchmark] public bool DotSegments_32K() => RequestSemantics.HasDotSegments(_32k);

    // ---- HasInvalidHostHeaderCount ----

    [Benchmark] public bool InvalidHost_Small() => RequestSemantics.HasInvalidHostHeaderCount(_small);
    [Benchmark] public bool InvalidHost_4K() => RequestSemantics.HasInvalidHostHeaderCount(_4k);
    [Benchmark] public bool InvalidHost_32K() => RequestSemantics.HasInvalidHostHeaderCount(_32k);

    // ---- HasInvalidContentLengthFormat ----

    [Benchmark] public bool InvalidCLFormat_Small() => RequestSemantics.HasInvalidContentLengthFormat(_small);
    [Benchmark] public bool InvalidCLFormat_4K() => RequestSemantics.HasInvalidContentLengthFormat(_4k);
    [Benchmark] public bool InvalidCLFormat_32K() => RequestSemantics.HasInvalidContentLengthFormat(_32k);

    // ---- HasContentLengthWithLeadingZeros ----

    [Benchmark] public bool LeadingZerosCL_Small() => RequestSemantics.HasContentLengthWithLeadingZeros(_small);
    [Benchmark] public bool LeadingZerosCL_4K() => RequestSemantics.HasContentLengthWithLeadingZeros(_4k);
    [Benchmark] public bool LeadingZerosCL_32K() => RequestSemantics.HasContentLengthWithLeadingZeros(_32k);

    // ---- HasConflictingCommaSeparatedContentLength ----

    [Benchmark] public bool ConflictingCommaCL_Small() => RequestSemantics.HasConflictingCommaSeparatedContentLength(_small);
    [Benchmark] public bool ConflictingCommaCL_4K() => RequestSemantics.HasConflictingCommaSeparatedContentLength(_4k);
    [Benchmark] public bool ConflictingCommaCL_32K() => RequestSemantics.HasConflictingCommaSeparatedContentLength(_32k);

    // ---- HasFragmentInRequestTarget ----

    [Benchmark] public bool Fragment_Small() => RequestSemantics.HasFragmentInRequestTarget(_small);
    [Benchmark] public bool Fragment_4K() => RequestSemantics.HasFragmentInRequestTarget(_4k);
    [Benchmark] public bool Fragment_32K() => RequestSemantics.HasFragmentInRequestTarget(_32k);

    // ---- HasBackslashInPath ----

    [Benchmark] public bool Backslash_Small() => RequestSemantics.HasBackslashInPath(_small);
    [Benchmark] public bool Backslash_4K() => RequestSemantics.HasBackslashInPath(_4k);
    [Benchmark] public bool Backslash_32K() => RequestSemantics.HasBackslashInPath(_32k);

    // ---- HasDoubleEncoding ----

    [Benchmark] public bool DoubleEncoding_Small() => RequestSemantics.HasDoubleEncoding(_small);
    [Benchmark] public bool DoubleEncoding_4K() => RequestSemantics.HasDoubleEncoding(_4k);
    [Benchmark] public bool DoubleEncoding_32K() => RequestSemantics.HasDoubleEncoding(_32k);

    // ---- HasEncodedNullByte ----

    [Benchmark] public bool EncodedNull_Small() => RequestSemantics.HasEncodedNullByte(_small);
    [Benchmark] public bool EncodedNull_4K() => RequestSemantics.HasEncodedNullByte(_4k);
    [Benchmark] public bool EncodedNull_32K() => RequestSemantics.HasEncodedNullByte(_32k);

    // ---- HasOverlongUtf8 ----

    [Benchmark] public bool OverlongUtf8_Small() => RequestSemantics.HasOverlongUtf8(_small);
    [Benchmark] public bool OverlongUtf8_4K() => RequestSemantics.HasOverlongUtf8(_4k);
    [Benchmark] public bool OverlongUtf8_32K() => RequestSemantics.HasOverlongUtf8(_32k);

    // ---- HasInvalidTransferEncoding ----

    [Benchmark] public bool InvalidTE_Small() => RequestSemantics.HasInvalidTransferEncoding(_small);
    [Benchmark] public bool InvalidTE_4K() => RequestSemantics.HasInvalidTransferEncoding(_4k);
    [Benchmark] public bool InvalidTE_32K() => RequestSemantics.HasInvalidTransferEncoding(_32k);
}

[MemoryDiagnoser]
public class AllSemanticChecksBenchmark
{
    private static readonly ParserLimits Limits = ParserLimits.Default with { MaxTotalHeaderBytes = 64 * 1024, MaxHeaderCount = 200 };

    private readonly BinaryRequest _small = new();
    private readonly BinaryRequest _4k = new();
    private readonly BinaryRequest _32k = new();

    public AllSemanticChecksBenchmark()
    {
        Parse(_small, BenchmarkData.BuildSmallHeader());
        Parse(_4k, BenchmarkData.BuildHeader(4096));
        Parse(_32k, BenchmarkData.BuildHeader(32768));
    }

    private static void Parse(BinaryRequest into, byte[] data)
    {
        ReadOnlyMemory<byte> rom = data;
        HardenedParser.TryExtractFullHeaderROM(ref rom, into, in Limits, out _);
    }

    private static bool RunAllChecks(BinaryRequest req)
    {
        bool result = false;
        result |= RequestSemantics.HasConflictingContentLength(req);
        result |= RequestSemantics.HasTransferEncodingWithContentLength(req);
        result |= RequestSemantics.HasDotSegments(req);
        result |= RequestSemantics.HasInvalidHostHeaderCount(req);
        result |= RequestSemantics.HasInvalidContentLengthFormat(req);
        result |= RequestSemantics.HasContentLengthWithLeadingZeros(req);
        result |= RequestSemantics.HasConflictingCommaSeparatedContentLength(req);
        result |= RequestSemantics.HasFragmentInRequestTarget(req);
        result |= RequestSemantics.HasBackslashInPath(req);
        result |= RequestSemantics.HasDoubleEncoding(req);
        result |= RequestSemantics.HasEncodedNullByte(req);
        result |= RequestSemantics.HasOverlongUtf8(req);
        result |= RequestSemantics.HasInvalidTransferEncoding(req);
        return result;
    }

    [Benchmark] public bool AllChecks_Small() => RunAllChecks(_small);
    [Benchmark] public bool AllChecks_4K() => RunAllChecks(_4k);
    [Benchmark] public bool AllChecks_32K() => RunAllChecks(_32k);
}
