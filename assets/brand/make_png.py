# Logo e icona di Renamr. Font: Outfit Bold (licenza OFL, Google Fonts); percorso in OUTFIT_FONT.
# Uso: pip install pillow fonttools; python3 make_png.py; python3 make_svg.py
import os
from PIL import Image, ImageDraw, ImageFont, ImageFilter
F=os.environ.get('OUTFIT_FONT', 'Outfit-Bold.ttf')
NAVY=(15,27,61,255); ACCENT=(47,107,255,255); WHITE=(255,255,255,255)

def wordmark(color, tagline=True, H=600):
    big=ImageFont.truetype(F, 400); small=ImageFont.truetype(F, 74)
    W=2000; im=Image.new('RGBA',(W,H),(0,0,0,0)); d=ImageDraw.Draw(im)
    d.text((0,0),"Renamr",font=big,fill=color)
    bb=d.textbbox((0,0),"Renamr",font=big)
    if tagline:
        # tagline larga quanto la parola, lettere spaziate
        text="SMART FILE RENAMING TOOL"; target=bb[2]-bb[0]
        widths=[d.textlength(c,font=small) for c in text]
        gap=(target-sum(widths))/(len(text)-1)
        x=bb[0]; y=bb[3]+40
        for c,w in zip(text,widths):
            d.text((x,y),c,font=small,fill=color); x+=w+gap
    return im.crop(im.getbbox())

def mark(S=1024):
    im=Image.new('RGBA',(S,S),(0,0,0,0))
    # quadrato arrotondato con sfumatura navy -> blu
    grad=Image.new('RGBA',(S,S))
    gd=ImageDraw.Draw(grad)
    for y in range(S):
        t=y/(S-1)
        c=tuple(int(NAVY[i]*(1-t*0.55)+ACCENT[i]*(t*0.55)) for i in range(3))+(255,)
        gd.line([(0,y),(S,y)],fill=c)
    m=Image.new('L',(S,S),0); ImageDraw.Draw(m).rounded_rectangle([0,0,S-1,S-1],radius=int(S*0.22),fill=255)
    im.paste(grad,(0,0),m)
    d=ImageDraw.Draw(im)
    f=ImageFont.truetype(F,int(S*0.66))
    bb=d.textbbox((0,0),"R",font=f)
    w,h=bb[2]-bb[0],bb[3]-bb[1]
    x=(S-w)/2-bb[0]-S*0.04; y=(S-h)/2-bb[1]-S*0.03
    d.text((x,y),"R",font=f,fill=WHITE)
    # cursore di testo accanto alla R: "rinomina"
    cx=x+bb[2]+S*0.05; d.rounded_rectangle([cx,y+bb[1]+h*0.08,cx+S*0.055,y+bb[3]],radius=int(S*0.025),fill=(120,170,255,255))
    return im

for name,col in [('logo-light',NAVY),('logo-dark',WHITE)]:
    wm=wordmark(col)
    for hpx in (48,96,144):
        r=wm.resize((round(wm.width*hpx/wm.height),hpx),Image.LANCZOS); r.save(f'{name}-{hpx}.png')
    wm.save(f'{name}-full.png')
mk=mark()
mk.save('mark-1024.png')
for s in (16,24,32,48,64,128,256,512):
    mk.resize((s,s),Image.LANCZOS).save(f'mark-{s}.png')
mk.resize((256,256),Image.LANCZOS).save('renamr.ico',sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
for name,col in [('wordmark-light',NAVY),('wordmark-dark',WHITE)]:
    wm=wordmark(col,tagline=False)
    for hpx in (40,80):
        wm.resize((round(wm.width*hpx/wm.height),hpx),Image.LANCZOS).save(f'{name}-{hpx}.png')
