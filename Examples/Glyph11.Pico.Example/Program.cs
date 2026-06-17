// Glyph11.Pico (picohttpparser + managed glue) — every option, end to end.
//
//   dotnet run --project Examples/Glyph11.Pico.Example
//
// Fills the SAME BinaryRequest as the managed Glyph11 parser, but only picohttpparser's
// validation — fastest path to a request, no hardening. The NuGet bundles libglyph11pico;
// running in-repo, point at a built lib (see Examples/README.md):
//   GLYPH11_PICO_NATIVE_PATH=<...>/libglyph11pico.so  dotnet run --project Examples/Glyph11.Pico.Example

using System.Buffers;
using System.Text;
using Glyph11.Pico;
using Glyph11.Protocol; // BinaryRequest
using Glyph11.Parser;   // ChunkedBodyStream (chunked decoding is glyph11's)

// Same reusable BinaryRequest as the managed parser — allocate once, Clear() per request.
var request = new BinaryRequest();

ContiguousParse();
SequenceParse();
ChunkedBody();
ValidationTradeoff();

request.Dispose();

// ─────────────────────────────────────────────────────────────────────────────
// 1. Parse a contiguous buffer into a BinaryRequest (identical shape to Glyph11).
// ─────────────────────────────────────────────────────────────────────────────
void ContiguousParse()
{
    Console.WriteLine("== 1. Contiguous parse ==");

    byte[] input = "GET /api/users?page=1&sort=asc HTTP/1.1\r\nHost: example.com\r\nAccept: */*\r\n\r\n"u8.ToArray();

    request.Clear();

    // true → parsed; false → malformed or incomplete. `consumed` follows glyph11's -1
    // convention (the body begins at consumed + 1).
    if (PicoParser.TryParse(input, request, out int consumed))
    {
        Console.WriteLine($"  method  = {Ascii(request.Method)}");
        Console.WriteLine($"  path    = {Ascii(request.Path)}");     // query stripped
        Console.WriteLine($"  version = {Ascii(request.Version)}");

        for (int i = 0; i < request.Headers.Count; i++)
        {
            var (name, value) = request.Headers[i];
            Console.WriteLine($"  header  {Ascii(name)}: {Ascii(value)}");
        }
        for (int i = 0; i < request.QueryParameters.Count; i++)
        {
            var (key, val) = request.QueryParameters[i];
            Console.WriteLine($"  query   {Ascii(key)} = {Ascii(val)}");
        }
        Console.WriteLine($"  body begins at byte {consumed + 1}");
    }
    Console.WriteLine();
}

// ─────────────────────────────────────────────────────────────────────────────
// 2. Parse a fragmented ReadOnlySequence. Single-segment is zero-copy; multi-segment
//    is linearized into a fresh array internally — which the BinaryRequest slices keep
//    alive, so there's no buffer for you to manage.
// ─────────────────────────────────────────────────────────────────────────────
void SequenceParse()
{
    Console.WriteLine("== 2. ReadOnlySequence parse ==");

    byte[] all = "GET /x?a=1 HTTP/1.1\r\nHost: h\r\n\r\n"u8.ToArray();
    ReadOnlySequence<byte> seq = ThreeSegments(all);

    request.Clear();
    if (PicoParser.TryParse(seq, request, out _))
        Console.WriteLine($"  parsed {Ascii(request.Method)} {Ascii(request.Path)} from 3 segments\n");
}

// ─────────────────────────────────────────────────────────────────────────────
// 3. Chunked bodies use glyph11's decoder (this package depends on Glyph11).
// ─────────────────────────────────────────────────────────────────────────────
void ChunkedBody()
{
    Console.WriteLine("== 3. Chunked body (via Glyph11.Parser.ChunkedBodyStream) ==");

    byte[] body = "5\r\nHello\r\n6\r\n World\r\n0\r\n\r\n"u8.ToArray();
    var decoder = new ChunkedBodyStream();
    var decoded = new ArrayBufferWriter<byte>();
    int offset = 0;

    while (true)
    {
        ChunkResult r = decoder.TryReadChunk(
            body.AsSpan(offset), out int consumed, out int dataOffset, out int dataLength);
        if (r == ChunkResult.Chunk) { decoded.Write(body.AsSpan(offset + dataOffset, dataLength)); offset += consumed; continue; }
        break; // Completed (done) or NeedMoreData (read more)
    }
    Console.WriteLine($"  decoded = \"{Encoding.ASCII.GetString(decoded.WrittenSpan)}\"\n");
}

// ─────────────────────────────────────────────────────────────────────────────
// 4. The trade-off: Pico does NOT do glyph11's hardening. A path-traversal request
//    that the managed/native parser rejects is happily tokenized here — so only use
//    Pico when you validate elsewhere or trust the source.
// ─────────────────────────────────────────────────────────────────────────────
void ValidationTradeoff()
{
    Console.WriteLine("== 4. Validation trade-off ==");

    // Glyph11 / Glyph11.Native would throw / reject this (dot-segment traversal). Pico parses it.
    byte[] evil = "GET /a/../../etc/passwd HTTP/1.1\r\nHost: x\r\n\r\n"u8.ToArray();

    request.Clear();
    if (PicoParser.TryParse(evil, request, out _))
    {
        Console.WriteLine($"  Pico accepted path = {Ascii(request.Path)}");
        Console.WriteLine("  → validate paths/tokens yourself when using Pico on untrusted input.\n");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
static string Ascii(ReadOnlyMemory<byte> m) => Encoding.ASCII.GetString(m.Span);

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
