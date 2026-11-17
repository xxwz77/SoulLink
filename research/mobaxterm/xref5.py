
import struct, json, re
src = open(r'research\mobaxterm\pe.py','r',encoding='utf-8').read()
src = src[:src.index("for path in [")]
ns={}; exec(compile(src,'pe','exec'), ns); PE=ns['PE']
from capstone import Cs, CS_ARCH_X86, CS_MODE_32
md=Cs(CS_ARCH_X86, CS_MODE_32)
IB=0x400000
R=r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaRTE.exe'
pe=PE(R); d=pe.data
IMP=json.load(open(r'research\mobaxterm\imports.json'))
stubs={int(k,16):tuple(v) for k,v in IMP['MobaRTE']['stubs'].items()}
code=[s for s in pe.secs if s['name']=='CODE'][0]
clo,chi=code['va'],code['va']+code['vsize']

def find_abs(addr):
    pat=struct.pack('<I',addr); out=[]; i=0
    while True:
        i=d.find(pat,i)
        if i<0: break
        r=pe.off2va(i)
        if r is not None and clo<=r<chi: out.append(r)
        i+=1
    return out

def s_at(rva):
    o=pe.va2off(rva)
    if o is None or o<4: return None
    p=struct.unpack_from('<i',d,o-4)[0]
    if 0<p<240:
        s=d[o:o+p]
        if all(32<=c<127 or c in (9,10,13) for c in s): return s.decode('latin1')
    return None

targets={'mobahwnd':0x3090e4,'disabletopbar':0x3090f8,'numtab':0x309140,'displayres':0x3091b8,
         'Fit to screen resolution':0x309194,'numtab-code':0x309150,'wsNormal':0x309164}
for k,va in targets.items():
    print('%-24s RVA=%#08x operand=%#010x  xrefs=%s'%(k,va,IB+va,[hex(x) for x in find_abs(IB+va)]))
print()
print('RDPConnect arg table:')
for rva in (0x30c2a0,0x30c2b8,0x30c2cc,0x30c2e4,0x30c304,0x30c31c,0x30c330,0x30c340,0x30c3d8,0x30c42c,0x30c450,0x30c4e0,0x30c538):
    print('  %#010x  %r  xrefs=%s'%(IB+rva, s_at(rva), [hex(x) for x in find_abs(IB+rva)][:6]))
