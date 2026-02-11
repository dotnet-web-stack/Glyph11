using System.Buffers;
using System.Runtime.CompilerServices;
using Glyph11.Protocol;
using Glyph11.Validation;

namespace Glyph11.Parser;

/// <summary>
/// 
/// </summary>
public static class ParserConstants
{
    // ---- Line terminators ----
    public static ReadOnlySpan<byte> Crlf => "\r\n"u8;
    public static ReadOnlySpan<byte> CrlfCrlf => "\r\n\r\n"u8;

    // ---- Special bytes ----
    public const byte Space = 0x20; // ' '
    public const byte Question = 0x3F; // '?'
    public const byte QuerySeparator = 0x26; // '&'
    public const byte Equal = 0x3D; // '='
    public const byte Colon = 0x3A; // ':'
    
    // ---- SIMD-accelerated character class validators (SearchValues<byte>) ----

    // Token chars (RFC 9110 §5.6.2): !#$%&'*+-.^_`|~ DIGIT ALPHA
    public static readonly SearchValues<byte> TokenSearchValues = SearchValues.Create(
        "!#$%&'*+-.0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ^_`abcdefghijklmnopqrstuvwxyz|~"u8);

    // Field-value chars (RFC 9110 §5.5): HTAB SP VCHAR obs-text
    public static readonly SearchValues<byte> FieldValueSearchValues = SearchValues.Create(
        BuildFieldValueBytes());

    // Request-target: only VCHAR (0x21-0x7E) and SP (0x20) — RFC 3986 URIs are ASCII-only
    public static readonly SearchValues<byte> RequestTargetSearchValues = SearchValues.Create(
        BuildRequestTargetBytes());

    public static byte[] BuildFieldValueBytes()
    {
        var bytes = new byte[1 + (0x7E - 0x20 + 1) + (0xFF - 0x80 + 1)];
        int i = 0;
        bytes[i++] = 0x09; // HTAB
        for (int b = 0x20; b <= 0x7E; b++) bytes[i++] = (byte)b; // SP + VCHAR
        for (int b = 0x80; b <= 0xFF; b++) bytes[i++] = (byte)b; // obs-text
        return bytes;
    }

    public static byte[] BuildRequestTargetBytes()
    {
        // ASCII VCHAR (0x21-0x7E) + SP (0x20) only — no non-ASCII (0x80-0xFF)
        var bytes = new byte[0x7E - 0x20 + 1];
        int i = 0;
        for (int b = 0x20; b <= 0x7E; b++) bytes[i++] = (byte)b;
        return bytes;
    }

    // ---- Validation helpers (Span) ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidToken(ReadOnlySpan<byte> span)
        => span.IndexOfAnyExcept(TokenSearchValues) < 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidFieldValue(ReadOnlySpan<byte> span)
        => span.IndexOfAnyExcept(FieldValueSearchValues) < 0;

    /// <summary>
    /// HTTP-version = "HTTP/" DIGIT "." DIGIT  (RFC 9112 §2.6)
    /// Exactly 8 bytes, case-sensitive.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidHttpVersion(ReadOnlySpan<byte> span)
    {
        return span.Length == 8
            && span[0] == (byte)'H'
            && span[1] == (byte)'T'
            && span[2] == (byte)'T'
            && span[3] == (byte)'P'
            && span[4] == (byte)'/'
            && span[5] == (byte)'1'
            && span[6] == (byte)'.'
            && (span[7] == (byte)'0' || span[7] == (byte)'1');
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDigit(byte b) => (uint)(b - '0') <= 9;

    /// <summary>
    /// Validates that a request-target contains no control characters (0x00-0x1F, 0x7F).
    /// RFC 9112 §3.2 — request-target must only contain VCHAR and unreserved/reserved URI chars.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidRequestTarget(ReadOnlySpan<byte> span)
        => span.IndexOfAnyExcept(RequestTargetSearchValues) < 0;
    
    
    
    
    public static ReadOnlySpan<byte> TransferEncodingName => "transfer-encoding"u8;
    public static ReadOnlySpan<byte> ChunkedValue => "chunked"u8;

    /// <summary>
    /// Inspects the parsed headers in <paramref name="request"/> and returns the body
    /// framing kind (chunked, content-length, or none) without touching any body bytes.
    /// </summary>
    public static BodyFramingResult DetectBodyFraming(BinaryRequest request)
    {
        if (HasChunkedTE(request))
            return BodyFramingResult.ForChunked;

        long cl = ContentLengthBodyReader.ParseContentLength(request);
        if (cl > 0)
            return BodyFramingResult.ForContentLength(cl);

        return BodyFramingResult.NoBody;
    }

    public static bool HasChunkedTE(BinaryRequest request)
    {
        var headers = request.Headers;

        for (int i = 0; i < headers.Count; i++)
        {
            var name = headers[i].Key.Span;
            if (!AsciiEqualsIgnoreCase(name, TransferEncodingName))
                continue;

            var value = headers[i].Value.Span;

            // Trim OWS
            int start = 0;
            while (start < value.Length && (value[start] == (byte)' ' || value[start] == (byte)'\t'))
                start++;
            int end = value.Length;
            while (end > start && (value[end - 1] == (byte)' ' || value[end - 1] == (byte)'\t'))
                end--;

            var trimmed = value[start..end];
            if (AsciiEqualsIgnoreCase(trimmed, ChunkedValue))
                return true;
        }

        return false;
    }

    public static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if ((a[i] | 0x20) != (b[i] | 0x20)) return false;
        }
        return true;
    }
}