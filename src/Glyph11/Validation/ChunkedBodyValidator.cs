
namespace Glyph11.Validation;

/// <summary>
/// Validates chunked transfer-encoding body format per RFC 9112 §7.1.
/// </summary>
public static class ChunkedBodyValidator
{
    private const int MaxChunkExtensionBytes = 4096;

    /// <summary>
    /// Validates chunked body format.
    /// Returns true if valid and complete.
    /// Returns false if incomplete (need more data).
    /// Throws <see cref="HttpParseException"/> if malformed.
    /// </summary>
    public static bool TryValidate(ReadOnlySpan<byte> body, out int bytesConsumed)
    {
        bytesConsumed = 0;
        int pos = 0;

        while (pos < body.Length)
        {
            // --- Parse chunk size (hex digits) ---
            int sizeStart = pos;

            // Reject leading whitespace
            if (pos < body.Length && (body[pos] == (byte)' ' || body[pos] == (byte)'\t'))
                throw new HttpParseException("Leading whitespace in chunk size.");

            // Reject negative sign
            if (pos < body.Length && body[pos] == (byte)'-')
                throw new HttpParseException("Negative chunk size.");

            // Reject 0x prefix
            if (pos + 1 < body.Length && body[pos] == (byte)'0' &&
                (body[pos + 1] == (byte)'x' || body[pos + 1] == (byte)'X'))
                throw new HttpParseException("Hex prefix in chunk size.");

            // Read hex digits
            long chunkSize = 0;
            int digitCount = 0;
            while (pos < body.Length && IsHexDigit(body[pos]))
            {
                if (digitCount >= 16) // overflow guard: 16 hex digits = 64 bits
                    throw new HttpParseException("Chunk size overflow.");

                chunkSize = (chunkSize << 4) | (uint)HexVal(body[pos]);
                digitCount++;
                pos++;
            }

            if (digitCount == 0)
            {
                if (pos < body.Length)
                    throw new HttpParseException("Invalid character in chunk size.");
                return false; // incomplete
            }

            // Reject underscore after digits (e.g., "5_0")
            if (pos < body.Length && body[pos] == (byte)'_')
                throw new HttpParseException("Underscore in chunk size.");

            // --- Parse optional chunk extensions (;token=value) ---
            if (pos < body.Length && body[pos] == (byte)';')
            {
                int extStart = pos;
                pos++; // skip ';'

                while (pos < body.Length)
                {
                    if (pos - extStart > MaxChunkExtensionBytes)
                        throw new HttpParseException("Chunk extension too large.");

                    // Check for NUL
                    if (body[pos] == 0)
                        throw new HttpParseException("NUL byte in chunk extension.");

                    // bare LF
                    if (body[pos] == (byte)'\n')
                        throw new HttpParseException("Bare LF in chunk extension.");

                    // CRLF ends the extension
                    if (body[pos] == (byte)'\r')
                        break;

                    // Another extension
                    if (body[pos] == (byte)';')
                    {
                        pos++;
                        continue;
                    }

                    pos++;
                }
            }

            // --- Expect CRLF after chunk size (+ extensions) ---
            if (pos + 1 >= body.Length)
                return false; // incomplete

            if (body[pos] == (byte)'\n')
                throw new HttpParseException("Bare LF after chunk size.");

            if (body[pos] != (byte)'\r' || body[pos + 1] != (byte)'\n')
                throw new HttpParseException("Missing CRLF after chunk size.");

            pos += 2;

            // --- Last chunk (size == 0): parse optional trailers ---
            if (chunkSize == 0)
            {
                // Trailers: zero or more header lines, terminated by CRLF
                while (true)
                {
                    if (pos >= body.Length)
                        return false; // incomplete

                    // Empty line = end of trailers
                    if (pos + 1 < body.Length && body[pos] == (byte)'\r' && body[pos + 1] == (byte)'\n')
                    {
                        pos += 2;
                        bytesConsumed = pos;
                        return true;
                    }

                    // Bare LF as end?
                    if (body[pos] == (byte)'\n')
                        throw new HttpParseException("Bare LF in chunked trailer.");

                    // Read trailer line until CRLF
                    bool foundCrlf = false;
                    while (pos < body.Length)
                    {
                        if (body[pos] == (byte)'\n' && (pos == 0 || body[pos - 1] != (byte)'\r'))
                            throw new HttpParseException("Bare LF in chunked trailer.");

                        if (pos + 1 < body.Length && body[pos] == (byte)'\r' && body[pos + 1] == (byte)'\n')
                        {
                            pos += 2;
                            foundCrlf = true;
                            break;
                        }

                        pos++;
                    }

                    if (!foundCrlf)
                        return false; // incomplete trailer line
                }
            }

            // --- Read chunk data (exactly chunkSize bytes) ---
            if (pos + chunkSize > body.Length)
                return false; // incomplete

            // Check for NUL-spill: data claims to extend beyond what was sent
            // (handled by the length check above)

            pos += (int)chunkSize;

            // --- Expect CRLF after chunk data ---
            if (pos + 1 >= body.Length)
                return false; // incomplete

            if (body[pos] == (byte)'\n' && (pos == 0 || body[pos - 1] != (byte)'\r'))
                throw new HttpParseException("Bare LF after chunk data.");

            if (body[pos] != (byte)'\r' || body[pos + 1] != (byte)'\n')
                throw new HttpParseException("Missing CRLF after chunk data.");

            pos += 2;
        }

        return false; // incomplete
    }

    private static bool IsHexDigit(byte b)
        => (b >= (byte)'0' && b <= (byte)'9') ||
           (b >= (byte)'a' && b <= (byte)'f') ||
           (b >= (byte)'A' && b <= (byte)'F');

    private static int HexVal(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => 0
    };
}
