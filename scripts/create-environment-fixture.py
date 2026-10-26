"""Original synthetic workshop for offline environment tests; not a submitted sketch."""
from pathlib import Path
import sys
from PIL import Image, ImageDraw

output = Path(sys.argv[1])
output.parent.mkdir(parents=True, exist_ok=True)
image = Image.new("RGB", (1080, 1920), "#d8e5df")
draw = ImageDraw.Draw(image)
draw.rectangle((0, 1180, 1080, 1920), fill="#adbaad")
draw.rounded_rectangle((70, 320, 440, 900), 30, fill="#386879", outline="#293d49", width=10)
draw.rounded_rectangle((640, 320, 1010, 900), 30, fill="#386879", outline="#293d49", width=10)
for x in (255, 825):
    draw.line((x, 325, x, 895), fill="#adcbbd", width=12)
draw.rectangle((480, 425, 600, 595), fill="#d7a45b", outline="#293d49", width=8)
draw.rectangle((60, 1210, 1020, 1390), fill="#a96645", outline="#483a36", width=10)
draw.rectangle((80, 1225, 1000, 1260), fill="#e6b374")
draw.rectangle((100, 1390, 130, 1700), fill="#483a36")
draw.rectangle((950, 1390, 980, 1700), fill="#483a36")
draw.rectangle((200, 1145, 260, 1220), fill="#815142")
draw.line((230, 1010, 230, 1160), fill="#315b43", width=12)
draw.ellipse((192, 1030, 235, 1100), fill="#498a56")
draw.ellipse((226, 1050, 268, 1125), fill="#498a56")
image.save(output)
print(output)
