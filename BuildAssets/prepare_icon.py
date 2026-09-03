from __future__ import annotations

import base64
from io import BytesIO
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
BUILD_ASSETS = ROOT / "BuildAssets"
MASTER = ROOT / "Resources" / "AppIcon" / "eavdrop-approved.png"
ICO = ROOT / "appicon.ico"

# The approved glossy blue-green EAVdrop artwork is stored losslessly as
# ordered base64 chunks so the GitHub connector never has to reinterpret it.
chunks = sorted(BUILD_ASSETS.glob("eavdrop-approved.png.b64.*"))
if not chunks:
    raise RuntimeError("Approved EAVdrop icon data is missing.")

encoded = "".join(p.read_text(encoding="ascii").strip() for p in chunks)
raw = base64.b64decode(encoded)
MASTER.parent.mkdir(parents=True, exist_ok=True)
MASTER.write_bytes(raw)

with Image.open(BytesIO(raw)) as src:
    src = src.convert("RGBA")

    # Windows ICO, all derived from the exact same artwork.
    src.save(
        ICO,
        format="ICO",
        sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
    )

    # Android legacy transparent launcher PNGs. This deliberately bypasses
    # adaptive foreground/background recomposition so the launcher sees the
    # approved artwork as one image.
    densities = {
        "mipmap-mdpi": 48,
        "mipmap-hdpi": 72,
        "mipmap-xhdpi": 96,
        "mipmap-xxhdpi": 144,
        "mipmap-xxxhdpi": 192,
    }
    for folder, size in densities.items():
        dst_dir = ROOT / "Platforms" / "Android" / "Resources" / folder
        dst_dir.mkdir(parents=True, exist_ok=True)
        src.resize((size, size), Image.Resampling.LANCZOS).save(
            dst_dir / "eavdrop_icon.png", optimize=True
        )

print("Prepared exact approved EAVdrop icon assets.")
