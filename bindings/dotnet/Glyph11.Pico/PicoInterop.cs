using System.Runtime.InteropServices;

namespace Glyph11.Pico;

/// <summary>A byte range (offset + length) into the parsed input buffer.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PicoSpan
{
    public uint Off;
    public uint Len;
}

/// <summary>A header name/value pair as two spans into the input buffer.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PicoField
{
    public PicoSpan Name;
    public PicoSpan Value;
}

internal static class PicoNative
{
    private const string Lib = "glyph11pico";

    /// <summary>
    /// Offset-based wrapper over picohttpparser's <c>phr_parse_request</c>.
    /// Returns &gt;= 0 header-block length (consumed), -1 parse error, -2 incomplete.
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe int pico_parse_request(
        byte* buf, nuint len, PicoSpan* method, PicoSpan* target, int* minorVersion,
        PicoField* headers, nuint* numHeaders);
}
