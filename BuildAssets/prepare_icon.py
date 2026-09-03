from __future__ import annotations

import base64
from io import BytesIO
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
BUILD_ASSETS = ROOT / "BuildAssets"
SOURCE_B64 = BUILD_ASSETS / "eavdrop-approved.webp.b64"
MASTER = ROOT / "Resources" / "AppIcon" / "eavdrop-approved.png"
ICO = ROOT / "appicon.ico"

if not SOURCE_B64.exists():
    raise RuntimeError("Verified approved EAVdrop icon source is missing.")

raw = base64.b64decode(SOURCE_B64.read_text(encoding="ascii").strip())

with Image.open(BytesIO(raw)) as src:
    src = src.convert("RGBA")

    MASTER.parent.mkdir(parents=True, exist_ok=True)
    src.save(MASTER, format="PNG", optimize=True)

    # Windows multi-size icon from the exact same approved artwork.
    src.save(
        ICO,
        format="ICO",
        sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
    )

    # Android launcher icons. Generate the exact resource names referenced by
    # AndroidManifest.xml so no MAUI-generated SVG approximation is involved.
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
        icon = src.resize((size, size), Image.Resampling.LANCZOS)
        icon.save(dst_dir / "appicon.png", optimize=True)
        icon.save(dst_dir / "appicon_round.png", optimize=True)

print("Prepared verified approved EAVdrop icon assets.")
