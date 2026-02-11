using System.Buffers;
using BenchmarkDotNet.Attributes;
using GenHTTP.Types;
using Glyph11.Parser;
using Glyph11.Parser.Hardened;

namespace Benchmarks;

/*
 | Method                 | Mean        | Error        | StdDev     | Gen0   | Allocated |
   |----------------------- |------------:|-------------:|-----------:|-------:|----------:|
   | Small_ROM              |    96.37 ns |    17.844 ns |   0.978 ns |      - |         - |
   | Small_MultiSegment     |   205.71 ns |     7.895 ns |   0.433 ns | 0.0057 |     112 B |
   | Header4K_ROM           |   691.88 ns |   252.478 ns |  13.839 ns |      - |         - |
   | Header4K_MultiSegment  | 1,251.61 ns |   236.890 ns |  12.985 ns | 0.2193 |    4128 B |
   | Header32K_ROM          | 5,017.95 ns | 3,535.474 ns | 193.791 ns |      - |         - |
   | Header32K_MultiSegment | 8,811.07 ns | 2,621.855 ns | 143.713 ns | 1.7242 |   32808 B |
 */

[MemoryDiagnoser]
public class HardenedParserBenchmark
{
    private readonly Request _into = new();

    private static readonly ParserLimits Limits = ParserLimits.Default with { MaxTotalHeaderBytes = 64 * 1024, MaxHeaderCount = 200 };

    // ---- Small (~80B) ----

    private readonly ReadOnlySequence<byte> _buffer =
        new(("GET /route?p1=1&p2=2&p3=3&p4=4 HTTP/1.1\r\n"u8 +
            "Content-Length: 100\r\n"u8 +
            "Server: GenHTTP\r\n\r\n"u8).ToArray());

    private ReadOnlySequence<byte> _segmentedBuffer = CreateMultiSegment();

    private ReadOnlyMemory<byte> _memory;

    // ---- Large headers: 4KB, 32KB ----

    private static readonly byte[] _header4K = BenchmarkData.BuildHeader(4096);
    private static readonly byte[] _header32K = BenchmarkData.BuildHeader(32768);

    private ReadOnlyMemory<byte> _rom4K;
    private ReadOnlyMemory<byte> _rom32K;

    private ReadOnlySequence<byte> _seg4K;
    private ReadOnlySequence<byte> _seg32K;

    public HardenedParserBenchmark()
    {
        _memory = _buffer.ToArray();

        _rom4K = _header4K;
        _rom32K = _header32K;

        _seg4K = BenchmarkData.ToThreeSegments(_header4K);
        _seg32K = BenchmarkData.ToThreeSegments(_header32K);
    }

    private static ReadOnlySequence<byte> CreateMultiSegment()
    {
        var seg1 = "GET /route?p1=1&p2=2&p3=3&p4=4 HT"u8.ToArray();
        var seg2 = "TP/1.1\r\nContent-Length: 100\r\nServer: "u8.ToArray();
        var seg3 = "GenHTTP\r\n\r\n"u8.ToArray();

        var first = new Glyph11.Utils.BufferSegment(seg1);
        var last = first.Append(seg2).Append(seg3);

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    // ---- Small: ROM / MultiSegment ----

    [Benchmark]
    public void Small_ROM()
    {
        _into.Reset();
        HardenedParser.TryExtractFullHeaderROM(ref _memory, _into.Source, in Limits, out _);
    }

    [Benchmark]
    public void Small_MultiSegment()
    {
        _into.Reset();
        HardenedParser.TryExtractFullHeader(ref _segmentedBuffer, _into.Source, in Limits, out _);
    }

    // ---- 4KB ----

    [Benchmark]
    public void Header4K_ROM()
    {
        _into.Reset();
        HardenedParser.TryExtractFullHeaderROM(ref _rom4K, _into.Source, in Limits, out _);
    }

    [Benchmark]
    public void Header4K_MultiSegment()
    {
        _into.Reset();
        HardenedParser.TryExtractFullHeader(ref _seg4K, _into.Source, in Limits, out _);
    }

    // ---- 32KB ----

    [Benchmark]
    public void Header32K_ROM()
    {
        _into.Reset();
        HardenedParser.TryExtractFullHeaderROM(ref _rom32K, _into.Source, in Limits, out _);
    }

    [Benchmark]
    public void Header32K_MultiSegment()
    {
        _into.Reset();
        HardenedParser.TryExtractFullHeader(ref _seg32K, _into.Source, in Limits, out _);
    }
}
