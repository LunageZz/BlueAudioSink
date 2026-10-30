"""Export the Android rectangle vector as a multi-resolution Windows icon.

Run from any directory: python tools/build_app_icon.py
Requires Pillow. Fails on unsupported paths instead of silently changing artwork.
"""
from pathlib import Path
import re
import xml.etree.ElementTree as ET

from PIL import Image, ImageDraw


root = Path(__file__).resolve().parents[1]
vector = ET.parse(root / "android/app/src/main/res/drawable/ic_launcher.xml").getroot()
android = "{http://schemas.android.com/apk/res/android}"
width = float(vector.attrib[android + "viewportWidth"])
height = float(vector.attrib[android + "viewportHeight"])
canvas = Image.new("RGBA", (1080, 1080), (0, 0, 0, 0))
draw = ImageDraw.Draw(canvas)
for path in vector:
    data = path.attrib[android + "pathData"]
    match = re.fullmatch(r"M([\d.]+),([\d.]+)h([\d.]+)v([\d.]+)h-([\d.]+)z", data)
    if not match:
        raise ValueError(f"Unsupported Android path: {data}")
    x, y, w, h, closing_width = map(float, match.groups())
    if w != closing_width:
        raise ValueError("Expected a closed rectangle")
    box = (round(x / width * 1080), round(y / height * 1080),
           round((x + w) / width * 1080) - 1, round((y + h) / height * 1080) - 1)
    draw.rectangle(box, fill=path.attrib[android + "fillColor"])
icon = canvas.resize((256, 256), Image.Resampling.LANCZOS)
target = root / "src/SinkLite/Assets/app.ico"
icon.save(target, sizes=[(n, n) for n in (16, 20, 24, 32, 40, 48, 64, 128, 256)])
print(target)
