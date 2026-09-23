#!/usr/bin/env python3
"""Local test helper (dev-only): count the chart's candle/line colours in a
window screenshot, to tell a drawn chart from an empty one.
  chart-pixels.py <png> <x> <y> <w> <h> [label]
"""
import sys
from PIL import Image
UP, DOWN, LINE = (239, 88, 91), (156, 161, 171), (238, 92, 94)
raw, x, y, w, h = sys.argv[1], *map(int, sys.argv[2:6])
label = sys.argv[6] if len(sys.argv) > 6 else raw
im = Image.open(raw).convert("RGB").crop((x, y, x + w, y + h))
up = down = line = 0
for r, g, b in im.getdata():
    if abs(r-UP[0]) <= 2 and abs(g-UP[1]) <= 2 and abs(b-UP[2]) <= 2: up += 1
    elif abs(r-DOWN[0]) <= 2 and abs(g-DOWN[1]) <= 2 and abs(b-DOWN[2]) <= 2: down += 1
    elif abs(r-LINE[0]) <= 2 and abs(g-LINE[1]) <= 2 and abs(b-LINE[2]) <= 2: line += 1
print(f"{label}: up={up} down={down} line={line} total={up+down+line} "
      f"({'chart drawn' if up+down+line > 500 else 'EMPTY'})")
