
import struct, json, re
src = open(r'research/mobaxterm/pe.py','r',encoding='utf-8').read()
src = src[:src.index("for path in [")]
ns={}; exec(compile(src,'pe','exec'), ns); PE=ns['PE']
from capstone import Cs, CS_ARCH_X86, CS_MODE_32
md=Cs(CS_ARCH_X86, CS_MODE_32)
IB=0x400000
X=r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaXterm.exe'
pe=PE(X); d=pe.data
code=[s for s in pe.secs if s['name']=='CODE'][0]
clo,chi=code['va'],code['va']+code['vsize']
def prologue(va,limit=0x800):
    for s in range(va-1, va-limit,-1):
        o=pe.va2off(s)
        if o is None: continue
        if d[o]==0x55 and d[o+1]==0x8b and d[o+2]==0xec:
            cb=d[o:o+(va-s)+32]
            for ins in md.disasm(cb,s):
                if ins.address==va: return s
                if ins.address>va: break
    return None
def rel_callers(target):
    out=[]
    for i in range(len(d)-5):
        if d[i] in (0xe8,0xe9):
            r=pe.off2va(i)
            if r is None or not (clo<=r<chi): continue
            rel=struct.unpack_from('<i',d,i+1)[0]
            if ((r+5+rel)&0xffffffff)==target: out.append(r)
    return out
p=prologue(0x3734e6)
print('CreateProcessA enclosing func prologue:', hex(p) if p else None)
if p:
    print('  callers of %#x: %s'%(p,[hex(x) for x in rel_callers(p)][:20]))
    print('  callers of %#x: %s'%(p-1,[hex(x) for x in rel_callers(p-1)][:5]) if d[pe.va2off(p)-1] else '')
# also: does the RDP session string-builder region reference 0x3734e6 func?
for t in (0x3734e6,):
    print(' direct refs to %#x: %s'%(t,[hex(x) for x in rel_callers(t)][:5]))
