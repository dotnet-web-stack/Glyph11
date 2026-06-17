// Reproduces the benchmark tables shown on the docs site: the three packages
// (Glyph11 managed, Glyph11.Native, Glyph11.Pico) on contiguous request parsing,
// multi-segment request parsing, and chunked-body decoding. Best-of-5, ns/op.
//
//   # build the native cores, then point at them and run:
//   cmake -S core -B core/build -DGLYPH11_BUILD_TESTS=OFF && cmake --build core/build
//   cmake -S bindings/dotnet/Glyph11.Pico/native -B pico-build && cmake --build pico-build
//   GLYPH11_NATIVE_PATH="$PWD/core/build/libglyph11.so" \
//   GLYPH11_PICO_NATIVE_PATH="$PWD/pico-build/libglyph11pico.so" \
//     dotnet run -c Release --project Benchmarks
//
// (Glyph11 — pure managed — needs no native library.)

using System.Buffers;
using System.Diagnostics;
using Glyph11.Native;
using Glyph11.Pico;
using Glyph11.Protocol;
using Glyph11.Parser;
using Glyph11.Parser.UltraHardened;

// ---- payloads (identical bytes for every parser) ----
byte[] small = Payloads.Small;
(string label, byte[] bytes)[] requests =
{
    ("~95 B", small),
    ("4 KB", Payloads.Header(4096)),
    ("32 KB", Payloads.Header(32768)),
};
(string label, byte[] bytes)[] chunked =
{
    ("256 B", Payloads.Chunked(256)),
    ("4 KB", Payloads.Chunked(4096)),
    ("32 KB", Payloads.Chunked(32768)),
};

// ---- policy + reusable storage (no per-request allocation in the parse itself) ----
var managedLimits = ParserLimits.Default with { MaxHeaderCount = 200, MaxTotalHeaderBytes = 64 * 1024 };
var nativeLimits = Glyph11Limits.Default;
nativeLimits.MaxHeaderCount = 200;
nativeLimits.MaxTotalHeaderBytes = 64 * 1024;

var headers = new Glyph11Field[256];
var query = new Glyph11Field[256];
var picoRequest = new BinaryRequest();
var managedRequest = new BinaryRequest();
var chunkOutput = new byte[64 * 1024];

long Iters(int len) => len < 1024 ? 2_000_000 : len < 16_384 ? 500_000 : 100_000;

// ---- contiguous request parsing ----
Console.WriteLine("Request header parsing — contiguous (ns/op, lower is better)");
Table(requests, (label, bytes) =>
{
    var rom = (ReadOnlyMemory<byte>)bytes;
    long it = Iters(bytes.Length);
    double glyph = Best(it, () => { managedRequest.Clear(); var r = rom; UltraHardenedParser.TryExtractFullHeaderROM(ref r, managedRequest, in managedLimits, out _); });
    double native = Best(it, () => Glyph11Parser.Parse(bytes, headers, query, nativeLimits, out _));
    double pico = Best(it, () => PicoParser.TryParse(rom, picoRequest, out _));
    return (glyph, native, pico);
});

// ---- multi-segment request parsing (3 segments, linearized per request, then parsed) ----
Console.WriteLine("\nRequest header parsing — multi-segment");
Table(requests, (label, bytes) =>
{
    var seq = ThreeSegments(bytes);
    int len = bytes.Length;
    long it = Iters(len);
    double glyph = Best(it, () => { managedRequest.Clear(); var s = seq; UltraHardenedParser.TryExtractFullHeaderValidated(ref s, managedRequest, in managedLimits, out _); });
    double native = Best(it, () => { var buf = new byte[len]; seq.CopyTo(buf); Glyph11Parser.Parse(buf, headers, query, nativeLimits, out _); });
    double pico = Best(it, () => { var buf = new byte[len]; seq.CopyTo(buf); PicoParser.TryParse(buf, picoRequest, out _); });
    return (glyph, native, pico);
});

// ---- chunked body decoding (Glyph11 == Pico, which reuses ChunkedBodyStream) ----
Console.WriteLine("\nChunked body decoding (Glyph11 / Pico share the decoder)");
Console.WriteLine($"  {"decoded",-8}{"Glyph11/Pico",16}{"Glyph11.Native",16}");
foreach (var (label, body) in chunked)
{
    long it = body.Length < 4096 ? 1_000_000 : body.Length < 32_768 ? 300_000 : 50_000;
    double glyph = Best(it, () => DecodeManaged(body, chunkOutput));
    double native = Best(it, () => DecodeNative(body, chunkOutput));
    Console.WriteLine($"  {label,-8}{glyph,13:F0} ns{native,13:F0} ns");
}

// ─────────────────────────────────────────────────────────────────────────────
void Table((string label, byte[] bytes)[] cases, Func<string, byte[], (double glyph, double native, double pico)> run)
{
    Console.WriteLine($"  {"payload",-8}{"Glyph11",13}{"Glyph11.Native",17}{"Glyph11.Pico",15}");
    foreach (var (label, bytes) in cases)
    {
        var (g, n, p) = run(label, bytes);
        Console.WriteLine($"  {label,-8}{g,10:F0} ns{n,14:F0} ns{p,12:F0} ns");
    }
}

static void DecodeManaged(byte[] body, byte[] output)
{
    var stream = new ChunkedBodyStream();
    int inOff = 0, outOff = 0;
    while (true)
    {
        var r = stream.TryReadChunk(body.AsSpan(inOff), out int consumed, out int dataOff, out int dataLen);
        if (r != ChunkResult.Chunk) break;
        body.AsSpan(inOff + dataOff, dataLen).CopyTo(output.AsSpan(outOff));
        outOff += dataLen;
        inOff += consumed;
    }
}

static void DecodeNative(byte[] body, byte[] output)
{
    Glyph11Chunked.Init(out var decoder);
    Glyph11Chunked.Decode(ref decoder, body, output, out _, out _);
}

// best of N timed trials (after warmup) — filters scheduling / turbo interference
static double Best(long iters, Action body)
{
    for (long i = 0; i < iters / 10 + 1; i++) body();
    double best = double.MaxValue;
    for (int t = 0; t < 5; t++)
    {
        var sw = Stopwatch.StartNew();
        for (long i = 0; i < iters; i++) body();
        sw.Stop();
        double ns = sw.Elapsed.TotalNanoseconds / iters;
        if (ns < best) best = ns;
    }
    return best;
}

static ReadOnlySequence<byte> ThreeSegments(byte[] data)
{
    int s1 = data.Length / 3, s2 = 2 * data.Length / 3;
    var first = new Seg(data.AsMemory(0, s1));
    var last = first.Append(data.AsMemory(s1, s2 - s1)).Append(data.AsMemory(s2));
    return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
}

sealed class Seg : ReadOnlySequenceSegment<byte>
{
    public Seg(ReadOnlyMemory<byte> memory) => Memory = memory;
    public Seg Append(ReadOnlyMemory<byte> memory)
    {
        var next = new Seg(memory) { RunningIndex = RunningIndex + Memory.Length };
        Next = next;
        return next;
    }
}

// Identical payload bytes to the docs-site benchmark.
static class Payloads
{
    public static readonly byte[] Small = System.Text.Encoding.ASCII.GetBytes(
        "GET /route?p1=1&p2=2&p3=3&p4=4 HTTP/1.1\r\n" +
        "Host: localhost\r\nContent-Length: 100\r\nServer: Glyph11\r\n\r\n");

    // A request whose header block is ~`target` bytes (many ~200-byte header values).
    public static byte[] Header(int target)
    {
        var sb = new System.Text.StringBuilder(target + 128);
        sb.Append("GET /route?p1=1&p2=2&p3=3&p4=4 HTTP/1.1\r\nHost: localhost\r\n");
        int i = 0;
        while (sb.Length < target - 4)
        {
            string name = $"X-Header-{i++}";
            int remaining = target - sb.Length - name.Length - 4;
            int vlen = Math.Min(Math.Max(remaining, 1), 200);
            sb.Append(name).Append(": ").Append('A', vlen).Append("\r\n");
        }
        sb.Append("\r\n");
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }

    // A chunked transfer-encoding body whose decoded length is `decodedSize` (512-byte chunks).
    public static byte[] Chunked(int decodedSize, int chunkSize = 512)
    {
        var body = new byte[decodedSize];
        for (int i = 0; i < decodedSize; i++) body[i] = (byte)(i % 251);
        var outp = new ArrayBufferWriter<byte>();
        for (int i = 0; i < decodedSize; i += chunkSize)
        {
            int c = Math.Min(chunkSize, decodedSize - i);
            outp.Write(System.Text.Encoding.ASCII.GetBytes($"{c:x}\r\n"));
            outp.Write(body.AsSpan(i, c));
            outp.Write("\r\n"u8);
        }
        outp.Write("0\r\n\r\n"u8);
        return outp.WrittenSpan.ToArray();
    }
}
