# Kotlin / JVM binding (Panama FFM)

Calls the Glyph11 C core (`libglyph11`) from Kotlin via the Foreign Function &
Memory API — no JNI glue, pure managed downcalls.

```kotlin
val r = Glyph11.parse(requestBytes)
when {
    r.isOk -> {
        val method = String(requestBytes, r.method.offset, r.method.length)
        // also: r.path, r.version, r.headerCount, r.queryCount, r.consumed
    }
    r.isIncomplete -> { /* read more bytes */ }
    else -> { val httpStatus = Glyph11.httpCode(r.status) /* 400 / 431 */ }
}
```

## Build & run

FFM is a preview feature on JDK 21 (finalized in JDK 22), so the build passes
`--enable-preview`. Point at the native library with `GLYPH11_LIB`:

```sh
# build the .so first (from the repo root)
cmake -S core -B core/build-rel -DGLYPH11_SANITIZE=OFF -DGLYPH11_BUILD_TESTS=OFF
cmake --build core/build-rel

cd bindings/kotlin
GLYPH11_LIB="$PWD/../../core/build-rel/libglyph11.so" gradle run
```

`Main.kt` is a smoke test that parses valid and malformed requests and checks the
status, parsed spans, and HTTP codes against the native core.

## Notes

- Requires JDK 21+ (FFM). On JDK 22+ you can drop `--enable-preview`.
- The library is resolved from `-Dglyph11.lib` (the `run` task sets it from the
  `GLYPH11_LIB` env var) or the default OS library search path.
- The C `glyph11_request` struct layout is mirrored by byte offset in
  `Glyph11.kt` (LP64); the binding allocates the header/query storage off-heap
  via an `Arena` per call.
