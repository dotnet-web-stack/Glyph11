#!/usr/bin/env python3
"""Aggregate cross-language bench CSV (`lang,payload,ns` on stdin) into
results.md + results.json in the output dir (argv[1], default cwd)."""
import datetime
import json
import os
import sys

LANGS = [
    ("dotnet-managed-rom", "C# Ultra (ROM)"),
    ("dotnet-managed-multiseg", "C# Ultra (multi-seg)"),
    ("pure-c", "Pure C"),
    ("dotnet-ffi", "C# binding (FFI)"),
    ("kotlin-ffi", "Kotlin binding (FFI)"),
]
PAYLOADS = [("small", "~95 B"), ("4k", "4 KB"), ("32k", "32 KB")]


def main() -> None:
    out = sys.argv[1] if len(sys.argv) > 1 else "."
    os.makedirs(out, exist_ok=True)

    data: dict[str, dict[str, float]] = {}
    for line in sys.stdin:
        parts = line.strip().split(",")
        if len(parts) != 3:
            continue
        lang, payload, ns = parts
        try:
            data.setdefault(payload, {})[lang] = float(ns)
        except ValueError:
            continue

    header = "| Payload | " + " | ".join(label for _, label in LANGS) + " |"
    md = [header, "|" + "---|" * (len(LANGS) + 1)]
    rows_json = []
    for pkey, plabel in PAYLOADS:
        row = data.get(pkey, {})
        cells = [(f"{row[k]:.0f} ns" if k in row else "—") for k, _ in LANGS]
        md.append(f"| {plabel} | " + " | ".join(cells) + " |")
        rows_json.append({"payload": pkey, "label": plabel, **{k: row.get(k) for k, _ in LANGS}})

    md_text = "\n".join(md) + "\n"
    with open(os.path.join(out, "results.md"), "w") as f:
        f.write(md_text)
    generated = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M UTC")
    with open(os.path.join(out, "results.json"), "w") as f:
        json.dump({
            "unit": "ns/op",
            "generated": generated,
            "langs": [{"key": k, "label": v} for k, v in LANGS],
            "rows": rows_json,
        }, f, indent=2)
    sys.stdout.write(md_text)


if __name__ == "__main__":
    main()
