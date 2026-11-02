using System.Buffers;
using System.Runtime.CompilerServices;

namespace Glyph11.Parser.UltraHardened;

public static partial class UltraHardenedParser
{
    // =====================================================================
    //  Locating the end of the header section — RFC 9112 §2.2
    //
    //  The hot path is a CRLFCRLF search and nothing else (TryLocateHeaderSection for the
    //  single-buffer parsers). Everything else here runs only when that search fails, or when the
    //  caller opted into bare-LF messages (ParserLimits.AllowBareLf).
    //
    //  Two rules:
    //    - A message is never reported incomplete because of a bare LF. "Incomplete" asks the
    //      caller for more bytes; once a CRLF message holds a bare LF no later byte can make it
    //      valid, so the answer is a rejection, not a wait.
    //    - Bare LF is accepted only uniformly. The first line terminator of the message decides
    //      its style: CRLF throughout, or bare LF throughout. A message that mixes the two is
    //      exactly where two parsers disagree about where a line ended, so it is rejected.
    // =====================================================================

    private static ReadOnlySpan<byte> Lf => "\n"u8;
    private static ReadOnlySpan<byte> LfLf => "\n\n"u8;

    private const string BareLfMessage = "Bare LF detected; only CRLF line endings are allowed.";
    private const string MixedLineEndingsMessage = "Mixed CRLF and bare LF line endings.";

    /// <summary>
    /// The header section of a single-buffer message, shared by the request and response parsers:
    /// its length including the terminating empty line, and the line terminator it uses. Returns
    /// <c>false</c> if more bytes are needed.
    /// </summary>
    /// <param name="span">The bytes received so far.</param>
    /// <param name="limits">The caller's limits: <see cref="ParserLimits.AllowBareLf"/> and
    /// <see cref="ParserLimits.MaxTotalHeaderBytes"/>.</param>
    /// <param name="totalHeaderBytes">Bytes up to and including the terminating empty line.</param>
    /// <param name="eol">CRLF, or LF for a bare-LF message the caller allowed.</param>
    /// <exception cref="HttpParseException">
    /// The bytes so far can never form a valid header section, or the section exceeds
    /// <see cref="ParserLimits.MaxTotalHeaderBytes"/> (status 431).
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryLocateHeaderSection(
        ReadOnlySpan<byte> span, in ParserLimits limits, out int totalHeaderBytes, out ReadOnlySpan<byte> eol)
    {
        int headerEnd = span.IndexOf(ParserConstants.CrlfCrlf);

        // ---- No CRLFCRLF yet, or bare LF allowed: off the hot path ----
        bool bareLf = false;
        if (headerEnd < 0 || limits.AllowBareLf)
        {
            headerEnd = LocateHeaderEnd(span, headerEnd, limits.AllowBareLf, out bareLf);
            if (headerEnd < 0)
            {
                totalHeaderBytes = 0;
                eol = default;
                return false;
            }
        }

        // A bare-LF message ends in LFLF and splits its lines on LF; every other one, CRLF.
        eol = bareLf ? Lf : ParserConstants.Crlf;

        totalHeaderBytes = headerEnd + 2 * eol.Length;
        if (totalHeaderBytes > limits.MaxTotalHeaderBytes)
            throw new HttpParseException("Total header size exceeds limit.", statusCode: 431);

        return true;
    }

    /// <summary>
    /// Header-section end for the single-buffer parsers, called when no CRLFCRLF was found or bare
    /// LF is allowed. Returns where the terminating empty line's sequence starts (the index of
    /// CRLFCRLF, or of LFLF when <paramref name="bareLf"/> is set), or -1 if more bytes are needed.
    /// </summary>
    /// <param name="span">The bytes received so far.</param>
    /// <param name="crlfCrlf">Index of the first CRLFCRLF in <paramref name="span"/>, or -1.</param>
    /// <param name="allowBareLf">Whether the caller accepts bare-LF messages.</param>
    /// <param name="bareLf">Set when the message uses bare LF line endings throughout.</param>
    /// <exception cref="HttpParseException">The bytes so far can never form a valid header section.</exception>
    private static int LocateHeaderEnd(ReadOnlySpan<byte> span, int crlfCrlf, bool allowBareLf, out bool bareLf)
    {
        bareLf = false;

        if (allowBareLf)
        {
            int firstLf = span.IndexOf((byte)'\n');
            if (firstLf < 0)
                return -1;

            if (firstLf == 0 || span[firstLf - 1] != (byte)'\r')
            {
                // A bare-LF message: the section ends at the first empty line, and a CR anywhere
                // in it means the message mixes line endings.
                bareLf = true;

                int lfLf = span.IndexOf(LfLf);
                var section = lfLf < 0 ? span : span[..(lfLf + 2)];
                if (section.IndexOf((byte)'\r') >= 0)
                    throw new HttpParseException(MixedLineEndingsMessage);

                return lfLf;
            }

            // A CRLF message: from here on, exactly the strict rules.
        }

        if (crlfCrlf >= 0)
            return crlfCrlf;

        // No terminator yet. A bare LF already present settles the outcome.
        if (ContainsBareLf(span))
            throw new HttpParseException(BareLfMessage);

        return -1;
    }

    /// <summary>
    /// Header-section end for the multi-segment entry points. Advances <paramref name="reader"/>
    /// (positioned at the start of its sequence) past the CRLFCRLF — or past the LFLF of a bare-LF
    /// message the caller allowed — and returns <c>true</c>; returns <c>false</c>, reader unmoved,
    /// if more bytes are needed. Reaches the same decisions as <see cref="LocateHeaderEnd"/>
    /// without linearizing, so an incomplete call allocates nothing.
    /// </summary>
    /// <exception cref="HttpParseException">The bytes so far can never form a valid header section.</exception>
    private static bool TryReadPastHeaderEnd(ref SequenceReader<byte> reader, in ParserLimits limits)
    {
        var sequence = reader.Sequence;

        if (limits.AllowBareLf && FirstLineEndsInBareLf(sequence))
        {
            var probe = reader;
            bool complete = probe.TryReadTo(out ReadOnlySequence<byte> _, LfLf, advancePastDelimiter: true);

            var section = complete ? sequence.Slice(0, probe.Consumed) : sequence;
            if (section.PositionOf((byte)'\r') is not null)
                throw new HttpParseException(MixedLineEndingsMessage);

            if (complete)
                reader = probe;
            return complete;
        }

        if (reader.TryReadTo(out ReadOnlySequence<byte> _, ParserConstants.CrlfCrlf, advancePastDelimiter: true))
            return true;

        // No terminator yet. A bare LF already present settles the outcome.
        if (ContainsBareLf(sequence))
            throw new HttpParseException(BareLfMessage);

        return false;
    }

    /// <summary>True if <paramref name="span"/> holds an LF not immediately preceded by CR.</summary>
    private static bool ContainsBareLf(ReadOnlySpan<byte> span)
    {
        int from = 0;
        while (true)
        {
            int lf = span[from..].IndexOf((byte)'\n');
            if (lf < 0)
                return false;

            lf += from;
            if (lf == 0 || span[lf - 1] != (byte)'\r')
                return true;

            from = lf + 1;
        }
    }

    /// <summary>
    /// True if <paramref name="sequence"/> holds an LF not immediately preceded by CR, where the
    /// preceding byte may sit at the end of the previous segment.
    /// </summary>
    private static bool ContainsBareLf(in ReadOnlySequence<byte> sequence)
    {
        byte previous = 0; // no byte before the first one: an LF there is bare
        foreach (var memory in sequence)
        {
            var span = memory.Span;
            if (span.IsEmpty)
                continue;

            int from = 0;
            while (true)
            {
                int lf = span[from..].IndexOf((byte)'\n');
                if (lf < 0)
                    break;

                lf += from;
                byte before = lf == 0 ? previous : span[lf - 1];
                if (before != (byte)'\r')
                    return true;

                from = lf + 1;
            }

            previous = span[^1];
        }

        return false;
    }

    /// <summary>
    /// True if the first line terminator in <paramref name="sequence"/> is a bare LF; false if it is
    /// CRLF or there is no LF yet.
    /// </summary>
    private static bool FirstLineEndsInBareLf(in ReadOnlySequence<byte> sequence)
    {
        byte previous = 0;
        foreach (var memory in sequence)
        {
            var span = memory.Span;
            if (span.IsEmpty)
                continue;

            int lf = span.IndexOf((byte)'\n');
            if (lf >= 0)
                return (lf == 0 ? previous : span[lf - 1]) != (byte)'\r';

            previous = span[^1];
        }

        return false;
    }
}
