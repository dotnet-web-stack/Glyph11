using System.Diagnostics;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Glyph11.Native;
using Glyph11.Parser;
using Glyph11.Parser.UltraHardened;
using Glyph11.Protocol;

// `csv <payload-dir>` emits CSV (dotnet-managed / dotnet-ffi) for the cross-language
// aggregator, using the shared payload files. Default: the rich BenchmarkDotNet run.
if (args.Length >= 1 && args[0] == "csv")
{
    CsvBench.Run(args.Length > 1 ? args[1] : ".");
    return;
}

BenchmarkRunner.Run<ParserComparison>();

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 5, iterationCount: 8)]
public class ParserComparison
{
    // Larger limits so the 4K/32K payloads (many headers) parse on both sides.
    private static readonly ParserLimits ManagedLimits =
        ParserLimits.Default with { MaxTotalHeaderBytes = 64 * 1024, MaxHeaderCount = 200 };

    private static readonly Glyph11Limits NativeLimits = new()
    {
        StructSize = 32, MaxHeaderCount = 200, MaxHeaderNameLength = 256,
        MaxHeaderValueLength = 8192, MaxUrlLength = 8192, MaxQueryParameterCount = 128,
        MaxMethodLength = 16, MaxTotalHeaderBytes = 64 * 1024,
    };

    private readonly byte[] _small = BuildSmall();
    private readonly byte[] _4k   = BuildHeader(4096);
    private readonly byte[] _32k  = BuildHeader(32768);

    private readonly BinaryRequest _req = new();
    private readonly Glyph11Field[] _h = new Glyph11Field[256];
    private readonly Glyph11Field[] _q = new Glyph11Field[256];

    // ---- managed (reference) ----
    private bool Managed(byte[] data)
    {
        _req.Clear();
        var rom = (ReadOnlyMemory<byte>)data;
        return UltraHardenedParser.TryExtractFullHeaderROM(ref rom, _req, in ManagedLimits, out _);
    }

    // ---- native via FFI ----
    private int Native(byte[] data)
        => Glyph11Parser.Parse(data, _h, _q, NativeLimits, out _);

    [Benchmark(Baseline = true)] public bool Managed_Small() => Managed(_small);
    [Benchmark]                  public int  Native_Small()  => Native(_small);

    [Benchmark] public bool Managed_4K() => Managed(_4k);
    [Benchmark] public int  Native_4K()  => Native(_4k);

    [Benchmark] public bool Managed_32K() => Managed(_32k);
    [Benchmark] public int  Native_32K()  => Native(_32k);

    // ---- inputs (Host-bearing so UltraHardened accepts) ----
    private static byte[] BuildSmall() => Encoding.ASCII.GetBytes(
        "GET /route?p1=1&p2=2&p3=3&p4=4 HTTP/1.1\r\n" +
        "Host: localhost\r\nContent-Length: 100\r\nServer: Glyph11\r\n\r\n");

    private static byte[] BuildHeader(int targetBytes)
    {
        var sb = new StringBuilder(targetBytes + 128);
        sb.Append("GET /route?p1=1&p2=2&p3=3&p4=4 HTTP/1.1\r\nHost: localhost\r\n");
        int index = 0;
        while (sb.Length < targetBytes - 4)
        {
            string name = $"X-Header-{index++}";
            int remaining = targetBytes - sb.Length - name.Length - 4;
            int valueLen = Math.Min(Math.Max(remaining, 1), 200);
            sb.Append(name).Append(": ").Append('A', valueLen).Append("\r\n");
        }
        sb.Append("\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}

// Consistent manual-timing harness (warmup + timed loop) matching the pure-C and
// Kotlin benches, so the four series are directly comparable in one table.
internal static class CsvBench
{
    private static readonly ParserLimits ManagedLimits =
        ParserLimits.Default with { MaxTotalHeaderBytes = 64 * 1024, MaxHeaderCount = 200 };

    private static readonly Glyph11Limits NativeLimits = new()
    {
        StructSize = 32, MaxHeaderCount = 200, MaxHeaderNameLength = 256,
        MaxHeaderValueLength = 8192, MaxUrlLength = 8192, MaxQueryParameterCount = 128,
        MaxMethodLength = 16, MaxTotalHeaderBytes = 64 * 1024,
    };

    public static void Run(string dir)
    {
        var req = new BinaryRequest();
        var h = new Glyph11Field[256];
        var q = new Glyph11Field[256];
        (string name, string file, long iters)[] cases =
        {
            ("small", "small.bin", 2_000_000),
            ("4k", "h4k.bin", 500_000),
            ("32k", "h32k.bin", 100_000),
        };
        foreach (var (name, file, iters) in cases)
        {
            var data = File.ReadAllBytes(Path.Combine(dir, file));
            var rom = (ReadOnlyMemory<byte>)data;

            for (long i = 0; i < iters / 10 + 1; i++) { req.Clear(); var r = rom; UltraHardenedParser.TryExtractFullHeaderROM(ref r, req, in ManagedLimits, out _); }
            var sw = Stopwatch.StartNew();
            for (long i = 0; i < iters; i++) { req.Clear(); var r = rom; UltraHardenedParser.TryExtractFullHeaderROM(ref r, req, in ManagedLimits, out _); }
            sw.Stop();
            Console.WriteLine($"dotnet-managed,{name},{sw.Elapsed.TotalNanoseconds / iters:F1}");

            for (long i = 0; i < iters / 10 + 1; i++) Glyph11Parser.Parse(data, h, q, NativeLimits, out _);
            sw = Stopwatch.StartNew();
            for (long i = 0; i < iters; i++) Glyph11Parser.Parse(data, h, q, NativeLimits, out _);
            sw.Stop();
            Console.WriteLine($"dotnet-ffi,{name},{sw.Elapsed.TotalNanoseconds / iters:F1}");
        }
        req.Dispose();
    }
}
