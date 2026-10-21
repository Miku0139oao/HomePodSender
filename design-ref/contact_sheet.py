# 把连拍的帧拼成一张接触印样（文件名里带毫秒时间戳），便于一次看完动画过程
import sys
from pathlib import Path
from PIL import Image, ImageDraw

src = Path(sys.argv[1])
out = Path(sys.argv[2])
cols = int(sys.argv[3]) if len(sys.argv) > 3 else 6
thumb_w = int(sys.argv[4]) if len(sys.argv) > 4 else 290
start = int(sys.argv[5]) if len(sys.argv) > 5 else 0
count = int(sys.argv[6]) if len(sys.argv) > 6 else 999

frames = sorted(p for p in src.glob("f*.png"))[start:start + count]
first = Image.open(frames[0])
th = int(first.height * thumb_w / first.width)
rows = (len(frames) + cols - 1) // cols
sheet = Image.new("RGB", (cols * (thumb_w + 6) + 6, rows * (th + 22) + 6), (20, 20, 24))
draw = ImageDraw.Draw(sheet)
for i, p in enumerate(frames):
    im = Image.open(p).convert("RGB").resize((thumb_w, th), Image.LANCZOS)
    x = 6 + (i % cols) * (thumb_w + 6)
    y = 6 + (i // cols) * (th + 22)
    sheet.paste(im, (x, y + 16))
    draw.text((x, y), p.stem, fill=(230, 230, 230))
sheet.save(out)
print(out, len(frames))
