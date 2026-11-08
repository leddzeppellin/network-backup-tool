"""Gera assets/icon.ico (16 a 256 px) e assets/icon.png. Uso: python tools/make_icon.py"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter

S = 1024
out = Path(__file__).resolve().parent.parent / "assets"
out.mkdir(exist_ok=True)

def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))

# Fundo: quadrado arredondado com degradê diagonal azul → ciano.
grad = Image.new("RGB", (S, S))
px = grad.load()
c1, c2 = (14, 74, 160), (18, 170, 200)
for y in range(S):
    for x in range(S):
        px[x, y] = lerp(c1, c2, (x + y) / (2 * S))
mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle((40, 40, S - 40, S - 40), radius=210, fill=255)
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
img.paste(grad, (0, 0), mask)

# Sombra suave da pasta.
shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
sd = ImageDraw.Draw(shadow)
def folder(d, dx, dy, fill):
    d.rounded_rectangle((200 + dx, 300 + dy, 520 + dx, 420 + dy), radius=40, fill=fill)  # aba
    d.rounded_rectangle((200 + dx, 360 + dy, 824 + dx, 770 + dy), radius=60, fill=fill)  # corpo
folder(sd, 0, 24, (0, 30, 70, 110))
shadow = shadow.filter(ImageFilter.GaussianBlur(26))
img = Image.alpha_composite(img, Image.composite(shadow, Image.new("RGBA", (S, S)), mask))

d = ImageDraw.Draw(img)
folder(d, 0, 0, (255, 255, 255, 255))

# Duas setas (») dentro da pasta: envio rápido.
blue = (14, 100, 180, 255)
def chevron(x, w=150, h=240, t=70):
    cy = 565
    d.polygon([(x, cy - h // 2), (x + t, cy - h // 2), (x + t + w, cy), (x + t, cy + h // 2),
               (x, cy + h // 2), (x + w, cy)], fill=blue)
chevron(330)
chevron(510)

img.save(out / "icon.png")
sizes = [256, 128, 64, 48, 32, 24, 16]
frames = [img.resize((s, s), Image.LANCZOS) for s in sizes]
frames[0].save(out / "icon.ico", sizes=[(s, s) for s in sizes], append_images=frames[1:])
# macOS: .icns (o pacote .app usa este)
img.resize((1024, 1024), Image.LANCZOS).save(out / "icon.icns", sizes=[(16, 16), (32, 32), (64, 64), (128, 128), (256, 256), (512, 512), (1024, 1024)])
for s in (256, 48, 16):
    frames[sizes.index(s)].save(out / f"preview_{s}.png")
print("ok")
