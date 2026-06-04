#!/usr/bin/env python3
"""Generate the shared benchmark payloads so every language benches identical bytes.
Writes small.bin / h4k.bin / h32k.bin to the given directory (default: cwd)."""
import os
import sys


def build_header(target: int) -> bytes:
    s = "GET /route?p1=1&p2=2&p3=3&p4=4 HTTP/1.1\r\nHost: localhost\r\n"
    i = 0
    while len(s) < target - 4:
        name = f"X-Header-{i}"
        i += 1
        remaining = target - len(s) - len(name) - 4
        vlen = min(max(remaining, 1), 200)
        s += f"{name}: " + ("A" * vlen) + "\r\n"
    s += "\r\n"
    return s.encode("ascii")


SMALL = (
    "GET /route?p1=1&p2=2&p3=3&p4=4 HTTP/1.1\r\n"
    "Host: localhost\r\nContent-Length: 100\r\nServer: Glyph11\r\n\r\n"
).encode("ascii")


def main() -> None:
    out = sys.argv[1] if len(sys.argv) > 1 else "."
    os.makedirs(out, exist_ok=True)
    payloads = {"small.bin": SMALL, "h4k.bin": build_header(4096), "h32k.bin": build_header(32768)}
    for name, data in payloads.items():
        with open(os.path.join(out, name), "wb") as f:
            f.write(data)
    print("payloads -> " + out + ": " + ", ".join(f"{n}={len(d)}B" for n, d in payloads.items()))


if __name__ == "__main__":
    main()
