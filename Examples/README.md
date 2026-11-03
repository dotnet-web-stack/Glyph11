# Examples

A runnable, fully-commented example for the Glyph11 NuGet package. The project mirrors what a
consumer writes (the same `using`s and APIs) — in-repo it uses a `ProjectReference`; a real app
uses `dotnet add package Glyph11`.

| Project | Package |
|---|---|
| [`Glyph11.Example`](Glyph11.Example) | [`Glyph11`](https://www.nuget.org/packages/Glyph11/) |

`Program.cs` walks through **every option**: contiguous parse, `ReadOnlySequence` parse,
custom limits, reading fields/headers/query, chunked decoding, status/error handling, and reuse.

## Run

Pure managed — nothing extra needed:

```sh
dotnet run --project Examples/Glyph11.Example
```

See the [docs site](https://dotnet-web-stack.github.io/Glyph11/) for the full reference.
