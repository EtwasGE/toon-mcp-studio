from PIL import Image, ImageDraw, ImageFont
from pathlib import Path
root = Path(__file__).resolve().parent.parent
im = Image.new('RGB', (640, 940), '#e8f1ed')
d = ImageDraw.Draw(im)
font = ImageFont.truetype('C:/Windows/Fonts/arial.ttf', 32)
small = ImageFont.truetype('C:/Windows/Fonts/arial.ttf', 26)
d.rectangle((0,0,640,120), fill='#325366')
d.text((30,28), 'УЧЕБНЫЙ ЭКРАН', fill='white', font=font)
d.text((30,72), 'FIXTURE · НЕ ИНТЕРФЕЙС БОТА', fill='#ead49c', font=small)
for y,title,detail in [(190,'Мои планы','Пример карточки'),(370,'Свободный вечер','Время для себя'),(550,'Чашка чая','Демонстрационные данные')]:
    d.rounded_rectangle((25,y,615,y+145),radius=22,fill='white')
    d.text((50,y+24),title,fill='#253a48',font=font)
    d.text((50,y+80),detail,fill='#597678',font=small)
d.rounded_rectangle((25,760,615,850),radius=22,fill='#357f83')
d.text((120,785),'Пример кнопки',fill='white',font=font)
im.save(root/'examples/offline-dialogue/screen.png')
