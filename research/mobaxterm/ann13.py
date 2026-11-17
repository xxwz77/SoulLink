
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
def s_at_rva(rva, maxtry=2):
    o=pe.va2off(rva)
    if o is None or o<4: return None
    p=struct.unpack_from('<i',d,o-4)[0]
    if 0<p<240:
        s=d[o:o+p]
        if all(32<=c<127 or c in (9,10,13) for c in s): return s.decode('latin1')
    return None
def lab(opchar):
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
                t=int(tok,16)
                if IB<=t<IB+0x7e0000:
                    L=s_at_rva(t-IB)
                    if L: note+='   ; "%s"'%L
        print('%s%08x  %-16s %-7s %-34s%s'%(ind,ins.address,ins.bytes.hex(),ins.mnemonic,ins.op_str,note))
print('### MobaRTE RDP unit: parameter parsing (0x308d60..0x3090a0) ###')
dis(0x308d60,0x3090a0)
