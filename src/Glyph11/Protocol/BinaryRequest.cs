namespace Glyph11.Protocol;

/// <summary>
/// Holds the parsed components of an HTTP/1.1 request header.
/// All byte-level fields are <see cref="ReadOnlyMemory{T}"/> slices that reference
/// the original input buffer (zero-copy on the single-segment path).
/// <para>
/// Reuse instances across requests by calling <see cref="Clear"/> between parses.
/// Call <see cref="Dispose"/> when the instance is no longer needed to return
/// pooled arrays used by <see cref="Headers"/> and <see cref="QueryParameters"/>.
/// </para>
/// </summary>
public sealed class BinaryRequest : IDisposable
{
    private readonly KeyValueList _headers = new(), _query = new();

    /// <summary>HTTP version string, e.g. "HTTP/1.1". Set by UltraHardenedParser only.</summary>
    public ReadOnlyMemory<byte> Version { get; internal set; }

    /// <summary>HTTP method, e.g. "GET", "POST".</summary>
    public ReadOnlyMemory<byte> Method { get; internal set; }

    /// <summary>Request path without the query string, e.g. "/api/users".</summary>
    public ReadOnlyMemory<byte> Path { get; internal set; }

    /// <summary>Parsed query parameters as key-value pairs.</summary>
    public KeyValueList QueryParameters => _query;

    /// <summary>Parsed HTTP headers as key-value pairs.</summary>
    public KeyValueList Headers => _headers;

    /// <summary>Request body bytes. Not populated by the header parser.</summary>
    public ReadOnlyMemory<byte> Body { get; internal set; }

    /// <summary>
    /// Resets the request for reuse. Clears headers and query parameters
    /// but keeps the underlying pooled arrays allocated.
    /// </summary>
    public void Clear()
    {
        Version = default;
        Method = default;
        Path = default;
        Body = default;
        _headers.Clear();
        _query.Clear();
    }

    /// <summary>
    /// Returns pooled arrays to <see cref="System.Buffers.ArrayPool{T}"/>.
    /// The instance should not be used after disposal.
    /// </summary>
    public void Dispose()
    {
        _headers.Dispose();
        _query.Dispose();
    }

}
