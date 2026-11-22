#!/usr/bin/env python
"""read-aida.py -- print the XML that Zephyr publishes into "AIDA64_SensorValues".

This named shared memory is the channel RTSS's OverlayEditor reads. Any process
can own it; whoever creates it first wins the name. Use this script to confirm
that Zephyr is alive and writing sensible numbers before you go blaming the
overlay layout.

Usage:
    python tools\\read-aida.py            # print once
    python tools\\read-aida.py -n 10      # print 10 times, 1s apart
"""
import argparse
import ctypes
import ctypes.wintypes as wt
import time

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.OpenFileMappingW.restype = wt.HANDLE
k32.OpenFileMappingW.argtypes = [wt.DWORD, wt.BOOL, wt.LPCWSTR]
k32.MapViewOfFile.restype = ctypes.c_void_p
k32.MapViewOfFile.argtypes = [wt.HANDLE, wt.DWORD, wt.DWORD, wt.DWORD, ctypes.c_size_t]
k32.UnmapViewOfFile.argtypes = [ctypes.c_void_p]
k32.CloseHandle.argtypes = [wt.HANDLE]

FILE_MAP_READ = 0x0004
MAPPING = "AIDA64_SensorValues"
SIZE = 65536


def read_mapping(name):
    h = k32.OpenFileMappingW(FILE_MAP_READ, False, name)
    if not h:
        return None
    p = k32.MapViewOfFile(h, FILE_MAP_READ, 0, 0, SIZE)
    if not p:
        k32.CloseHandle(h)
        return None
    try:
        return ctypes.string_at(p, SIZE)
    finally:
        k32.UnmapViewOfFile(p)
        k32.CloseHandle(h)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("-n", "--count", type=int, default=1, help="how many samples to print")
    ap.add_argument("-i", "--interval", type=float, default=1.0, help="seconds between samples")
    args = ap.parse_args()

    for i in range(args.count):
        raw = read_mapping(MAPPING)
        stamp = time.strftime("%H:%M:%S")

        if raw is None:
            print(f"[{stamp}] {MAPPING}: NOT FOUND (Zephyr is not running?)")
        else:
            end = raw.find(b"\x00")
            if end < 0:
                end = len(raw)
            text = raw[:end].decode("utf-8", "replace")
            if not text:
                print(f"[{stamp}] mapping exists but is empty")
            else:
                print(f"[{stamp}] {len(text)} bytes")
                print(text)

        if i + 1 < args.count:
            time.sleep(args.interval)


if __name__ == "__main__":
    main()
