#!/usr/bin/env python3
"""Local test helper (dev-only): hash the y-axis label column of the chart.

The labels only change when the drawn price range changes, so the hash identifies
which instrument's series is on screen - 510300 lives around 4.4-5.1 while 600519
is above 1100.
  race-signature.py <screenshot.png> <windowX> <windowY>
"""
import hashlib
import sys
from PIL import Image

im = Image.open(sys.argv[1]).convert("RGB")
x, y = int(sys.argv[2]), int(sys.argv[3])
crop = im.crop((x + 428, y + 290, x + 468, y + 350))
print(hashlib.md5(crop.tobytes()).hexdigest()[:12])
