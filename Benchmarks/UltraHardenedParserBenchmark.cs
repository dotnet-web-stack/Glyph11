using System.Buffers;
using BenchmarkDotNet.Attributes;
using GenHTTP.Types;
using Glyph11.Parser;
using Glyph11.Parser.Hardened;
using Glyph11.Parser.UltraHardened;

namespace Benchmarks;

[MemoryDiagnoser]
public class UltraHardenedParserBenchmark
{
    private readonly Request _into = new();

    private static readonly ParserLimits Limits = ParserLimits.Default with { MaxTotalHeaderBytes = 64 * 1024, MaxHeaderCount = 200 };

    // ---- Small (~80B) ----

    private readonly ReadOnlySequence<byte> _buffer =
        new(("GET /route?p1=1&p2=2&p3=3&p4=4 HTTP/1.1\r\n"u8 +
            "Host: localhost\r\n"u8 +
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

    public UltraHardenedParserBenchmark()
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
        var seg2 = "TP/1.1\r\nHost: localhost\r\nContent-Length: 100\r\nServer: "u8.ToArray();
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
        UltraHardenedParser.TryExtractFullHeaderROM(ref _memory, _into.Source, in Limits, out _);
    }

    [Benchmark]
    public void Small_MultiSegment()
    {
        _into.Reset();
        UltraHardenedParser.TryExtractFullHeaderValidated(ref _segmentedBuffer, _into.Source, in Limits, out _);
    }

    // ---- 4KB ----

    [Benchmark]
    public void Header4K_ROM()
    {
        _into.Reset();
        UltraHardenedParser.TryExtractFullHeaderROM(ref _rom4K, _into.Source, in Limits, out _);
    }

    [Benchmark]
    public void Header4K_MultiSegment()
    {
        _into.Reset();
        UltraHardenedParser.TryExtractFullHeaderValidated(ref _seg4K, _into.Source, in Limits, out _);
    }

    // ---- 32KB ----

    [Benchmark]
    public void Header32K_ROM()
    {
        _into.Reset();
        UltraHardenedParser.TryExtractFullHeaderROM(ref _rom32K, _into.Source, in Limits, out _);
    }

    [Benchmark]
    public void Header32K_MultiSegment()
    {
        _into.Reset();
        UltraHardenedParser.TryExtractFullHeaderValidated(ref _seg32K, _into.Source, in Limits, out _);
    }
}