#!/usr/bin/env python3
"""
make-ico.py — Assemble multi-size Vista+ PNG-compressed .ico from rm-icon-{16..256}.png
Uses Python stdlib only (struct, pathlib).
"""
import struct
from pathlib import Path

SIZES = [16, 32, 48, 64, 128, 256]
PREFIX = "rm-icon"

def read_png_dims(data: bytes) -> tuple[int, int]:
    if data[:8] != b"\x89PNG\r\n\x1a\n" or data[12:16] != b"IHDR":
        raise ValueError("Invalid PNG header")
    w, h = struct.unpack(">II", data[16:24])
    return w, h

def build_ico(assets_dir: Path) -> Path:
    entries_data = []
    for sz in SIZES:
        png_path = assets_dir / f"{PREFIX}-{sz}.png"
        data = png_path.read_bytes()
        w, h = read_png_dims(data)
        if (w, h) != (sz, sz):
            raise ValueError(f"{png_path.name}: expected {sz}x{sz}, got {w}x{h}")
        entries_data.append((sz, data))

    count = len(entries_data)
    header = struct.pack("<HHH", 0, 1, count)
    offset = 6 + 16 * count
    dir_entries = []
    payloads = []

    for sz, data in entries_data:
        b_width = 0 if sz == 256 else sz
        b_height = 0 if sz == 256 else sz
        entry = struct.pack(
            "<BBBBHHII",
            b_width,
            b_height,
            0,      # palette count
            0,      # reserved
            1,      # color planes
            32,     # bits per pixel
            len(data),
            offset,
        )
        dir_entries.append(entry)
        payloads.append(data)
        offset += len(data)

    ico_path = assets_dir / f"{PREFIX}.ico"
    ico_path.write_bytes(header + b"".join(dir_entries) + b"".join(payloads))
    return ico_path

if __name__ == "__main__":
    here = Path(__file__).resolve().parent
    out = build_ico(here)
    print(f"Created {out.name} ({out.stat().st_size} bytes) with {len(SIZES)} entries: {SIZES}")
