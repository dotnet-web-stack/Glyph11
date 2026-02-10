using System.Runtime.CompilerServices;
using System.Text;
using Glyph11.Protocol;

namespace Glyph11.Validation;

/// <summary>
/// Post-parse semantic validation helpers for HTTP/1.1 requests.
/// </summary>
public static class RequestSemantics
{
    private static ReadOnlySpan<byte> ContentLengthName => "content-length"u8;
    private static ReadOnlySpan<byte> TransferEncodingName => "transfer-encoding"u8;
    private static ReadOnlySpan<byte> HostName => "host"u8;
    private static ReadOnlySpan<byte> ChunkedValue => "chunked"u8;

    /// <summary>
    /// Returns true if the request has multiple Content-Length headers with differing values.
    /// (RFC 9110 §8.6 — request smuggling vector)
    /// </summary>
    public static bool HasConflictingContentLength(BinaryRequest request)
    {
        var headers = request.Headers;
        ReadOnlyMemory<byte> firstValue = default;
        bool found = false;

        for (int i = 0; i < headers.Count; i++)
        {
            var kv = headers[i];
            if (!AsciiEqualsIgnoreCase(kv.Key.Span, ContentLengthName))
                continue;

            if (!found)
            {
                firstValue = kv.Value;
                found = true;
                continue;
            }

            if (!kv.Value.Span.SequenceEqual(firstValue.Span))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true if the request has both Transfer-Encoding and Content-Length headers.
    /// (RFC 9112 §6.1 — request smuggling vector)
    /// </summary>
    public static bool HasTransferEncodingWithContentLength(BinaryRequest request)
    {
        var headers = request.Headers;
        bool hasTE = false;
        bool hasCL = false;

        for (int i = 0; i < headers.Count; i++)
        {
            var name = headers[i].Key.Span;
            if (!hasTE && AsciiEqualsIgnoreCase(name, TransferEncodingName))
                hasTE = true;
            if (!hasCL && AsciiEqualsIgnoreCase(name, ContentLengthName))
                hasCL = true;
            if (hasTE && hasCL)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true if the request path contains dot segments (/../, /./, trailing /.. or /.).
    /// (Directory traversal vector)
    /// </summary>
    public static bool HasDotSegments(BinaryRequest request)
    {
        var path = request.Path.Span;
        if (path.Length == 0)
            return false;

        int i = 0;
        while (i < path.Length)
        {
            if (path[i] != (byte)'/')
            {
                i++;
                continue;
            }

            // We're at a '/', check what follows
            int remaining = path.Length - i - 1;

            // "/." at end
            if (remaining == 1 && path[i + 1] == (byte)'.')
                return true;

            // "/.." at end
            if (remaining == 2 && path[i + 1] == (byte)'.' && path[i + 2] == (byte)'.')
                return true;

            // "/./" segment
            if (remaining >= 2 && path[i + 1] == (byte)'.' && path[i + 2] == (byte)'/')
                return true;

            // "/../" segment
            if (remaining >= 3 && path[i + 1] == (byte)'.' && path[i + 2] == (byte)'.' && path[i + 3] == (byte)'/')
                return true;

            i++;
        }

        return false;
    }

    /// <summary>
    /// Returns true if the request does not have exactly one Host header.
    /// (RFC 9112 §3.2 — HTTP/1.1 requires exactly one Host header)
    /// </summary>
    public static bool HasInvalidHostHeaderCount(BinaryRequest request)
    {
        var headers = request.Headers;
        int count = 0;

        for (int i = 0; i < headers.Count; i++)
        {
            if (AsciiEqualsIgnoreCase(headers[i].Key.Span, HostName))
                count++;
        }

        return count != 1;
    }

    /// <summary>
    /// Returns true if any Content-Length header value is not valid per
    /// RFC 9110 §8.6 / RFC 9112 §6.2: 1*DIGIT with optional comma-separated duplicates.
    /// Rejects empty values, non-digit characters (including +/-), leading zeros,
    /// and trailing commas.
    /// </summary>
    public static bool HasInvalidContentLengthFormat(BinaryRequest request)
    {
        var headers = request.Headers;

        for (int i = 0; i < headers.Count; i++)
        {
            if (!AsciiEqualsIgnoreCase(headers[i].Key.Span, ContentLengthName))
                continue;

            if (!IsValidContentLengthValue(headers[i].Value.Span))
                return true;
        }

        return false;
    }

    // Max digits in ulong.MaxValue (18446744073709551615)
    private const int MaxContentLengthDigits = 20;

    private static bool IsValidContentLengthValue(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty) return false;

        int pos = 0;
        while (pos < value.Length)
        {
            // Skip OWS before each element
            while (pos < value.Length && (value[pos] == (byte)' ' || value[pos] == (byte)'\t'))
                pos++;

            if (pos >= value.Length) return false;

            // Must start with a digit
            byte b = value[pos];
            if (b < (byte)'0' || b > (byte)'9') return false;

            // Reject leading zeros: "0" is ok, "00" or "007" is not
            int digitStart = pos;
            if (value[pos] == (byte)'0')
            {
                pos++;
                if (pos < value.Length && value[pos] >= (byte)'0' && value[pos] <= (byte)'9')
                    return false; // leading zero
            }
            else
            {
                pos++;
                while (pos < value.Length && value[pos] >= (byte)'0' && value[pos] <= (byte)'9')
                    pos++;
            }

            // Reject values that overflow ulong
            int digitCount = pos - digitStart;
            if (digitCount > MaxContentLengthDigits)
                return false;
            if (digitCount == MaxContentLengthDigits)
            {
                // Compare against "18446744073709551615" (ulong.MaxValue)
                ReadOnlySpan<byte> ulongMax = "18446744073709551615"u8;
                for (int j = 0; j < MaxContentLengthDigits; j++)
                {
                    if (value[digitStart + j] > ulongMax[j]) return false;
                    if (value[digitStart + j] < ulongMax[j]) break;
                }
            }

            // Skip OWS after the number
            while (pos < value.Length && (value[pos] == (byte)' ' || value[pos] == (byte)'\t'))
                pos++;

            // Must be end or comma
            if (pos >= value.Length) return true;
            if (value[pos] != (byte)',') return false;
            pos++; // skip comma
        }

        return false; // trailing comma with nothing after
    }

    /// <summary>
    /// Returns true if any Content-Length header value has leading zeros (e.g. "0200").
    /// Leading zeros can cause octal interpretation confusion in some parsers.
    /// </summary>
    public static bool HasContentLengthWithLeadingZeros(BinaryRequest request)
    {
        var headers = request.Headers;

        for (int i = 0; i < headers.Count; i++)
        {
            if (!AsciiEqualsIgnoreCase(headers[i].Key.Span, ContentLengthName))
                continue;

            var value = headers[i].Value.Span;
            if (HasLeadingZerosInValue(value))
                return true;
        }

        return false;
    }

    private static bool HasLeadingZerosInValue(ReadOnlySpan<byte> value)
    {
        // Check each comma-separated segment
        int start = 0;
        while (start < value.Length)
        {
            // Skip OWS
            while (start < value.Length && (value[start] == (byte)' ' || value[start] == (byte)'\t'))
                start++;

            // Find end of this segment
            int end = start;
            while (end < value.Length && value[end] != (byte)',')
                end++;

            int segLen = end - start;
            if (segLen > 1 && value[start] == (byte)'0')
                return true;

            start = end + 1;
        }
        return false;
    }

    /// <summary>
    /// Returns true if a single Content-Length header value contains comma-separated values
    /// that are not all identical. (RFC 9112 §6.2 — smuggling vector)
    /// </summary>
    public static bool HasConflictingCommaSeparatedContentLength(BinaryRequest request)
    {
        var headers = request.Headers;

        for (int i = 0; i < headers.Count; i++)
        {
            if (!AsciiEqualsIgnoreCase(headers[i].Key.Span, ContentLengthName))
                continue;

            var value = headers[i].Value.Span;
            if (value.IndexOf((byte)',') < 0)
                continue;

            // Split on commas, compare all segments
            ReadOnlySpan<byte> firstSegment = default;
            int start = 0;
            while (start < value.Length)
            {
                // Skip OWS
                while (start < value.Length && (value[start] == (byte)' ' || value[start] == (byte)'\t'))
                    start++;

                int end = start;
                while (end < value.Length && value[end] != (byte)',')
                    end++;

                // Trim trailing OWS
                int trimEnd = end;
                while (trimEnd > start && (value[trimEnd - 1] == (byte)' ' || value[trimEnd - 1] == (byte)'\t'))
                    trimEnd--;

                var segment = value[start..trimEnd];

                if (firstSegment.Length == 0)
                    firstSegment = segment;
                else if (!segment.SequenceEqual(firstSegment))
                    return true;

                start = end + 1;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if the request-target contains a fragment indicator (#).
    /// Fragments must not appear in HTTP request-targets (RFC 9112 §3.2).
    /// </summary>
    public static bool HasFragmentInRequestTarget(BinaryRequest request)
    {
        return request.Path.Span.IndexOf((byte)'#') >= 0;
    }

    /// <summary>
    /// Returns true if the request path contains backslash characters.
    /// Backslashes can be used for path traversal on Windows systems.
    /// </summary>
    public static bool HasBackslashInPath(BinaryRequest request)
    {
        return request.Path.Span.IndexOf((byte)'\\') >= 0;
    }

    /// <summary>
    /// Returns true if the request path contains double-encoded characters (%25).
    /// Double encoding can bypass security filters that only decode once.
    /// </summary>
    public static bool HasDoubleEncoding(BinaryRequest request)
    {
        var path = request.Path.Span;
        for (int i = 0; i < path.Length - 2; i++)
        {
            if (path[i] == (byte)'%' && path[i + 1] == (byte)'2' && path[i + 2] == (byte)'5')
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true if the request path contains a percent-encoded null byte (%00).
    /// Null bytes can cause path truncation in C-based file systems.
    /// </summary>
    public static bool HasEncodedNullByte(BinaryRequest request)
    {
        var path = request.Path.Span;
        for (int i = 0; i < path.Length - 2; i++)
        {
            if (path[i] == (byte)'%' && path[i + 1] == (byte)'0' && path[i + 2] == (byte)'0')
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true if the request path contains overlong UTF-8 sequences.
    /// Overlong encodings (e.g. 0xC0 0xAF for '/') can bypass ASCII path checks.
    /// (RFC 3629 §3 — overlong sequences are forbidden)
    /// </summary>
    public static bool HasOverlongUtf8(BinaryRequest request)
    {
        var path = request.Path.Span;
        for (int i = 0; i < path.Length; i++)
        {
            byte b = path[i];
            // 0xC0 and 0xC1 always produce overlong 2-byte sequences
            if (b == 0xC0 || b == 0xC1)
                return true;
            // 0xE0 followed by < 0xA0 is overlong 3-byte
            if (b == 0xE0 && i + 1 < path.Length && path[i + 1] < 0xA0)
                return true;
            // 0xF0 followed by < 0x90 is overlong 4-byte
            if (b == 0xF0 && i + 1 < path.Length && path[i + 1] < 0x90)
                return true;
        }
        return false;
    }

    private static ReadOnlySpan<byte> OptionsMethod => "options"u8;
    private static ReadOnlySpan<byte> ConnectMethod => "connect"u8;
    private static ReadOnlySpan<byte> Asterisk => "*"u8;

    /// <summary>
    /// Returns true if the Host header value contains userinfo (@) or path (/) components.
    /// (RFC 9110 §7.2 — Host must be host:port only)
    /// </summary>
    public static bool HasInvalidHostFormat(BinaryRequest request)
    {
        var headers = request.Headers;

        for (int i = 0; i < headers.Count; i++)
        {
            if (!AsciiEqualsIgnoreCase(headers[i].Key.Span, HostName))
                continue;

            var value = headers[i].Value.Span;
            if (value.IndexOf((byte)'@') >= 0 || value.IndexOf((byte)'/') >= 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true if the request-target is asterisk-form (*) but the method is not OPTIONS.
    /// (RFC 9112 §3.2.4 — asterisk-form is only valid for OPTIONS)
    /// </summary>
    public static bool HasAsteriskFormWithoutOptions(BinaryRequest request)
    {
        if (!request.Path.Span.SequenceEqual(Asterisk))
            return false;

        return !AsciiEqualsIgnoreCase(request.Method.Span, OptionsMethod);
    }

    /// <summary>
    /// Returns true if the request method is CONNECT.
    /// Origin servers should reject CONNECT requests (RFC 9110 §9.3.6 — CONNECT is for proxies).
    /// </summary>
    public static bool HasInvalidConnectRequest(BinaryRequest request)
    {
        return AsciiEqualsIgnoreCase(request.Method.Span, ConnectMethod);
    }

    /// <summary>
    /// Returns true if a Transfer-Encoding header value is present but does not
    /// equal "chunked" (after trimming OWS and case-insensitive comparison).
    /// Obfuscated TE values are a smuggling vector (RFC 9112 §6.1).
    /// </summary>
    public static bool HasInvalidTransferEncoding(BinaryRequest request)
    {
        var headers = request.Headers;

        for (int i = 0; i < headers.Count; i++)
        {
            if (!AsciiEqualsIgnoreCase(headers[i].Key.Span, TransferEncodingName))
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
            if (!AsciiEqualsIgnoreCase(trimmed, ChunkedValue))
                return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length)
            return false;

        for (int i = 0; i < a.Length; i++)
        {
            // OR 0x20 lowercases ASCII alpha; for non-alpha it's a no-op or
            // maps to a shared value — but since 'b' is already lowercase,
            // this only matches if 'a' is the same letter (upper or lower).
            if ((a[i] | 0x20) != b[i])
                return false;
        }

        return true;
    }
}
