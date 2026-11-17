
import struct, json, re
src = open(r'research\mobaxterm\pe.py','r',encoding='utf-8').read()
src = src[:src.index("for path in [")]
ns={}; exec(compile(src,'pe','exec'), ns); PE=ns['PE']
from capstone import Cs, CS_ARCH_X86, CS_MODE_32
md=Cs(CS_ARCH_X86, CS_MODE_32)
R=r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaRTE.exe'
pe=PE(R); d=pe.data
IMP=json.load(open(r'research\mobaxterm\imports.json'))
stubs={int(k,16):tuple(v) for k,v in IMP['MobaRTE']['stubs'].items()}
code=[s for s in pe.secs if s['name']=='CODE'][0]
clo,chi=code['va'],code['va']+code['vsize']

def find_abs(addr, lo=None, hi=None):
    pat=struct.pack('<I',addr); out=[]; i=0
    while True:
        i=d.find(pat,i)
        if i<0: break
        r=pe.off2va(i)
        if r is not None and (lo is None or lo<=r<hi): out.append(r)
        i+=1
    return out

print('global 0x77356f refs:', [hex(x) for x in find_abs(0x77356f, clo, chi)])
print('global 0x773568 refs:', [hex(x) for x in find_abs(0x773568, clo, chi)][:12])

# relative callers of 0x2d5b28
def rel_callers(target,lo,hi):
    out=[]
    for i in range(len(d)-5):
        if d[i] in (0xe8,0xe9):
            r=pe.off2va(i)
            if r is None or not (lo<=r<hi): continue
            rel=struct.unpack_from('<i',d,i+1)[0]
            if ((r+5+rel)&0xffffffff)==target: out.append(r)
    return out
print('rel callers of 0x2d5b28:', [hex(x) for x in rel_callers(0x2d5b28,clo,chi)])
print('rel callers of 0x2d5b0c(line?):', [hex(x) for x in rel_callers(0x2d5ae4,clo,chi)])
