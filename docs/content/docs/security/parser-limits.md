---
title: Parser Limits
weight: 1
---

`ParserLimits` is a `record struct` that controls resource limits enforced during parsing. All limits are configurable via `with` expressions.

## Default Limits

```csharp
public readonly record struct ParserLimits
{
    public int MaxHeaderCount           { get; init; }  // default: 100
    public int MaxHeaderNameLength      { get; init; }  // default: 256
    public int MaxHeaderValueLength     { get; init; }  // default: 8192
    public int MaxUrlLength             { get; init; }  // default: 8192
    public int MaxQueryParameterCount   { get; init; }  // default: 128
    public int MaxMethodLength          { get; init; }  // default: 16
    public int MaxTotalHeaderBytes      { get; init; }  // default: 1048576

    public static ParserLimits Default { get; }
}
```

## Customizing Limits

Use `with` expressions to create custom limits:

```csharp
var strict = ParserLimits.Default with
{
    MaxHeaderCount = 50,
    MaxTotalHeaderBytes = 16384
};

UltraHardenedParser.TryExtractFullHeaderValidated(ref buffer, request, in strict, out bytesRead);
```

## Limit Reference

| Limit | Default | Purpose |
|-------|---------|---------|
| `MaxHeaderCount` | 100 | Maximum number of headers per request |
| `MaxHeaderNameLength` | 256 | Maximum length of a single header name |
| `MaxHeaderValueLength` | 8192 | Maximum length of a single header value |
| `MaxUrlLength` | 8192 | Maximum length of the request URL |
| `MaxQueryParameterCount` | 128 | Maximum number of query string parameters |
| `MaxMethodLength` | 16 | Maximum length of the HTTP method |
| `MaxTotalHeaderBytes` | 1048576 | Maximum total bytes for the entire header section (1 MiB) |

## Examples

### Strict API Gateway

```csharp
var apiGateway = ParserLimits.Default with
{
    MaxHeaderCount = 30,
    MaxUrlLength = 2048,
    MaxTotalHeaderBytes = 8192,
    MaxQueryParameterCount = 20
};
```

### Permissive Proxy

```csharp
var proxy = ParserLimits.Default with
{
    MaxHeaderCount = 200,
    MaxHeaderValueLength = 16384,
    MaxTotalHeaderBytes = 65536
};
```

{{< callout type="warning" >}}
Increasing limits above defaults increases exposure to denial-of-service attacks. Only raise limits when you have a specific requirement and understand the memory implications.
{{< /callout >}}
