#!/usr/bin/env python
"""add-zephyr-layers.py -- make Zephyr's fan RPM a draggable RTSS overlay layer.

WHY THIS EXISTS
    RTSS has two rendering channels. OSD slots are plain text and cannot be
    dragged. The overlay layout channel is built from a .ovl file and is fully
    editable. To get fan RPM into that second channel you must add, inside a
    .ovl file:

      * data source blocks with Provider=AIDA whose ID matches what Zephyr
        publishes into the "AIDA64_SensorValues" shared memory, and
      * layer blocks whose Text references those sources as %SourceName%.

    Layer text uses %Name%, matching the *Name* field of the source block, not
    its ID. See OverlayMacroList.cpp in the RTSS SDK for the exact rule.

WHAT IT TOUCHES
    Only blocks carrying our own marker (source ID or layer name). Everything
    else is left byte-for-byte identical: this script reads and writes latin-1,
    a 1:1 byte mapping, so GBK-encoded Chinese layer names in OEM profiles
    survive a round trip unchanged. It also keeps the file's original line
    endings instead of normalising them.

INDICES MATTER
    RTSS loads sources and layers by looping n = 0 .. count-1 and looking up
    [Source{n}] / [Layer{n}]. A gap makes that slot silently become an empty
    entry. The General counts written here are therefore always derived from
    the headers actually present, and every block is renumbered contiguously.

IDEMPOTENT
    Running it twice is safe: existing blocks are found by ID/Name and updated
    in place rather than appended again.

USAGE
    python tools\\add-zephyr-layers.py <file.ovl>              # add or refresh
    python tools\\add-zephyr-layers.py <file.ovl> --dry-run    # report only
    python tools\\add-zephyr-layers.py <file.ovl> --revert     # remove our blocks

    After a successful run, restart RTSS so it reloads the layout:
    powershell -ExecutionPolicy Bypass -File tools\\restart-rtss.ps1
"""
from __future__ import annotations

import argparse
import datetime as _dt
import os
import re
import sys

# (source name, source id, units). The name is what layers reference via %Name%.
SOURCES = [
    ("ZEPHYR Fan1 RPM", "ZEPHYR_FAN1", "RPM"),
    ("ZEPHYR Fan2 RPM", "ZEPHYR_FAN2", "RPM"),
    ("ZEPHYR CPU Temp", "ZEPHYR_CPUT", "°C"),
]

# (layer name, layer text, position x, position y, text colour)
LAYERS = [
    ("CPU Fan RPM", "Fan1 %ZEPHYR Fan1 RPM% RPM", 0, -4, "8052EB99"),
    ("GPU Fan RPM", "Fan2 %ZEPHYR Fan2 RPM% RPM", -16, -4, "80B852EB"),
]

# About PositionX: RTSS does not read <P=x,y> as an absolute offset from the
# window origin. x is an advance along the current text flow, and a negative
# value moves to the RIGHT. Empirically -120 pushed the second layer outside the
# rendered area, so it never appeared at all; -16 keeps it on the same row, just
# right of the first one. y is a row offset with the same sign convention: more
# negative = further down. Keep both small and verify on screen after a restart.

OUR_SOURCE_IDS = {sid for _, sid, _ in SOURCES}
OUR_LAYER_NAMES = {name for name, *_ in LAYERS}

# Blocks written by this script before the WujieFan -> Zephyr rename.
#
# They must be removed, not ignored: a stale Source block with ID="WUJIE_FAN1"
# would sit in the layout forever with no layer referencing it, and RTSS keeps
# polling it every frame. The visible symptom for the user is different though --
# their old layers render "%WUJIE Fan1 RPM%" against a source that no longer
# exists, so the numbers silently disappear.
#
# The layer blocks are NOT listed by name: their names ("CPU Fan RPM") did not
# change in the rename, so the normal update pass rewrites their Text in place
# and the user's dragged position survives.
LEGACY_SOURCE_IDS = {"WUJIE_FAN1", "WUJIE_FAN2", "WUJIE_CPUT"}

HEADER_RE = re.compile(r"^\[([A-Za-z]+)(\d*)\]\s*$")


def strip_eol(line: str) -> str:
    return line.rstrip("\r\n")


def pair(line: str) -> str:
    """Return the trailing line ending of a line, or ''."""
    return line[len(strip_eol(line)):]


class Block:
    """One [Header] plus its body lines (each keeping its own line ending)."""

    __slots__ = ("kind", "index", "header_line", "body")

    def __init__(self, kind: str, index: int | None, header_line: str, body: list[str]):
        self.kind = kind
        self.index = index
        self.header_line = header_line
        self.body = body

    def get(self, key: str, default: str = "") -> str:
        prefix = key + "="
        for line in self.body:
            if line.startswith(prefix):
                return strip_eol(line[len(prefix):])
        return default

    def is_ours(self) -> bool:
        if self.kind == "Source":
            return (self.get("ID") in OUR_SOURCE_IDS
                    or self.get("ID") in LEGACY_SOURCE_IDS)
        if self.kind == "Layer":
            return self.get("Name") in OUR_LAYER_NAMES
        return False


class Layout:
    def __init__(self, text: str):
        self.eol = "\r\n" if "\r\n" in text else "\n"
        self.preamble: list[str] = []
        self.blocks: list[Block] = []
        current: Block | None = None

        for line in text.splitlines(keepends=True):
            m = HEADER_RE.match(strip_eol(line))
            if m:
                current = Block(m.group(1), int(m.group(2)) if m.group(2) else None, line, [])
                self.blocks.append(current)
            elif current is None:
                self.preamble.append(line)
            else:
                current.body.append(line)

    def of_kind(self, kind: str) -> list[Block]:
        return [b for b in self.blocks if b.kind == kind]

    def section(self, kind: str) -> Block | None:
        for b in self.blocks:
            if b.kind == kind and b.index is None:
                return b
        return None

    def reindex(self, kind: str) -> None:
        n = 0
        for b in self.blocks:
            if b.kind == kind:
                b.index = n
                n += 1

    def next_index(self, kind: str) -> int:
        return max((b.index for b in self.of_kind(kind) if b.index is not None), default=-1) + 1

    def append(self, block: Block) -> None:
        self.blocks.append(block)

    def insert_after_last(self, kind: str, block: Block) -> None:
        last = -1
        for i, b in enumerate(self.blocks):
            if b.kind == kind:
                last = i
        self.blocks.insert(last + 1, block)

    def render(self) -> str:
        out: list[str] = list(self.preamble)
        for b in self.blocks:
            if b.index is None:
                out.append(b.header_line)
            else:
                eol = pair(b.header_line) or self.eol
                out.append(f"[{b.kind}{b.index}]" + eol)
            out.extend(b.body)
        return "".join(out)

    def new_body(self, pairs: list[tuple[str, str]]) -> list[str]:
        return [f"{k}={v}{self.eol}" for k, v in pairs]

    def foreign_lines(self) -> list[str]:
        """Every line that does not belong to one of our blocks, in order.

        Used as the safety net in _finish: the rewrite must not disturb a single
        line of the user's existing layout. Header lines are normalised in
        render(), so this works on the rendered text rather than the original.
        """
        out: list[str] = list(self.preamble)
        for b in self.blocks:
            if b.is_ours():
                continue
            out.append(b.header_line if b.index is None else f"[{b.kind}{b.index}]" + (pair(b.header_line) or self.eol))
            out.extend(b.body)
        return out


def set_key(block: Block, key: str, value: str, eol: str) -> None:
    prefix = key + "="
    for i, line in enumerate(block.body):
        if line.startswith(prefix):
            block.body[i] = f"{key}={value}" + (pair(line) or eol)
            return
    block.body.append(f"{key}={value}{eol}")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("ovl")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--revert", action="store_true")
    args = ap.parse_args()

    if not os.path.isfile(args.ovl):
        raise SystemExit(f"not a file: {args.ovl}")

    with open(args.ovl, "rb") as f:
        original_bytes = f.read()
    lay = Layout(original_bytes.decode("latin-1"))

    general = lay.section("General")
    if general is None:
        raise SystemExit("no [General] section -- is this really an .ovl layout?")
    if not lay.of_kind("Layer"):
        raise SystemExit("no [LayerN] block found -- is this really an .ovl layout?")

    ours = [b for b in lay.blocks if b.is_ours()]
    print(f"before: {len(lay.of_kind('Source'))} sources, "
          f"{len(lay.of_kind('Layer'))} layers, {len(ours)} Zephyr block(s)")

    if args.revert:
        if not ours:
            print("nothing to revert")
            return 0
        lay.blocks = [b for b in lay.blocks if not b.is_ours()]
        lay.reindex("Source")
        lay.reindex("Layer")
        n_src = len(lay.of_kind("Source"))
        n_lay = len(lay.of_kind("Layer"))
        set_key(general, "Sources", str(n_src), lay.eol)
        set_key(general, "Layers", str(n_lay), lay.eol)
        return _finish(lay, general, args, original_bytes,
                       f"removed {len(ours)} block(s); "
                       f"after: {n_src} sources, {n_lay} layers")

    # ---- legacy clean-up ---------------------------------------------------
    # Drop Source blocks left over from the pre-rename script. Doing it here (and
    # not only in --revert) is what makes a plain re-run sufficient for someone
    # upgrading: they run the same command as before and the stale blocks go away.
    legacy = [b for b in lay.of_kind("Source") if b.get("ID") in LEGACY_SOURCE_IDS]
    if legacy:
        print(f"removing {len(legacy)} legacy WUJIE_* source block(s) from the pre-rename version")
        stale = {b.get("ID") for b in legacy}
        lay.blocks = [b for b in lay.blocks if not (b.kind == "Source" and b.get("ID") in stale)]

    # ---- sources -----------------------------------------------------------
    by_id = {b.get("ID"): b for b in lay.of_kind("Source") if b.get("ID") in OUR_SOURCE_IDS}
    for name, sid, units in SOURCES:
        block = by_id.get(sid)
        if block is None:
            block = Block("Source", lay.next_index("Source"), "", [])
            block.body = lay.new_body([
                ("Name", name), ("Units", units), ("Format", ""),
                ("Formula", ""), ("Provider", "AIDA"), ("ID", sid),
            ])
            lay.insert_after_last("Source", block)
        else:
            set_key(block, "Name", name, lay.eol)
            set_key(block, "Units", units, lay.eol)
            set_key(block, "Format", "", lay.eol)
            set_key(block, "Formula", "", lay.eol)
            set_key(block, "Provider", "AIDA", lay.eol)
            set_key(block, "ID", sid, lay.eol)

    # ---- layers ------------------------------------------------------------
    by_name = {b.get("Name"): b for b in lay.of_kind("Layer") if b.get("Name") in OUR_LAYER_NAMES}
    for name, text, px, py, colour in LAYERS:
        block = by_name.get(name)
        if block is None:
            block = Block("Layer", lay.next_index("Layer"), "", [])
            block.body = lay.new_body([
                ("Name", name), ("Text", text),
                ("PositionX", str(px)), ("PositionY", str(py)),
                ("ExtentX", "0"), ("ExtentY", "0"), ("ExtentOrigin", "0"),
                ("FixedAlignment", "1"), ("TextColor", colour),
            ])
            lay.append(block)
        else:
            # Refresh what identifies the layer, but leave placement alone.
            # The user may have dragged the layer somewhere in the RTSS editor;
            # writing our default coordinates back over theirs silently undoes
            # that. Text is refreshed because it has to track the source name.
            set_key(block, "Name", name, lay.eol)
            set_key(block, "Text", text, lay.eol)

    lay.reindex("Source")
    lay.reindex("Layer")

    n_src = len(lay.of_kind("Source"))
    n_lay = len(lay.of_kind("Layer"))
    set_key(general, "Sources", str(n_src), lay.eol)
    set_key(general, "Layers", str(n_lay), lay.eol)

    return _finish(lay, general, args, original_bytes,
                   f"after: {n_src} sources, {n_lay} layers")


# General's counters are derived from the blocks actually present, so this
# script legitimately rewrites them. They must not count as "user content"
# when the self-check compares before and after.
_MANAGED_KEYS = ("Sources=", "Layers=")


def _short(line: str, limit: int = 60) -> str:
    """Compact a diff line for the error message; some overlay texts are kilobytes."""
    s = line.rstrip()
    return repr(s if len(s) <= limit else s[:limit] + "...")


def _strip_managed_counts(lines: list[str]) -> list[str]:
    return [ln for ln in lines if not ln.startswith(_MANAGED_KEYS)]


def _check_foreign_untouched(original_bytes: bytes, lay: Layout) -> str | None:
    """Compare every non-ours line before and after. Returns an error or None.

    This is the whole safety net for editing someone's live layout: our blocks
    are inserted and rewritten, but not one line of the user's own sources and
    layers may move, change or disappear. Renumbering is expected to be a no-op
    on a healthy file, so a mismatch here means we were about to damage it.
    """
    before = _strip_managed_counts(Layout(original_bytes.decode("latin-1")).foreign_lines())
    after = _strip_managed_counts(lay.foreign_lines())

    if before == after:
        return None

    for i, (b, a) in enumerate(zip(before, after)):
        if b != a:
            return (f"self-check failed: line {i + 1} of the user's own content "
                    f"changed ({_short(b)} -> {_short(a)}). Nothing was written.")

    return (f"self-check failed: {len(after)} lines after the rewrite vs "
            f"{len(before)} before. Nothing was written.")


def _finish(lay: Layout, general: Block, args, original_bytes: bytes, summary: str) -> int:
    out = lay.render().encode("latin-1")
    print(summary)
    print(f"size: {len(original_bytes)} -> {len(out)} bytes")

    problem = _check_foreign_untouched(original_bytes, lay)
    if problem is not None:
        print(problem)
        return 1

    if args.dry_run:
        print("dry-run: nothing written")
        return 0

    stamp = _dt.datetime.now().strftime("%Y%m%d-%H%M%S")
    bak = f"{args.ovl}.bak-zephyr-{stamp}"
    if not os.path.exists(bak):
        with open(bak, "wb") as f:
            f.write(original_bytes)
        print(f"backup: {bak}")

    tmp = args.ovl + ".tmp"
    with open(tmp, "wb") as f:
        f.write(out)
    os.replace(tmp, args.ovl)
    print(f"written: {args.ovl}")
    print()
    print("Next: restart RTSS so it reloads the layout.")
    print("      powershell -ExecutionPolicy Bypass -File tools\\restart-rtss.ps1")
    return 0


if __name__ == "__main__":
    sys.exit(main())
