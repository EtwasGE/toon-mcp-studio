"""Original synthetic cutout sheet for offline rig tests; not an imported person's sketch."""
import sys
from pathlib import Path
from PIL import Image, ImageDraw

output = Path(sys.argv[1])
output.parent.mkdir(parents=True, exist_ok=True)
sheet = Image.new("RGBA", (640, 512))
ink = "#283446"
for tile in range(18):
    cell = Image.new("RGBA", (128, 128))
    draw = ImageDraw.Draw(cell)
    if tile == 0:
        draw.rounded_rectangle((5, 2, 123, 125), 18, fill="#c86340", outline=ink, width=4)
        draw.line((64, 8, 64, 120), fill="#f3ce82", width=4)
    elif tile == 1:
        draw.rounded_rectangle((6, 5, 122, 123), 30, fill="#edbb84", outline=ink, width=4)
        draw.rounded_rectangle((15, 3, 112, 35), 12, fill="#42384a", outline=ink, width=4)
    elif tile in (2, 3):
        draw.rounded_rectangle((25, 2, 103, 100), 25, fill="#c86340", outline=ink, width=5)
        draw.ellipse((30, 82, 98, 124), fill="#edbb84", outline=ink, width=5)
    elif tile == 4:
        for x in (8, 82):
            draw.ellipse((x, 15, x + 35, 110), fill="#fff7e9", outline=ink, width=5)
            draw.ellipse((x + 12, 43, x + 25, 85), fill=ink)
    elif tile == 5:
        draw.line((8, 64, 43, 64), fill=ink, width=13)
        draw.line((82, 64, 117, 64), fill=ink, width=13)
    elif tile in (6, 7, 8, 9):
        if tile == 6:
            draw.arc((15, 18, 113, 100), 0, 180, fill=ink, width=9)
        else:
            height = {7: 30, 8: 105, 9: 75}[tile]
            draw.ellipse((20, 64 - height // 2, 108, 64 + height // 2), fill="#673b46", outline=ink, width=5)
    elif 10 <= tile <= 15:
        delta = [0, 28, -28, -15, 12, 6][tile - 10]
        draw.line((8, 64 + delta, 43, 64 - delta), fill=ink, width=12)
        draw.line((82, 64 - delta, 117, 64 + delta), fill=ink, width=12)
    elif tile == 16:
        draw.rounded_rectangle((12, 2, 116, 126), 12, fill=ink)
        draw.rectangle((22, 16, 106, 108), fill="#b1d8d3")
    else:
        draw.rounded_rectangle((5, 0, 57, 125), 14, fill="#394e69", outline=ink, width=5)
        draw.rounded_rectangle((71, 0, 123, 125), 14, fill="#394e69", outline=ink, width=5)
    sheet.alpha_composite(cell, (tile % 5 * 128, tile // 5 * 128))
sheet.save(output)
print(output)
