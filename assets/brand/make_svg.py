# Logo e icona di Renamr. Font: Outfit Bold (licenza OFL, Google Fonts); percorso in OUTFIT_FONT.
# Uso: pip install pillow fonttools; python3 make_png.py; python3 make_svg.py
import os
from fontTools.ttLib import TTFont
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.pens.boundsPen import BoundsPen
f=TTFont(os.environ.get('OUTFIT_FONT', 'Outfit-Bold.ttf'))
gs=f.getGlyphSet(); cmap=f.getBestCmap(); upm=f['head'].unitsPerEm
def run(text,size,x0,y0,spacing=0.0):
    pen=SVGPathPen(gs); x=x0; s=size/upm
    for c in text:
        g=cmap[ord(c)]
        tp=TransformPen(pen,(s,0,0,-s,x,y0)); gs[g].draw(tp)
        x+=gs[g].width*s+spacing
    return pen.getCommands(), x
def width(text,size,spacing=0):
    s=size/upm; return sum(gs[cmap[ord(c)]].width*s for c in text)+spacing*(len(text)-1)
big=400; w=width("Renamr",big)
d1,_=run("Renamr",big,0,300)
small=74; n=len("SMART FILE RENAMING TOOL")
sp=(w-width("SMART FILE RENAMING TOOL",small))/(n-1)
d2,_=run("SMART FILE RENAMING TOOL",small,0,300+40+small,sp)
for name,col in [('renamr-logo','#0F1B3D'),('renamr-logo-dark','#FFFFFF')]:
    open(name+'.svg','w').write(f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="-4 -4 {w+8:.0f} {300+40+small+20+8}"><g fill="{col}"><path d="{d1}"/><path d="{d2}"/></g></svg>\n')
# marchio
S=1024; size=S*0.66
gR=gs[cmap[ord('R')]]; bp=BoundsPen(gs); gR.draw(bp); xmin,ymin,xmax,ymax=bp.bounds; s=size/upm
gw=(xmax-xmin)*s; gh=(ymax-ymin)*s
x=(S-gw)/2-xmin*s-S*0.04; base=(S+gh)/2+ymin*s
pen=SVGPathPen(gs); gR.draw(TransformPen(pen,(s,0,0,-s,x,base)))
cx=x+xmax*s+S*0.05; top=base-ymax*s+gh*0.08
open('renamr-icon.svg','w').write(f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {S} {S}">
  <defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#0F1B3D"/><stop offset="1" stop-color="#2149AE"/></linearGradient></defs>
  <rect width="{S}" height="{S}" rx="{S*0.22:.0f}" fill="url(#g)"/>
  <path fill="#FFFFFF" d="{pen.getCommands()}"/>
  <rect x="{cx:.0f}" y="{top:.0f}" width="{S*0.055:.0f}" height="{base-top:.0f}" rx="{S*0.025:.0f}" fill="#78AAFF"/>
</svg>
''')
