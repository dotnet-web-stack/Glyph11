// Glyph11 (pure managed) — every option, end to end.
//
//   dotnet run --project Examples/Glyph11.Example
//
// Glyph11 is dependency-free and runs anywhere .NET runs — no native library needed.

using System.Buffers;
using System.Text;
using Glyph11;                     // HttpParseException
using Glyph11.Protocol;            // BinaryRequest, KeyValueList
using Glyph11.Parser;             // ParserLimits, ChunkedBodyStream
using Glyph11.Parser.UltraHardened; // UltraHardenedParser

// A BinaryRequest is reusable storage — allocate it once and Clear() it between requests
// to stay allocation-free across a connection (see Example 6).
var request = new BinaryRequest();

ContiguousParse();
MultiSegmentParse();
CustomLimits();
ChunkedBody();
ErrorHandling();
ReuseAndDispose();

request.Dispose(); // returns the pooled header/query arrays to the ArrayPool

// ─────────────────────────────────────────────────────────────────────────────
// 1. Parse a request that's already in one contiguous buffer (the fast path).
// ─────────────────────────────────────────────────────────────────────────────
void ContiguousParse()
{
    Console.WriteLine("== 1. Contiguous parse ==");

    // The request bytes as one block. TryExtractFullHeaderROM takes a ReadOnlyMemory<byte>.
    ReadOnlyMemory<byte> input = Encoding.ASCII.GetBytes(
        "GET /api/users?page=1&sort=asc HTTP/1.1\r\n" +
        "Host: example.com\r\n" +
        "Accept: */*\r\n\r\n");

    request.Clear();
    var limits = ParserLimits.Default;

    // Returns true on a complete, valid header block; false if the buffer is incomplete;
    // throws HttpParseException if the bytes are a complete but invalid/malicious request.
    if (UltraHardenedParser.TryExtractFullHeaderROM(ref input, request, in limits, out int bytesRead))
    {
        // Every field is a zero-copy ReadOnlyMemory<byte> slice of `input`.
        Console.WriteLine($"  method  = {Ascii(request.Method)}");   // GET
        Console.WriteLine($"  path    = {Ascii(request.Path)}");     // /api/users  (query stripped)
        Console.WriteLine($"  version = {Ascii(request.Version)}");  // HTTP/1.1

        // Headers are an ordered list of (name, value) byte-slice pairs.
        Console.WriteLine($"  headers ({request.Headers.Count}):");
        for (int i = 0; i < request.Headers.Count; i++)
        {
            var (name, value) = request.Headers[i];
            Console.WriteLine($"    {Ascii(name)}: {Ascii(value)}");
        }

        // Query parameters are parsed out of the request target.
        Console.WriteLine($"  query ({request.QueryParameters.Count}):");
        for (int i = 0; i < request.QueryParameters.Count; i++)
        {
            var (key, val) = request.QueryParameters[i];
            Console.WriteLine($"    {Ascii(key)} = {Ascii(val)}");
        }

        // bytesRead follows glyph11's -1 convention: the body begins at bytesRead + 1.
        Console.WriteLine($"  header block length = {bytesRead + 1} bytes");
    }
    Console.WriteLine();
}

// ─────────────────────────────────────────────────────────────────────────────
// 2. Parse a fragmented request (a ReadOnlySequence split across buffers) — what
//    you get from a PipeReader/socket. The parser walks the segments for you.
// ─────────────────────────────────────────────────────────────────────────────
void MultiSegmentParse()
{
    Console.WriteLine("== 2. Multi-segment parse ==");

    byte[] all = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\n\r\n");

    // Build a 3-segment ReadOnlySequence to simulate fragmented network reads.
    ReadOnlySequence<byte> buffer = ThreeSegments(all);

    request.Clear();
    var limits = ParserLimits.Default;

    // Same contract as Example 1, but for a (possibly) multi-segment sequence. It takes the
    // contiguous fast path automatically when the sequence happens to be single-segment.
    if (UltraHardenedParser.TryExtractFullHeaderValidated(ref buffer, request, in limits, out int bytesRead))
        Console.WriteLine($"  parsed {Ascii(request.Method)} {Ascii(request.Path)} from 3 segments\n");
}

// ─────────────────────────────────────────────────────────────────────────────
// 3. Tighten the security policy with a `with` expression (ParserLimits is a
//    readonly record struct). Exceeding a limit is a rejection (HTTP 431), never
//    an overflow.
// ─────────────────────────────────────────────────────────────────────────────
void CustomLimits()
{
    Console.WriteLine("== 3. Custom limits ==");

    var strict = ParserLimits.Default with
    {
        MaxHeaderCount       = 2,         // allow at most 2 headers
        MaxHeaderValueLength = 1024,
        MaxUrlLength         = 256,
        MaxTotalHeaderBytes  = 4 * 1024,
    };

    // This request has 3 headers — more than MaxHeaderCount = 2, so it's rejected.
    ReadOnlyMemory<byte> tooMany = Encoding.ASCII.GetBytes(
        "GET / HTTP/1.1\r\nHost: x\r\nA: 1\r\nB: 2\r\n\r\n");

    request.Clear();
    try
    {
        UltraHardenedParser.TryExtractFullHeaderROM(ref tooMany, request, in strict, out _);
        Console.WriteLine("  unexpectedly accepted");
    }
    catch (HttpParseException ex)
    {
        // A limit breach maps to HTTP 431; a structural/semantic error maps to 400.
        Console.WriteLine($"  rejected: {ex.Message}\n");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 4. Decode a chunked (Transfer-Encoding: chunked) body with ChunkedBodyStream.
// ─────────────────────────────────────────────────────────────────────────────
void ChunkedBody()
{
    Console.WriteLine("== 4. Chunked body ==");

    // "Hello" + " World" framed as two chunks, then the terminal 0-length chunk.
    byte[] body = Encoding.ASCII.GetBytes("5\r\nHello\r\n6\r\n World\r\n0\r\n\r\n");

    var decoder = new ChunkedBodyStream();
    var decoded = new ArrayBufferWriter<byte>();
    int offset = 0;

    while (true)
    {
        // consumed   = input bytes used (framing + payload)
        // dataOffset = where the payload starts, relative to the slice we passed
        // dataLength = payload byte count
        ChunkResult r = decoder.TryReadChunk(
            body.AsSpan(offset), out int consumed, out int dataOffset, out int dataLength);

        if (r == ChunkResult.Chunk)
        {
            decoded.Write(body.AsSpan(offset + dataOffset, dataLength));
            offset += consumed;
            continue;
        }
        // Completed = terminal 0-chunk seen (body done); NeedMoreData = read more, then retry.
        // (Malformed framing surfaces as an HttpParseException from the parse path.)
        break;
    }

    Console.WriteLine($"  decoded body = \"{Encoding.ASCII.GetString(decoded.WrittenSpan)}\"\n"); // Hello World
}

// ─────────────────────────────────────────────────────────────────────────────
// 5. The three outcomes: valid (true), incomplete (false), invalid (throws).
// ─────────────────────────────────────────────────────────────────────────────
void ErrorHandling()
{
    Console.WriteLine("== 5. Error handling ==");
    var limits = ParserLimits.Default;

    // (a) Incomplete — the header block isn't terminated yet. Returns false: read more.
    ReadOnlyMemory<byte> partial = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\n");
    request.Clear();
    bool ok = UltraHardenedParser.TryExtractFullHeaderROM(ref partial, request, in limits, out _);
    Console.WriteLine($"  incomplete request → returned {ok} (need more bytes)");

    // (b) Invalid — a path-traversal attempt. A complete but malicious request → throws.
    ReadOnlyMemory<byte> evil = Encoding.ASCII.GetBytes("GET /a/../../etc/passwd HTTP/1.1\r\nHost: x\r\n\r\n");
    request.Clear();
    try
    {
        UltraHardenedParser.TryExtractFullHeaderROM(ref evil, request, in limits, out _);
    }
    catch (HttpParseException ex)
    {
        Console.WriteLine($"  malicious request → threw: {ex.Message}\n");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 6. Reuse across requests, then dispose. Clear() keeps the pooled arrays; Dispose()
//    returns them. Not disposing won't leak — it just skips the pooling.
// ─────────────────────────────────────────────────────────────────────────────
void ReuseAndDispose()
{
    Console.WriteLine("== 6. Reuse ==");
    var limits = ParserLimits.Default;
    var shared = new BinaryRequest();

    foreach (var path in new[] { "/a", "/b", "/c" })
    {
        ReadOnlyMemory<byte> req = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: x\r\n\r\n");
        shared.Clear();   // reset before each parse; reuses the same pooled storage
        if (UltraHardenedParser.TryExtractFullHeaderROM(ref req, shared, in limits, out _))
            Console.WriteLine($"  parsed {Ascii(shared.Path)}");
    }

    shared.Dispose();
    Console.WriteLine();
}

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────
static string Ascii(ReadOnlyMemory<byte> m) => Encoding.ASCII.GetString(m.Span);

static ReadOnlySequence<byte> ThreeSegments(byte[] data)
{
    int s1 = data.Length / 3, s2 = 2 * data.Length / 3;
    var first = new Seg(data.AsMemory(0, s1));
    var last = first.Append(data.AsMemory(s1, s2 - s1)).Append(data.AsMemory(s2));
    return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
}

// Minimal ReadOnlySequenceSegment to chain memory blocks into one sequence.
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
