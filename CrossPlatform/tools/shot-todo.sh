#!/usr/bin/env bash
# Local test helper (dev-only): crop a screenshot of the todo window.
# usage: shot.sh <output.png>
set -uo pipefail
WORKSPACE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
export DISPLAY="${DISPLAY:-:1}"
out="$1"
mkdir -p "$WORKSPACE_ROOT/.tools/out"
raw="$WORKSPACE_ROOT/.tools/out/.shot-raw.png"
rm -f "$raw"
gnome-screenshot -f "$raw" >/dev/null 2>&1
line="$(xwininfo -root -tree 2>/dev/null | grep -F 'Little Tools · Daily Todo' | grep -F 'dotnet' | head -1)"
python3 - "$raw" "$out" "$line" <<'PY'
import re, sys
from PIL import Image
raw, out, line = sys.argv[1], sys.argv[2], sys.argv[3]
m = re.search(r'\s(\d+)x(\d+)\+(-?\d+)\+(-?\d+)\s+\+(-?\d+)\+(-?\d+)', line)
if not m:
    print("no window geometry:", line); sys.exit(1)
w, h, x, y = (int(m.group(i)) for i in range(1, 5))
im = Image.open(raw).convert("RGB")
crop = im.crop((max(0, x), max(0, y), x + w, y + h))
crop.save(out)
print(f"captured {w}x{h}+{x}+{y} -> {out}")
PY
