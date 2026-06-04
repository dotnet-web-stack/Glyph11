---
title: Getting Started
weight: 1
---

## Installation

```bash
dotnet add package Glyph11
```

Targets .NET 10.0. No external dependencies.

## Quick Start

{{< steps >}}

### Add the NuGet package

```bash
dotnet add package Glyph11
```

### Parse a request

```csharp
using System.Buffers;
using Glyph11.Protocol;
using Glyph11.Parser;
using Glyph11.Parser.UltraHardened;

ReadOnlySequence<byte> buffer = ...; // from PipeReader, Socket, etc.

var request = new BinaryRequest();
var limits = ParserLimits.Default;

if (UltraHardenedParser.TryExtractFullHeaderValidated(ref buffer, request, in limits, out int bytesRead))
{
    // request.Method, request.Path, request.Headers, request.QueryParameters
    // are all populated as ReadOnlyMemory<byte> slices into the original buffer.
    // Advance your reader by bytesRead.
}
```

### Dispose when done

```csharp
// When done, dispose to return pooled arrays:
request.Dispose();
```

{{< /steps >}}

{{< callout type="warning" >}}
Since parsed fields reference the input buffer, the buffer must remain valid for as long as you access the request data. If you need the data to outlive the buffer, copy it (e.g. `request.Method.ToArray()`).
{{< /callout >}}

## Next Steps

- [PipeReader integration example](integration) for a complete server loop
- [Architecture overview](../architecture) to understand the parsing paths
- [UltraHardenedParser](../parsers/ultra-hardened-parser) for validation rules and limits
