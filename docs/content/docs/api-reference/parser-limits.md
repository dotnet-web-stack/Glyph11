---
title: ParserLimits
weight: 3
---

**Namespace:** `Glyph11.Parser`

```csharp
public readonly record struct ParserLimits
```

A `record struct` defining configurable resource limits for the parser. Use `with` expressions to customize.

## Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MaxHeaderCount` | `int` | 100 | Maximum number of headers per request |
| `MaxHeaderNameLength` | `int` | 256 | Maximum bytes for a single header name |
| `MaxHeaderValueLength` | `int` | 8192 | Maximum bytes for a single header value |
| `MaxUrlLength` | `int` | 8192 | Maximum bytes for the request URL |
| `MaxQueryParameterCount` | `int` | 128 | Maximum number of query parameters |
| `MaxMethodLength` | `int` | 16 | Maximum bytes for the HTTP method |
| `MaxTotalHeaderBytes` | `int` | 1048576 | Maximum total bytes for the header section (1 MiB) |

## Static Properties

| Property | Type | Description |
|----------|------|-------------|
| `Default` | `ParserLimits` | Returns a `ParserLimits` with all default values |

## Declaration

```csharp
namespace Glyph11.Parser;

public readonly record struct ParserLimits
{
    public int MaxHeaderCount           { get; init; }
    public int MaxHeaderNameLength      { get; init; }
    public int MaxHeaderValueLength     { get; init; }
    public int MaxUrlLength             { get; init; }
    public int MaxQueryParameterCount   { get; init; }
    public int MaxMethodLength          { get; init; }
    public int MaxTotalHeaderBytes      { get; init; }

    public static ParserLimits Default { get; }
}
```
