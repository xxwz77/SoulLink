
import struct, re
src = open(r'research\mobaxterm\pe.py','r',encoding='utf-8').read()
src = src[:src.index("for path in [")]
ns={}; exec(compile(src,'pe','exec'), ns); PE=ns['PE']
IB=0x400000
pe=PE(r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaRTE.exe'); d=pe.data
def get(rva,n=80):
    o=pe.va2off(rva)
    return d[o:o+n] if o is not None else b''
def s_at(va):
    rva=va-IB
    o=pe.va2off(rva)
    if o is None: return None
    p=struct.unpack_from('<i',d,o-4)[0]
    if 0<p<240:
        s=d[o:o+p]
        if all(32<=c<127 or c in (9,10,13) for c in s): return s.decode('latin1')
    return None
for va in (0x6d6440,0x6d6458,0x6d6468,0x6d6484,0x6d6498,0x6d64a4,0x6d64b0,0x6d64c0,
           0x6d6a00,0x6d6a74,0x6d6ac8,0x6d6ad4,0x6d69c4,0x6d69d8,0x6d69ec,0x70a5f0,0x70a67c,0x70a708):
    print('%#010x (rva %#08x)  -> %r'%(va, va-IB, s_at(va)))
print()
print('--- raw scan 0x2d6400..0x2d6b00 (rva) ---')
o=pe.va2off(0x2d6400); chunk=d[o:o+0x700]
for m in re.finditer(rb'[\x20-\x7e]{3,}', chunk):
    print('  rva %#08x  %r'%(0x2d6400+m.start(), m.group().decode('latin1')))
