#!/usr/bin/env python
"""peek-overlay.py -- inspect what RTSS's OverlayEditor actually renders.

RTSS keeps a shared memory block named "RTSSSharedMemoryV2". It has two
independent rendering channels inside it:

  * OSD slots -- plain text, up to 8 of them, any program may claim one.
    Anything written here CANNOT be dragged or edited in OverlayEditor.
  * The overlay layout channel -- written by OverlayEditor.dll, owner string
    "RTSSOverlayEditor". This is the one built from a .ovl file and it is
    fully draggable.

This script reads the second one. After editing a .ovl and restarting RTSS,
run this to confirm your new layers really made it into the rendered output.
The layer markup looks like <P=x,y><Ln><Cn>text<C> -- one block per layer.

Usage:
    python tools\\peek-overlay.py             # summary + layer tag list
    python tools\\peek-overlay.py --full      # dump the whole rendered string
    python tools\\peek-overlay.py --slots     # list all OSD slots instead
"""
import argparse
import ctypes
import ctypes.wintypes as wt
import re

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.OpenFileMappingW.restype = wt.HANDLE
k32.OpenFileMappingW.argtypes = [wt.DWORD, wt.BOOL, wt.LPCWSTR]
k32.MapViewOfFile.restype = ctypes.c_void_p
k32.MapViewOfFile.argtypes = [wt.HANDLE, wt.DWORD, wt.DWORD, wt.DWORD, ctypes.c_size_t]
k32.UnmapViewOfFile.argtypes = [ctypes.c_void_p]
k32.CloseHandle.argtypes = [wt.HANDLE]

FILE_MAP_READ = 0x0004
MAPPING = "RTSSSharedMemoryV2"
MAP_SIZE = 4 * 1024 * 1024
OVERLAY_OWNER = "RTSSOverlayEditor"

# Offsets into RTSS_SHARED_MEMORY (see RTSS SDK Include/RTSSSharedMemory.h).
OFF_APP_ARR_OFFSET = 12
OFF_APP_ARR_SIZE = 16
OFF_OSD_ENTRY_SIZE = 20
OFF_OSD_ARR_OFFSET = 24
OFF_OSD_ARR_SIZE = 28

# Offsets into RTSS_SHARED_MEMORY_OSD_ENTRY.
ENTRY_OWNER = 256
ENTRY_EX2 = 266752
EX2_SIZE = 32768


def read_block():
    h = k32.OpenFileMappingW(FILE_MAP_READ, False, MAPPING)
    if not h:
        raise SystemExit(f"{MAPPING} not found -- is RTSS running?")
    p = k32.MapViewOfFile(h, FILE_MAP_READ, 0, 0, MAP_SIZE)
    if not p:
        k32.CloseHandle(h)
        raise SystemExit("MapViewOfFile failed")
    try:
        return ctypes.string_at(p, MAP_SIZE)
    finally:
        k32.UnmapViewOfFile(p)
        k32.CloseHandle(h)


def cstr(buf, off, size):
    raw = bytes(buf[off:off + size])
    end = raw.find(b"\x00")
    if end >= 0:
        raw = raw[:end]
    return raw.decode("utf-8", "replace")


def u32(buf, off):
    return int.from_bytes(buf[off:off + 4], "little")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--full", action="store_true", help="dump the whole rendered overlay string")
    ap.add_argument("--slots", action="store_true", help="list OSD slots instead of the overlay channel")
    args = ap.parse_args()

    buf = read_block()
    entry = u32(buf, OFF_OSD_ENTRY_SIZE)
    arr = u32(buf, OFF_OSD_ARR_OFFSET)
    count = u32(buf, OFF_OSD_ARR_SIZE)

    if args.slots:
        print(f"OSD slots: {count}, entry size {entry}")
        for i in range(count):
            base = arr + i * entry
            owner = cstr(buf, base + ENTRY_OWNER, 256)
            text = cstr(buf, base + ENTRY_EX2, EX2_SIZE)
            print(f"  slot {i}: owner={owner!r} ex2={len(text)} bytes")
            if text:
                print("    " + text.replace("\n", "\\n")[:200])
        return

    found = False
    for i in range(count):
        base = arr + i * entry
        if cstr(buf, base + ENTRY_OWNER, 256) != OVERLAY_OWNER:
            continue
        found = True
        text = cstr(buf, base + ENTRY_EX2, EX2_SIZE)
        layers = re.findall(r"<L(\d+)>", text)
        print(f"slot {i}: OverlayEditor output, {len(text)} bytes")
        print(f"layer tags ({len(layers)}): {', '.join(layers)}")
        if args.full:
            print("--- BEGIN ---")
            print(text)
            print("--- END ---")
        else:
            print("tail: " + repr(text[-300:]))
        break

    if not found:
        raise SystemExit(f"no slot owned by {OVERLAY_OWNER} -- is the OverlayEditor plugin enabled?")


if __name__ == "__main__":
    main()
