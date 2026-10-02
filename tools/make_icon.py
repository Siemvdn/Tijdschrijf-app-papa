"""Maakt app/app.ico uit logo.png (vierkant, wit opgevuld, 256 px, PNG-in-ICO)."""
import os
from PIL import Image

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
im = Image.open(os.path.join(root, "logo.png")).convert("RGBA")
side = max(im.size)
sq = Image.new("RGBA", (side, side), (255, 255, 255, 255))
sq.paste(im, ((side - im.width) // 2, (side - im.height) // 2))
sq.save(os.path.join(root, "app", "app.ico"), sizes=[(256, 256), (64, 64), (48, 48), (32, 32), (16, 16)])
print("app/app.ico gemaakt")
