# 参考图与应用截图并排对比（截图是透明 PNG，先铺到深色桌面底上），输出到指定文件
import sys
from pathlib import Path
from PIL import Image

ref = Image.open(sys.argv[1]).convert("RGB")
shots = [Image.open(p).convert("RGBA") for p in sys.argv[2:-1]]
out = Path(sys.argv[-1])
H = 760

def fit(im):
    return im.resize((int(im.width * H / im.height), H), Image.LANCZOS)

tiles = [fit(ref)]
for s in shots:
    bg = Image.new("RGBA", s.size, (24, 27, 34, 255))
    bg.alpha_composite(s)
    tiles.append(fit(bg.convert("RGB")))
W = sum(t.width for t in tiles) + 16 * (len(tiles) + 1)
sheet = Image.new("RGB", (W, H + 32), (16, 16, 20))
x = 16
for t in tiles:
    sheet.paste(t, (x, 16))
    x += t.width + 16
sheet.save(out)
print(out, sheet.size)
