# 把 HomePodSender.exe --dump-app-icons 导出的光团帧转成 Tauri 需要的图标文件：
#   icons/32x32.png、128x128.png、128x128@2x.png、icon.png（512）、icon.ico（16~256 多尺寸，每个尺寸单独渲染）
import sys
from pathlib import Path
from PIL import Image

src = Path(sys.argv[1])
dst = Path(sys.argv[2])
dst.mkdir(parents=True, exist_ok=True)


def load(size):
    raw = (src / f"app-{size}x{size}.rgba").read_bytes()
    return Image.frombytes("RGBA", (size, size), raw)


frames = {s: load(s) for s in (16, 24, 32, 48, 64, 128, 256)}
frames[32].save(dst / "32x32.png")
frames[128].save(dst / "128x128.png")
frames[256].save(dst / "128x128@2x.png")
frames[256].resize((512, 512), Image.LANCZOS).save(dst / "icon.png")
order = [256, 128, 64, 48, 32, 24, 16]
frames[256].save(
    dst / "icon.ico",
    format="ICO",
    sizes=[(s, s) for s in order],
    append_images=[frames[s] for s in order[1:]],
)
# tauri-build 只在 tauri.conf.json 变化时重跑，图标换了不会重新嵌入 exe：刷新它的修改时间
conf = dst.parent / "tauri.conf.json"
if conf.exists():
    conf.touch()
print("写出", dst)
