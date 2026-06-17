using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using Glyph11.Parser.FlexibleParser;
using Glyph11.Protocol;

namespace Benchmarks;

public static class Program
{
    public sealed class FastConfig : ManualConfig
    {
        public FastConfig()
        {
            AddJob(Job.Default
                .WithMinIterationCount(1)
                .WithMaxIterationCount(3));

            // optional but useful (removes your other warnings)
            AddLogger(ConsoleLogger.Default);
            AddExporter(MarkdownExporter.Default);
            AddExporter(JsonExporter.FullCompressed);
            AddColumnProvider(DefaultColumnProviders.Instance);
        }
    }
    public static void Main(string[] args)
    {
        BenchmarkSwitcher
            .FromAssembly(typeof(Program).Assembly)
            .Run(args, new FastConfig());
    }
}

[MemoryDiagnoser]
public class FlexibleParserBenchmark
{
    private readonly BinaryRequest _into = new();

    // ---- Small (~80B) ----

    private readonly ReadOnlySequence<byte> _buffer =
        new(("GET /route?p1=1&p2=2&p3=3&p4=4 HTTP/1.1\r\n"u8 +
            "Content-Length: 100\r\n"u8 +
            "Server: Glyph11\r\n\r\n"u8).ToArray());

    private ReadOnlySequence<byte> _segmentedBuffer = CreateMultiSegment();

    private ReadOnlyMemory<byte> _memory;

    // ---- Large headers: 4KB, 32KB ----

    private static readonly byte[] _header4K = BenchmarkData.BuildHeader(4096);
    private static readonly byte[] _header32K = BenchmarkData.BuildHeader(32768);

    private ReadOnlyMemory<byte> _rom4K;
    private ReadOnlyMemory<byte> _rom32K;

    private ReadOnlySequence<byte> _seg4K;
    private ReadOnlySequence<byte> _seg32K;

    public FlexibleParserBenchmark()
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
        var seg3 = "Glyph11\r\n\r\n"u8.ToArray();

        var first = new Glyph11.Utils.BufferSegment(seg1);
        var last = first.Append(seg2).Append(seg3);

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    // ---- Small: ROM / MultiSegment ----

    [Benchmark]
    public void Small_ROM()
    {
        _into.Clear();
        FlexibleParser.TryExtractFullHeaderReadOnlyMemory(ref _memory, _into, out _);
    }

    [Benchmark]
    public void Small_MultiSegment()
    {
        _into.Clear();
        FlexibleParser.TryExtractFullHeader(ref _segmentedBuffer, _into, out _);
    }

    // ---- 4KB ----

    [Benchmark]
    public void Header4K_ROM()
    {
        _into.Clear();
        FlexibleParser.TryExtractFullHeaderReadOnlyMemory(ref _rom4K, _into, out _);
    }

    [Benchmark]
    public void Header4K_MultiSegment()
    {
        _into.Clear();
        FlexibleParser.TryExtractFullHeader(ref _seg4K, _into, out _);
    }

    // ---- 32KB ----

    [Benchmark]
    public void Header32K_ROM()
    {
        _into.Clear();
        FlexibleParser.TryExtractFullHeaderReadOnlyMemory(ref _rom32K, _into, out _);
    }

    [Benchmark]
    public void Header32K_MultiSegment()
    {
        _into.Clear();
        FlexibleParser.TryExtractFullHeader(ref _seg32K, _into, out _);
    }
}
