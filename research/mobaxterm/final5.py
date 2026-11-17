
import struct, json, re
src = open(r'research\mobaxterm\pe.py','r',encoding='utf-8').read()
src = src[:src.index("for path in [")]
ns={}; exec(compile(src,'pe','exec'), ns); PE=ns['PE']
IB=0x400000
IMP=json.load(open(r'research\mobaxterm\imports.json'))
X=r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaXterm.exe'
pe=PE(X); d=pe.data
def s_at(va):
    if not (IB<=va<IB+0x7e0000): return None
    o=pe.va2off(va-IB)
    if o is None or o<4: return None
    p=struct.unpack_from('<i',d,o-4)[0]
    if 0<p<260:
        s=d[o:o+p]
        if all(32<=c<127 or c in (9,10,13) for c in s): return s.decode('latin1')
    return None
def find_abs(addr):
    pat=struct.pack('<I',addr); out=[]; i=0
    while True:
        i=d.find(pat,i)
        if i<0: break
        r=pe.off2va(i)
        if r is not None and 0x1000<=r<0x760000: out.append(r)
        i+=1
    return out
print('--- RDP arg template strings (VA = RVA+0x400000) ---')
for rva in (0x4d8940,0x4d8950,0x4d8964,0x4d8974,0x4d8988,0x4d89a0,0x4d89b8,0x4d89d0,0x4d89e8,0x4d8a10,0x4d8a28):
    va=IB+rva
    print('  %-18r VA=%#010x refs=%s'%(s_at(va),va,[hex(x) for x in find_abs(va)][:6]))
print()
print('--- -mobahwnd adjacent strings ---')
for rva in (0x4d89c8,0x4d8a08,0x4d8a32,0x4d8b52,0x4d8b00):
    va=IB+rva
    print('  %-18r VA=%#010x refs=%s'%(s_at(va),va,[hex(x) for x in find_abs(va)][:6]))
