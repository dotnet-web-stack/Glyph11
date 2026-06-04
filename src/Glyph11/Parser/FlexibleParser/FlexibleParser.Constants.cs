// ReSharper disable SuggestVarOrType_BuiltInTypes

using System.Runtime.CompilerServices;
using Glyph11.Parser.UltraHardened;

namespace Glyph11.Parser.FlexibleParser;

/// <summary>
/// A fast, minimal-validation HTTP/1.1 header parser.
/// Optimized for throughput; silently skips malformed header lines
/// and does not enforce RFC token/field-value rules.
/// <para>
/// For security-sensitive workloads, use <see cref="UltraHardenedParser"/> instead.
/// </para>
/// </summary>
[SkipLocalsInit]
public static partial class FlexibleParser
{
    // ---- Line terminators ----
    private static ReadOnlySpan<byte> Crlf => "\r\n"u8;
    private static ReadOnlySpan<byte> CrlfCrlf => "\r\n\r\n"u8;

    // ---- Special bytes ----
    private const byte Space = 0x20; // ' '
    private const byte Question = 0x3F; // '?'
    private const byte QuerySeparator = 0x26; // '&'
    private const byte Equal = 0x3D; // '='
    private const byte Colon = 0x3A; // ':'
}
