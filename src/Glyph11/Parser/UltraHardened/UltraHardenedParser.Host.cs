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
        if (address.Length < 2)
            return false;

        // Only "::" may open the address.
        bool elided = address[0] == (byte)':';
        if (elided && address[1] != (byte)':')
            return false;

        int i = elided ? 2 : 0;
        int groups = 0;

        while (i < address.Length)
        {
            int digits = SemCountHexDigits(address[i..]);

            // ls32 as IPv4address: the rest of the literal, counting as two groups.
            if (i + digits < address.Length && address[i + digits] == (byte)'.')
                return SemIsValidIPv4(address[i..]) && SemIsCompleteIPv6(groups + 2, elided);

            if (digits is 0 or > 4)
                return false;

            groups++;
            i += digits;

            if (!SemSkipIPv6Separator(address, ref i, ref elided))
                return false;
        }

        return SemIsCompleteIPv6(groups, elided);
    }

    /// <summary>
    /// The hex digits <paramref name="s"/> starts with, counted up to five: one more than a group
    /// may hold, so an over-long group is seen as one.
    /// </summary>
    private static int SemCountHexDigits(ReadOnlySpan<byte> s)
    {
        int n = 0;
        while (n < s.Length && n < 5 && SemIsHexDigit(s[n]))
            n++;
        return n;
    }

    /// <summary>
    /// Steps <paramref name="i"/> over what follows an IPv6 group: the end of the address, the ':'
    /// before the next group, or the one "::" allowed. False for anything else, including a lone
    /// trailing ':'.
    /// </summary>
    private static bool SemSkipIPv6Separator(ReadOnlySpan<byte> address, ref int i, ref bool elided)
    {
        if (i == address.Length)
            return true;

        if (address[i] != (byte)':' || ++i == address.Length)
            return false;

        if (address[i] != (byte)':')
            return true;

        if (elided)
            return false; // a second "::"

        elided = true;
        i++;
        return true;
    }

    /// <summary>Eight groups, or fewer when a "::" stands for at least one zero group.</summary>
    private static bool SemIsCompleteIPv6(int groups, bool elided) => elided ? groups <= 7 : groups == 8;

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
