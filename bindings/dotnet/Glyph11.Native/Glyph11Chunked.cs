using System.Runtime.InteropServices;

namespace Glyph11.Native;

/// <summary>Streaming chunked transfer-encoding decoder state (mirrors the C <c>glyph11_chunk_decoder</c>).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Glyph11ChunkDecoder
{
    public uint Phase;
    public uint DigitCount;
    public uint ExtBytes;
    public uint Reserved;
    public ulong ChunkSize;
    public ulong Remaining;
}

/// <summary>Result of <see cref="Glyph11Chunked.Decode"/>.</summary>
public enum Glyph11ChunkResult
{
    /// <summary>Progress made — drain the output, supply more input, and call again.</summary>
    Ok = 0,

    /// <summary>Terminal chunk and trailers consumed; the body is complete.</summary>
    Done = 1,

    /// <summary>Malformed chunked encoding — the server should respond 400.</summary>
    Error = 2,
}

/// <summary>
/// Managed wrapper over the Glyph11 C streaming chunked-body decoder (<c>libglyph11</c>).
/// Strips chunk framing and writes the decoded body into a caller-provided buffer — no
/// allocation. Reuse one <see cref="Glyph11ChunkDecoder"/> across calls for a single body.
/// </summary>
public static class Glyph11Chunked
{
    private const string Lib = "glyph11";

    /// <summary>Reset a decoder to the start state (its zero value is the start state).</summary>
    public static void Init(out Glyph11ChunkDecoder decoder) => decoder = default;

    /// <summary>
    /// Decode chunked data: strip the framing, writing decoded body bytes into <paramref name="output"/>.
    /// Call repeatedly as data arrives; a chunk's payload may be split across calls.
    /// </summary>
    /// <returns>
    /// <see cref="Glyph11ChunkResult.Done"/> when the body is complete,
    /// <see cref="Glyph11ChunkResult.Error"/> on a malformed encoding, otherwise
    /// <see cref="Glyph11ChunkResult.Ok"/> — the call stopped because input was exhausted or output
    /// filled; advance by <paramref name="inConsumed"/>, drain <paramref name="outWritten"/>, call again.
    /// </returns>
    public static unsafe Glyph11ChunkResult Decode(
        ref Glyph11ChunkDecoder decoder,
        ReadOnlySpan<byte> input,
        Span<byte> output,
        out int inConsumed,
        out int outWritten)
    {
        fixed (Glyph11ChunkDecoder* pd = &decoder)
        fixed (byte* pin = input)
        fixed (byte* pout = output)
        {
            nuint consumed, written;
            int r = glyph11_chunk_decode(pd, pin, (nuint)input.Length, pout, (nuint)output.Length, &consumed, &written);
            inConsumed = (int)consumed;
            outWritten = (int)written;
            return (Glyph11ChunkResult)r;
        }
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int glyph11_chunk_decode(
        Glyph11ChunkDecoder* dec,
        byte* @in, nuint inLen,
        byte* @out, nuint outCap,
        nuint* inConsumed, nuint* outWritten);
}
