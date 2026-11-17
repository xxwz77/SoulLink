
import struct, json, re
src = open(r'research/mobaxterm/pe.py','r',encoding='utf-8').read()
src = src[:src.index("for path in [")]
ns={}; exec(compile(src,'pe','exec'), ns); PE=ns['PE']
from capstone import Cs, CS_ARCH_X86, CS_MODE_32
md=Cs(CS_ARCH_X86, CS_MODE_32)
IB=0x400000
IMP=json.load(open(r'research/mobaxterm/imports.json'))
R=r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaRTE.exe'
pe=PE(R); d=pe.data
stubs={int(k,16):tuple(v) for k,v in IMP['MobaRTE']['stubs'].items()}
def s_at(va):
    rva=va-IB
    if not (0<=rva<0x7e0000): return None
    o=pe.va2off(rva)
    if o is None or o<4: return None
    p=struct.unpack_from('<i',d,o-4)[0]
    if 0<p<260:
        s=d[o:o+p]
        if all(32<=c<127 or c in (9,10,13) for c in s): return s.decode('latin1')
    return None
def dis(a,b,ind='  '):
    o=pe.va2off(a); cb=d[o:o+(b-a)]
    n=0
    for ins in md.disasm(cb,a):
        note=''
        if ins.mnemonic in ('call','jmp') and ins.op_str.startswith('0x'):
            t=int(ins.op_str,16)
            if t in stubs:
                dll,fn=stubs[t]; note='   ;; %s!%s'%(dll.split('.')[0],fn)
        for tok in re.split(r'[\[\]\+\*\s,]+', ins.op_str):
            if tok.startswith('0x'):
                L=s_at(int(tok,16))
                if L: note+='   ; "%s"'%L
        print('%s%08x  %-16s %-7s %-30s%s'%(ind,ins.address,ins.bytes.hex(),ins.mnemonic,ins.op_str,note))
        n+=1
        if n>200: break
print('### Application.OnMessage handler 0x3096a0 ###')
dis(0x3096a0, 0x309900)
