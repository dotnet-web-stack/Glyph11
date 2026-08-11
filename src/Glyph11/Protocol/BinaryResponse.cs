namespace Glyph11.Protocol;

/// <summary>
/// Holds the parsed components of an HTTP/1.1 response header.
/// All byte-level fields are <see cref="ReadOnlyMemory{T}"/> slices that reference
/// the original input buffer (zero-copy on the single-segment path).
/// <para>
/// Reuse instances across responses by calling <see cref="Clear"/> between parses.
/// Call <see cref="Dispose"/> when the instance is no longer needed to return
/// pooled arrays used by <see cref="Headers"/>.
/// </para>
/// </summary>
public sealed class BinaryResponse : IDisposable
{
    private readonly KeyValueList _headers = new();

    /// <summary>HTTP version string, e.g. "HTTP/1.1". Set by UltraHardenedParser only.</summary>
    public ReadOnlyMemory<byte> Version { get; internal set; }

    /// <summary>
    /// Status code as the three bytes that arrived, e.g. "404". <see cref="Status"/> is the same
    /// value already parsed, and is what callers normally want.
    /// </summary>
    public ReadOnlyMemory<byte> StatusCode { get; internal set; }

    /// <summary>Status code as an integer, 100-599.</summary>
    public int Status { get; internal set; }

    /// <summary>
    /// Reason phrase, e.g. "Not Found". Empty is legal and common: HTTP/2 and HTTP/3 have no
    /// reason phrase at all, so anything translating from them emits none.
    /// </summary>
    public ReadOnlyMemory<byte> ReasonPhrase { get; internal set; }

    /// <summary>Parsed HTTP headers as key-value pairs.</summary>
    public KeyValueList Headers => _headers;

    /// <summary>Response body bytes. Not populated by the header parser.</summary>
    public ReadOnlyMemory<byte> Body { get; internal set; }

    /// <summary>
    /// Resets the response for reuse. Clears headers but keeps the underlying pooled
    /// arrays allocated.
    /// </summary>
    public void Clear()
    {
        Version = default;
        StatusCode = default;
        Status = 0;
        ReasonPhrase = default;
        Body = default;
        _headers.Clear();
    }

    /// <summary>
    /// Returns pooled arrays to <see cref="System.Buffers.ArrayPool{T}"/>.
    /// The instance should not be used after disposal.
    /// </summary>
    public void Dispose()
    {
        _headers.Dispose();
    }
}
