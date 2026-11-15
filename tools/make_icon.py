from PIL import Image, ImageDraw, ImageFont
import os

size = 256
img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
draw = ImageDraw.Draw(img)

# Rounded base with a vertical gradient.
base = Image.new("RGBA", (size, size), (0, 0, 0, 0))
bd = ImageDraw.Draw(base)
for y in range(size):
    t = y / (size - 1)
    r = int(15 + (34 - 15) * t)
    g = int(18 + (22 - 18) * t)
    b = int(28 + (52 - 28) * t)
    bd.line([(0, y), (size, y)], fill=(r, g, b, 255))
mask = Image.new("L", (size, size), 0)
md = ImageDraw.Draw(mask)
md.rounded_rectangle([8, 8, size - 8, size - 8], radius=52, fill=255)
img.paste(base, (0, 0), mask)

# Accent glow arc.
glow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
gd = ImageDraw.Draw(glow)
gd.ellipse([116, -80, 330, 150], fill=(139, 92, 246, 60))
gd.ellipse([150, -40, 300, 120], fill=(34, 211, 238, 38))
img = Image.alpha_composite(img, glow)

draw = ImageDraw.Draw(img)

# Piano keyboard motif.
left, right = 30, size - 30
top, bottom = 128, 218
count = 7
key_w = (right - left) / count
for i in range(count):
    x0 = left + i * key_w
    x1 = left + (i + 1) * key_w
    draw.rounded_rectangle([x0 + 2, top, x1 - 2, bottom], radius=7, fill=(245, 247, 252, 255))
    draw.line([(x0 + 2, bottom - 12), (x1 - 2, bottom - 12)], fill=(205, 209, 222, 255), width=2)

# Black keys.
for i in [0, 1, 3, 4, 5]:
    cx = left + (i + 1) * key_w
    draw.rounded_rectangle([cx - 12, top - 2, cx + 12, top + 52], radius=5, fill=(18, 21, 31, 255))
    draw.rounded_rectangle([cx - 8, top + 4, cx + 8, top + 12], radius=3, fill=(139, 92, 246, 255))

# Simple wordmark.
try:
    font = ImageFont.truetype("C:/Windows/Fonts/segoeuib.ttf", 54)
except Exception:
    font = ImageFont.load_default()

text = "MBD"
bbox = draw.textbbox((0, 0), text, font=font)
tw = bbox[2] - bbox[0]
th = bbox[3] - bbox[1]
draw.text(((size - tw) / 2, 46 - th / 2), text, font=font, fill=(255, 255, 255, 255))

out = os.path.join(os.path.dirname(__file__), "..", "src", "MidiBroad.App", "Assets", "App.ico")
out = os.path.abspath(out)
os.makedirs(os.path.dirname(out), exist_ok=True)
img.save(out, sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print(out)
