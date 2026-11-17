
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

# find all instructions referencing +0x358
hits=[]
for pat in (b'\x58\x03\x00\x00', b'\x58\x03\x00' ):
    i=0
    while True:
        i=d.find(pat,i)
        if i<0: break
        r=pe.off2va(i)
        if r and clo<=r<chi: hits.append(r)
        i+=1
hits=sorted(set(h for h in hits))
print('refs to +0x358:',[hex(h) for h in hits])

def s_at(va):
    if not (IB<=va<IB+0x7e0000): return None
    o=pe.va2off(va-IB)
    if o is None or o<4: return None
    p=struct.unpack_from('<i',d,o-4)[0]
    if 0<p<240:
        s=d[o:o+p]
        if all(32<=c<127 or c in (9,10,13) for c in s): return s.decode('latin1')
    return None
def dis(a,b,ind='  '):
    o=pe.va2off(a); code=d[o:o+(b-a)]
    for ins in md.disasm(code,a):
        note=''
        if ins.mnemonic in ('call','jmp') and ins.op_str.startswith('0x'):
            t=int(ins.op_str,16)
            if t in stubs:
                dll,fn=stubs[t]; note='   ;; %s!%s'%(dll.split('.')[0],fn)
        for tok in re.split(r'[\[\]\+\*\s,]+', ins.op_str):
            if tok.startswith('0x'):
                L=s_at(int(tok,16))
                if L: note+='   ; "%s"'%L
        print('%s%08x  %-16s %-7s %-34s%s'%(ind,ins.address,ins.bytes.hex(),ins.mnemonic,ins.op_str,note))
