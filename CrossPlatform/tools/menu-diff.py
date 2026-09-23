#!/usr/bin/env python3
"""Local test helper (dev-only): count pixels that differ inside the tray menu
region, to tell an open menu from a closed one.
  menu-diff.py <a.png> <b.png>
"""
import sys
from PIL import Image, ImageChops
a = Image.open(sys.argv[1]).convert("RGB").crop((2100, 36, 2420, 560))
b = Image.open(sys.argv[2]).convert("RGB").crop((2100, 36, 2420, 560))
d = ImageChops.difference(a, b)
changed = sum(1 for p in d.getdata() if sum(p) > 30)
print(f"changed_pixels={changed} of {a.size[0]*a.size[1]}")
