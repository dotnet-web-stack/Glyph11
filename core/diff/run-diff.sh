#!/usr/bin/env bash
# Build the release shared library and run the C-vs-C# differential harness.
#   ./run-diff.sh [iterations]   (default 1,000,000)
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
core="$(dirname "$here")"

cmake -S "$core" -B "$core/build-rel" \
    -DGLYPH11_SANITIZE=OFF -DGLYPH11_BUILD_TESTS=OFF -DCMAKE_BUILD_TYPE=Release >/dev/null
cmake --build "$core/build-rel" >/dev/null

LD_LIBRARY_PATH="$core/build-rel" \
    dotnet run -c Release --project "$here" -- "${1:-1000000}"
