using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Glyph11.Protocol;

namespace Glyph11.Pico;

/// <summary>
/// A fast HTTP/1.x request-header parser: picohttpparser (native, SSE4.2) does the
/// tokenizing, then minimal managed post-processing fills a <see cref="BinaryRequest"/>
/// with the same shape glyph11 produces — method, path, version, query parameters,
/// headers, body offset.
/// <para>
/// Unlike glyph11's hardened parser this does <b>no extra compliance checking</b> beyond
/// what picohttpparser does (no token / field-value validation, no path normalization,
/// no limits) — the trade is raw speed. For chunked request bodies, use glyph11's
/// <see cref="Glyph11.Parser.ChunkedBodyStream"/> (available via the Glyph11 dependency).
/// </para>
/// </summary>
public static class PicoParser
{
    // Synthesized version bytes (picohttpparser returns only the minor digit; major is 1).
    private static readonly byte[] Http11 = "HTTP/1.1"u8.ToArray();
    private static readonly byte[] Http10 = "HTTP/1.0"u8.ToArray();

    /// <summary>Header capacity per parse; requests with more headers are rejected (false).</summary>
    public const int MaxHeaders = 256;

    /// <summary>
    /// Parse one request header block from <paramref name="input"/> into
    /// <paramref name="request"/> (cleared first). Returns false on a malformed or
    /// incomplete header block. On success <paramref name="consumed"/> is the header-block
    /// length and <c>request.Body</c> is the remainder of the buffer.
    /// </summary>
    [SkipLocalsInit] // the scratch header array is written by the native call, not read uninitialized
    public static unsafe bool TryParse(ReadOnlyMemory<byte> input, BinaryRequest request, out int consumed)
    {
        request.Clear();
        consumed = 0;
        if (input.IsEmpty) return false;

        PicoSpan method, target;
        int minor;
        Span<PicoField> headers = stackalloc PicoField[MaxHeaders];
        nuint count = MaxHeaders;
        int ret;
        fixed (PicoField* hp = headers)
        {
            // Cheap pin: `fixed` on the backing array, not Memory.Pin() (a pinned GCHandle
            // costs tens of ns — dominant on small requests). Fall back to Pin() for the
            // rare non-array-backed memory.
            if (MemoryMarshal.TryGetArray(input, out ArraySegment<byte> seg))
            {
                fixed (byte* basePtr = seg.Array)
                    ret = PicoNative.pico_parse_request(basePtr + seg.Offset, (nuint)input.Length,
                        &method, &target, &minor, hp, &count);
            }
            else
            {
                using var handle = input.Pin();
                ret = PicoNative.pico_parse_request((byte*)handle.Pointer, (nuint)input.Length,
                    &method, &target, &minor, hp, &count);
            }
        }
        if (ret < 0) return false; // -1 parse error, -2 incomplete

        // Match glyph11's managed convention: `consumed` is headerLen - 1 (callers add +1
        // to find the body); Body below is sliced at the true header-block length.
        consumed = ret - 1;
        request.Method = input.Slice((int)method.Off, (int)method.Len);
        request.Version = minor == 0 ? Http10 : Http11;

        // Split the request-target into path + query (picohttpparser returns it whole).
        var targetMem = input.Slice((int)target.Off, (int)target.Len);
        int q = targetMem.Span.IndexOf((byte)'?');
        if (q < 0)
        {
            request.Path = targetMem;
        }
        else
        {
            request.Path = targetMem.Slice(0, q);
            ParseQuery(targetMem.Slice(q + 1), request.QueryParameters);
        }

        int n = (int)count;
        for (int i = 0; i < n; i++)
        {
            PicoSpan name = headers[i].Name, value = headers[i].Value;
            request.Headers.Add(input.Slice((int)name.Off, (int)name.Len),
                                input.Slice((int)value.Off, (int)value.Len));
        }

        request.Body = input.Slice(ret);
        return true;
    }

    /// <summary>
    /// Parse from a (possibly fragmented) <see cref="ReadOnlySequence{T}"/>. Single-segment
    /// input is parsed in place (zero-copy); multi-segment input is linearized into a fresh
    /// array first — picohttpparser needs one contiguous buffer — which the resulting
    /// <see cref="BinaryRequest"/> slices reference (so it stays alive, like glyph11's managed
    /// parser). Use the <see cref="ReadOnlyMemory{T}"/> overload when you already have a
    /// contiguous buffer.
    /// </summary>
    public static bool TryParse(in ReadOnlySequence<byte> input, BinaryRequest request, out int consumed)
    {
        if (input.IsSingleSegment)
            return TryParse(input.First, request, out consumed);
        return TryParse((ReadOnlyMemory<byte>)input.ToArray(), request, out consumed);
    }

    // Minimal query split on '&' / '=' — no percent-decoding, no validation (just be fast).
    private static void ParseQuery(ReadOnlyMemory<byte> query, KeyValueList into)
    {
        while (!query.IsEmpty)
        {
            int amp = query.Span.IndexOf((byte)'&');
            ReadOnlyMemory<byte> pair = amp < 0 ? query : query.Slice(0, amp);
            if (!pair.IsEmpty)
            {
                int eq = pair.Span.IndexOf((byte)'=');
                if (eq < 0) into.Add(pair, default);
                else into.Add(pair.Slice(0, eq), pair.Slice(eq + 1));
            }
            query = amp < 0 ? default : query.Slice(amp + 1);
        }
    }
}
