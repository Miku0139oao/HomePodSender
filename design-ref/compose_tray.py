# 把 --dump-tray 导出的原始 RGBA 帧拼到深色 / 浅色任务栏底色上，放大 4 倍便于检查
import sys
from pathlib import Path
from PIL import Image

src = Path(sys.argv[1] if len(sys.argv) > 1 else "design-ref/tray")
out = Path(sys.argv[2] if len(sys.argv) > 2 else "design-ref")
cases = ["idle", "busy", "play-a", "play-b", "play-c"]
sizes = [16, 24, 32, 64]
ZOOM = 4

for name, bg in (("tray-dark.png", (32, 32, 36)), ("tray-light.png", (238, 240, 244))):
    cell = 64 * ZOOM + 24
    sheet = Image.new("RGB", (cell * len(cases), cell * len(sizes)), bg)
    for j, size in enumerate(sizes):
        for i, case in enumerate(cases):
            raw = (src / f"{case}-{size}x{size}.rgba").read_bytes()
            im = Image.frombytes("RGBA", (size, size), raw)
            big = im.resize((size * ZOOM, size * ZOOM), Image.NEAREST)
            x = i * cell + (cell - size * ZOOM) // 2
            y = j * cell + (cell - size * ZOOM) // 2
            sheet.paste(big, (x, y), big)
    sheet.save(out / name)
    print("写出", out / name)
