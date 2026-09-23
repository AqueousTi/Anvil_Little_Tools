#!/usr/bin/env bash
# Local test helper (dev-only): crop a screenshot to a suite window chosen by
# PID and title substring.
#
#   shotwin.sh <pid> <title substring> <out.png> [full]
set -uo pipefail
WORKSPACE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
export DISPLAY="${DISPLAY:-:1}"
pid="$1"; needle="$2"; out="$3"; full="${4:-}"
raw="$WORKSPACE_ROOT/.tools/out/.shotwin-raw.png"
rm -f "$raw"
gnome-screenshot -f "$raw" >/dev/null 2>&1
line=""
while read -r l; do
  id="$(echo "$l" | awk '{print $1}')"
  p="$(xprop -id "$id" _NET_WM_PID 2>/dev/null | grep -oE '[0-9]+$')"
  [[ "$p" == "$pid" ]] || continue
  [[ "$l" == *"$needle"* ]] || continue
  line="$l"; break
done < <(xwininfo -root -tree 2>/dev/null | grep -F '"Little Tools')
if [[ -z "$line" ]]; then echo "no window for pid=$pid matching $needle"; exit 1; fi
if [[ "$full" == "full" ]]; then cp "$raw" "$out"; echo "full desktop -> $out"; exit 0; fi
python3 - "$raw" "$out" "$line" <<'PY'
import re, sys
from PIL import Image
raw, out, line = sys.argv[1], sys.argv[2], sys.argv[3]
m = re.search(r'\s(\d+)x(\d+)\+(-?\d+)\+(-?\d+)\s+\+(-?\d+)\+(-?\d+)', line)
if not m:
    print("no geometry:", line); sys.exit(1)
w, h, x, y = (int(m.group(i)) for i in range(1, 5))
im = Image.open(raw).convert("RGB")
crop = im.crop((max(0, x), max(0, y), max(0, x) + w, max(0, y) + h))
crop.save(out)
print(f"captured {w}x{h}+{x}+{y} -> {out}")
PY
