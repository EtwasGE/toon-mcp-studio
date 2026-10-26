"""Pre-compression pixel check of the explicit screen fixture and a review contact sheet."""
import sys, json, base64, subprocess
from pathlib import Path
from PIL import Image, ImageChops, ImageDraw

run = Path(sys.argv[1]).resolve()
report = json.loads((run/'acceptance.json').read_text(encoding='utf-8'))
job = run/'jobs'/report['final_job']
timeline = json.loads((job/'timeline.json').read_text(encoding='utf-8'))
frame = timeline['scenes'][1]['start_frame']
actual = Image.open(job/f'frame-{frame:04}.png').convert('RGB')
source = Image.open(Path(__file__).resolve().parent.parent/'examples/offline-dialogue/screen.png').convert('RGB')
# Remotion globally uses border-box: a 640x950 frame with 12px border
# contains a 616x926 image box. Independently rasterize the original with
# the pinned browser's contain scaling, including fractional source pixels.
root = Path(__file__).resolve().parent.parent
browser = root/'renderer/node_modules/.remotion/chrome-headless-shell/win64/chrome-headless-shell-win64/chrome-headless-shell.exe'
data = base64.b64encode((root/'examples/offline-dialogue/screen.png').read_bytes()).decode()
html = run/'expected-screen.html'
html.write_text(f'<html><body style="margin:0;background:#172735"><img src="data:image/png;base64,{data}" style="position:absolute;left:202px;top:362px;width:616px;height:926px;object-fit:contain;display:block"></body></html>',encoding='utf-8')
expected_path=run/'expected-screen.png'
subprocess.run([str(browser),'--headless','--no-sandbox','--hide-scrollbars','--force-device-scale-factor=1','--window-size=1080,1920',f'--screenshot={expected_path}',html.as_uri()],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
region = actual.crop((202,373,818,1277))
expected = Image.open(expected_path).convert('RGB').crop((202,373,818,1277))
diff = ImageChops.difference(region, expected)
assert diff.getbbox() is None, f'Exact screenshot differs: {diff.getbbox()}'
files = sorted(job.glob('frame-*.png'))
sheet = Image.new('RGB', (5*216, ((len(files)+4)//5)*410), '#e9e5dc')
draw = ImageDraw.Draw(sheet)
for i, file in enumerate(files):
    thumb = Image.open(file).convert('RGB').resize((216,384))
    x, y = i%5*216, i//5*410
    sheet.paste(thumb,(x,y)); draw.text((x+5,y+387),file.stem,fill='#182d3d')
sheet.save(run/'contact-sheet.jpg',quality=95)
(run/'visual-fixture-check.json').write_text(json.dumps({'exact_fixture_pixels':True,'source_size':source.size,'source_preserved':True,'frame':frame,'human_phone_readability':'pending','real_bot_interface':'pending'},indent=2),encoding='utf-8')
print(run/'contact-sheet.jpg')
print('Exact screenshot fixture pixels: PASS')
