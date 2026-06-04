#!/usr/bin/env bash
# Run the cross-language parser benchmark (pure C, C# managed + FFI, Kotlin FFI)
# on identical payloads and aggregate into bench/results/{results.md,results.json}.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
out="${1:-$root/bench/results}"
pay="$(mktemp -d)"
csv="$(mktemp)"
trap 'rm -rf "$pay" "$csv"' EXIT

python3 "$root/bench/gen_payloads.py" "$pay" >&2

# --- build the C core (release) ---
cmake -S "$root/core" -B "$root/core/build-rel" \
    -DGLYPH11_SANITIZE=OFF -DGLYPH11_BUILD_TESTS=OFF -DGLYPH11_BUILD_BENCH=ON \
    -DCMAKE_BUILD_TYPE=Release >/dev/null
cmake --build "$root/core/build-rel" >/dev/null
so="$root/core/build-rel/libglyph11.so"

# --- pure C ---
"$root/core/build-rel/glyph11_bench" "$pay" >>"$csv"

# --- C# (managed + FFI) ---
GLYPH11_NATIVE_PATH="$so" dotnet run -c Release --project "$root/bindings/dotnet/Glyph11.Bench" -- csv "$pay" \
    2>/dev/null | grep '^dotnet-' >>"$csv"

# --- Kotlin (FFI) ---
( cd "$root/bindings/kotlin" && GLYPH11_LIB="$so" ./gradlew -q run --args="bench $pay" --console=plain 2>/dev/null ) \
    | grep '^kotlin-' >>"$csv"

echo "" >&2
python3 "$root/bench/aggregate.py" "$out" <"$csv"
echo "wrote $out/results.{md,json}" >&2
