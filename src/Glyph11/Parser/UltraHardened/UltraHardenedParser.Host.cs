using System.Buffers;

namespace Glyph11.Parser.UltraHardened;

public static partial class UltraHardenedParser
{
    // reg-name characters other than pct-encoded (RFC 3986 §3.2.2):
    //   unreserved = ALPHA / DIGIT / "-" / "." / "_" / "~"
    //   sub-delims = "!" / "$" / "&" / "'" / "(" / ")" / "*" / "+" / "," / ";" / "="
    private static readonly SearchValues<byte> HostRegNameChars = SearchValues.Create(
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~!$&'()*+,;="u8);

    /// <summary>
    /// Validates a Host field value against RFC 9110 §7.2 / RFC 3986 §3.2.2:
    /// <c>Host = uri-host [ ":" port ]</c>, <c>uri-host = IP-literal / reg-name</c>.
    /// <para>
    /// IPv4address needs no branch of its own: it is syntactically a reg-name. IP-literal is
    /// accepted for IPv6 only; IPvFuture and zone identifiers are refused. Stricter than the
    /// ABNF where the ABNF is looser than any real authority: the value must not be empty,
    /// the port must be 1-5 digits no greater than 65535, and a pct-encoded NUL (<c>%00</c>)
    /// is refused, as it is in the path.
    /// </para>
    /// </summary>
    private static bool SemIsValidHost(ReadOnlySpan<byte> value)
    {
        // Field values keep trailing OWS; it is not part of the value (RFC 9110 §5.5).
        value = SemTrimOWS(value);
        if (value.IsEmpty)
            return false;

        ReadOnlySpan<byte> rest;

        if (value[0] == (byte)'[')
        {
            // IP-literal = "[" IPv6address "]"
            int close = value.IndexOf((byte)']');
            if (close < 0 || !SemIsValidIPv6(value[1..close]))
                return false;

            rest = value[(close + 1)..];
        }
        else
        {
            // A reg-name holds no ':', so the first one starts the port.
            int colon = value.IndexOf((byte)':');
            var host = colon < 0 ? value : value[..colon];
            if (!SemIsValidRegName(host))
                return false;

            rest = colon < 0 ? default : value[colon..];
        }

        if (rest.IsEmpty)
            return true;

        return rest[0] == (byte)':' && SemIsValidPort(rest[1..]);
    }

    /// <summary>reg-name = 1*( unreserved / pct-encoded / sub-delims ).</summary>
    private static bool SemIsValidRegName(ReadOnlySpan<byte> name)
    {
        if (name.IsEmpty)
            return false;

        while (true)
        {
            int i = name.IndexOfAnyExcept(HostRegNameChars);
            if (i < 0)
                return true;

            // The only other byte a reg-name may hold is the '%' of a pct-encoded octet.
            if (name[i] != (byte)'%' || i + 2 >= name.Length ||
                !SemIsHexDigit(name[i + 1]) || !SemIsHexDigit(name[i + 2]))
                return false;

            if (name[i + 1] == (byte)'0' && name[i + 2] == (byte)'0')
                return false; // %00 — a NUL that truncates the name for whoever decodes it

            name = name[(i + 3)..];
        }
    }

    /// <summary>port = 1*5DIGIT, at most 65535.</summary>
    private static bool SemIsValidPort(ReadOnlySpan<byte> port)
    {
        if (port.IsEmpty || port.Length > 5)
            return false;

        int value = 0;
        foreach (byte b in port)
        {
            if (!ParserConstants.IsDigit(b))
                return false;
            value = value * 10 + (b - '0');
        }

        return value <= 65535;
    }

    /// <summary>
    /// IPv6address per RFC 3986 §3.2.2: eight 16-bit groups of 1-4 HEXDIG separated by ':',
    /// at most one "::" standing for one or more zero groups, and an optional dotted-quad IPv4
    /// in place of the last two groups.
    /// </summary>
    private static bool SemIsValidIPv6(ReadOnlySpan<byte> address)
    {
        int n = address.Length;
        if (n < 2)
            return false;

        int i = 0;
        int groups = 0;
        bool elided = false;

        if (address[0] == (byte)':')
        {
            // Only "::" may open the address.
            if (address[1] != (byte)':')
                return false;
            elided = true;
            i = 2;
        }

        while (i < n)
        {
            int start = i;
            while (i < n && i - start < 5 && SemIsHexDigit(address[i]))
                i++;

            if (i < n && address[i] == (byte)'.')
            {
                // ls32 as IPv4address: the rest of the literal, counting as two groups.
                if (!SemIsValidIPv4(address[start..]))
                    return false;
                groups += 2;
                break;
            }

            int length = i - start;
            if (length is 0 or > 4)
                return false;
            groups++;

            if (i == n)
                break;
            if (address[i] != (byte)':')
                return false;

            i++;
            if (i == n)
                return false; // a lone trailing ':'

            if (address[i] == (byte)':')
            {
                if (elided)
                    return false; // a second "::"
                elided = true;
                i++;
            }
        }

        return elided ? groups <= 7 : groups == 8;
    }

    /// <summary>
    /// IPv4address = dec-octet "." dec-octet "." dec-octet "." dec-octet, where a dec-octet is
    /// 0-255 without leading zeros (RFC 3986 §3.2.2).
    /// </summary>
    private static bool SemIsValidIPv4(ReadOnlySpan<byte> address)
    {
        int i = 0;
        int octets = 0;

        while (true)
        {
            int start = i;
            int value = 0;
            while (i < address.Length && i - start < 3 && ParserConstants.IsDigit(address[i]))
            {
                value = value * 10 + (address[i] - '0');
                i++;
            }

            int length = i - start;
            if (length == 0 || value > 255 || (length > 1 && address[start] == (byte)'0'))
                return false;

            if (++octets == 4)
                return i == address.Length;

            if (i == address.Length || address[i] != (byte)'.')
                return false;
            i++;
        }
    }

    private static bool SemIsHexDigit(byte b)
        => ParserConstants.IsDigit(b) || (uint)((b | 0x20) - 'a') <= 'f' - 'a';
}
