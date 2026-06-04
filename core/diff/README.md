# Differential harness — C core vs C# reference

Runs every input through **both** parsers in one process and asserts identical
results:

- the C core `glyph11_parse_request` (via P/Invoke into `libglyph11.so`), and
- the C# reference `UltraHardenedParser`.

It compares the outcome (parsed / incomplete / error), the HTTP status (400/431)
on rejection, and on success the **method / path / version** bytes, every
**header** and **query** pair, and the **consumed** byte count.

Inputs are a set of curated vectors plus a deterministic, seeded fuzzer that
mutates and truncates them — so any failure is reproducible.

## Run

```sh
./core/diff/run-diff.sh           # 1,000,000 fuzz iterations (default)
./core/diff/run-diff.sh 50000     # fewer
```

Exit code is non-zero if any divergence is found; up to 25 are printed with the
offending input.

## Notes

- The C ABI returns the **clean** header byte count in `consumed`; the C#
  `bytesReadCount` is `total - 1`. The harness adds 1 to the C# value before
  comparing — the only intentional difference between the two.
- `target` (the full request-target) is a C-only convenience field and is not
  compared (the C# `BinaryRequest` exposes only the query-stripped `Path`).
- Build the C side with `-DGLYPH11_SANITIZE=ON` for the standalone C tests
  (`ctest`); the differential harness uses a plain Release `.so` so the CLR can
  load it without preloading the ASan runtime.
