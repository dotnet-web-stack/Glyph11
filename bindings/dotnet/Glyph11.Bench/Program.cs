using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Glyph11.Native;
using Glyph11.Parser;
using Glyph11.Parser.UltraHardened;
using Glyph11.Protocol;

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
