// Glyph11.Native (the C core via P/Invoke) — every option, end to end.
//
//   dotnet run --project Examples/Glyph11.Native.Example
//
// The NuGet package bundles the native libglyph11. Running in-repo, point at a built core:
//   GLYPH11_NATIVE_PATH=core/build/libglyph11.so  dotnet run --project Examples/Glyph11.Native.Example
// (see Examples/README.md). Output is zero-copy: the parser writes (offset,length) spans
// into arrays you provide; you slice the input buffer to read them.

using System.Buffers;
using System.Text;
using Glyph11.Native;

Console.WriteLine($"libglyph11 ABI = 0x{Glyph11Parser.AbiVersion():x6}\n");

ContiguousParse();
SequenceParse();
CustomLimitsAndPooling();
StatusCodes();
ChunkedDecode();

// ─────────────────────────────────────────────────────────────────────────────
// 1. Parse a contiguous buffer. You supply the field storage; nothing is allocated.
// ─────────────────────────────────────────────────────────────────────────────
void ContiguousParse()
{
    Console.WriteLine("== 1. Contiguous parse ==");

    byte[] request = Encoding.ASCII.GetBytes(
        "GET /api/users?page=1 HTTP/1.1\r\nHost: example.com\r\nAccept: */*\r\n\r\n");

    var limits = Glyph11Limits.Default;

    // Size storage to the limits so any request the policy accepts fits. The parser
    // bounds-checks every write — a smaller array just lowers your effective limit.
    Span<Glyph11Field> headers = stackalloc Glyph11Field[(int)limits.MaxHeaderCount];
    Span<Glyph11Field> query   = stackalloc Glyph11Field[(int)limits.MaxQueryParameterCount];

    int status = Glyph11Parser.Parse(request, headers, query, limits, out Glyph11Result r);
    if (status == Glyph11Parser.Ok)
    {
        // Offsets index into `request` — slice it to read.
        string Slice(Glyph11Span s) => Encoding.ASCII.GetString(request, (int)s.Offset, (int)s.Length);
        Console.WriteLine($"  method  = {Slice(r.Method)}");
        Console.WriteLine($"  path    = {Slice(r.Path)}");     // query stripped
        Console.WriteLine($"  target  = {Slice(r.Target)}");   // full target, as received
        Console.WriteLine($"  version = {Slice(r.Version)}");

        for (int i = 0; i < r.HeaderCount; i++)
            Console.WriteLine($"  header  {Slice(headers[i].Name)}: {Slice(headers[i].Value)}");
        for (int i = 0; i < r.QueryCount; i++)
            Console.WriteLine($"  query   {Slice(query[i].Name)} = {Slice(query[i].Value)}");

        Console.WriteLine($"  body begins at byte {r.Consumed}");
    }
    Console.WriteLine();
}

// ─────────────────────────────────────────────────────────────────────────────
// 2. Parse a fragmented ReadOnlySequence. The C core needs one contiguous buffer,
//    so multi-segment input is linearized into a scratch buffer YOU provide
//    (keeping the zero-allocation contract). `parsed` tells you which buffer the
//    offsets index into.
// ─────────────────────────────────────────────────────────────────────────────
void SequenceParse()
{
    Console.WriteLine("== 2. ReadOnlySequence parse ==");

    byte[] all = Encoding.ASCII.GetBytes("GET /x?a=1 HTTP/1.1\r\nHost: h\r\n\r\n");
    ReadOnlySequence<byte> seq = ThreeSegments(all); // simulate fragmented reads

    var limits = Glyph11Limits.Default;
    Span<Glyph11Field> headers = stackalloc Glyph11Field[(int)limits.MaxHeaderCount];
    Span<Glyph11Field> query   = stackalloc Glyph11Field[(int)limits.MaxQueryParameterCount];

    // scratch only needs to hold a request when the input is fragmented (single-segment
    // input is parsed in place). Size it to MaxTotalHeaderBytes.
    Span<byte> scratch = stackalloc byte[8 * 1024];

    int status = Glyph11Parser.Parse(
        seq, scratch, headers, query, limits,
        out Glyph11Result r,
        out ReadOnlySpan<byte> parsed); // ← the contiguous bytes the offsets index into

    if (status == Glyph11Parser.Ok)
    {
        // Slice against `parsed` (input's first segment if single, else `scratch`) — NOT `seq`.
        Console.WriteLine($"  method = {Encoding.ASCII.GetString(parsed.Slice((int)r.Method.Offset, (int)r.Method.Length))}");
        Console.WriteLine($"  path   = {Encoding.ASCII.GetString(parsed.Slice((int)r.Path.Offset,   (int)r.Path.Length))}\n");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 3. A custom policy, and pooled storage for large limits (instead of stackalloc).
// ─────────────────────────────────────────────────────────────────────────────
void CustomLimitsAndPooling()
{
    Console.WriteLine("== 3. Custom limits + pooled storage ==");

    var limits = Glyph11Limits.Default;     // Glyph11Limits is a struct — copy and set fields
    limits.MaxHeaderCount      = 200;
    limits.MaxTotalHeaderBytes = 64 * 1024;

    byte[] request = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\n\r\n");

    // Big limits → rent from ArrayPool rather than blowing the stack.
    Glyph11Field[] headers = ArrayPool<Glyph11Field>.Shared.Rent((int)limits.MaxHeaderCount);
    Glyph11Field[] query   = ArrayPool<Glyph11Field>.Shared.Rent((int)limits.MaxQueryParameterCount);
    try
    {
        int status = Glyph11Parser.Parse(request, headers, query, limits, out var r);
        Console.WriteLine($"  status={status} headers={r.HeaderCount}\n");
    }
    finally
    {
        ArrayPool<Glyph11Field>.Shared.Return(headers);
        ArrayPool<Glyph11Field>.Shared.Return(query);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 4. The status int: 0 = OK, 1 = incomplete, anything else maps to an HTTP code.
// ─────────────────────────────────────────────────────────────────────────────
void StatusCodes()
{
    Console.WriteLine("== 4. Status codes ==");
    var limits = Glyph11Limits.Default;
    Span<Glyph11Field> h = stackalloc Glyph11Field[(int)limits.MaxHeaderCount];
    Span<Glyph11Field> q = stackalloc Glyph11Field[(int)limits.MaxQueryParameterCount];

    // Incomplete: header block not terminated → status 1 (read more, retry).
    int incomplete = Glyph11Parser.Parse(
        Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\n"), h, q, limits, out _);
    Console.WriteLine($"  incomplete → status {incomplete} (Incomplete = {Glyph11Parser.Incomplete})");

    // Invalid: missing Host → an error status that maps to HTTP 400.
    int bad = Glyph11Parser.Parse(
        Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n"), h, q, limits, out _);
    Console.WriteLine($"  no-Host    → status {bad} → HTTP {Glyph11Parser.HttpCode(bad)}\n");
}

// ─────────────────────────────────────────────────────────────────────────────
// 5. Decode a chunked body with the streaming native decoder. Feed each read; a
//    chunk's payload may span calls and the decoder carries the partial state.
// ─────────────────────────────────────────────────────────────────────────────
void ChunkedDecode()
{
    Console.WriteLine("== 5. Chunked decode ==");

    byte[] chunked = Encoding.ASCII.GetBytes("5\r\nHello\r\n6\r\n World\r\n0\r\n\r\n");

    Glyph11Chunked.Init(out Glyph11ChunkDecoder decoder); // one decoder per body (zeroed state)
    Span<byte> output = stackalloc byte[256];             // decoded bytes land here

    Glyph11ChunkResult r = Glyph11Chunked.Decode(
        ref decoder, chunked, output, out int inConsumed, out int outWritten);

    Console.WriteLine($"  result={r} consumed={inConsumed} wrote={outWritten}");
    Console.WriteLine($"  decoded = \"{Encoding.ASCII.GetString(output[..outWritten])}\"\n"); // Hello World
}

// ─────────────────────────────────────────────────────────────────────────────
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
