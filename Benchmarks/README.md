# Benchmarks

Reproduces the benchmark tables on the [docs site](https://dotnet-web-stack.github.io/Glyph11/benchmarks.html):
**Glyph11** on

- **request header parsing** (contiguous and multi-segment), and
- **chunked body decoding**

across `~95 B` / `4 KB` / `32 KB` payloads, best-of-5, ns/op. The payload bytes are built
in-process and are identical to what the site reports.

## Run

```sh
dotnet run -c Release --project Benchmarks
```

Numbers vary run-to-run and by hardware; treat them as relative.
