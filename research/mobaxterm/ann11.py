
import struct
src = open(r'research\mobaxterm\pe.py','r',encoding='utf-8').read()
src = src[:src.index("for path in [")]
ns={}; exec(compile(src,'pe','exec'), ns); PE=ns['PE']
pe=PE(r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaRTE.exe'); d=pe.data
for va in (0x6d6440,0x6d6458,0x6d6468,0x6d6484,0x6d6498,0x6d64a4,0x6d64b0,0x6d64c0,0x6d6a00,0x6d6a74,0x6d6ac8,0x6d6ad4,0x6d69c4,0x6d69d8,0x6d69ec):
    o=pe.va2off(va)
    print('%#08x  %s'%(va, d[o:o+56]))
print()
print('--- wide dump 0x6d6400..0x6d6b20 ---')
o=pe.va2off(0x6d6400)
chunk=d[o:o+0x720]
import re
for m in re.finditer(rb'[\x20-\x7e]{3,}', chunk):
    print('  %#08x  %r'%(0x6d6400+m.start(), m.group().decode('latin1')))
