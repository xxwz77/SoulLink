
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
def raw(va,n=120):
    o=pe.va2off(va)
    return d[o:o+n] if o is not None else b''
def lab(va):
    o=pe.va2off(va)
    if o is None or o<4: return ''
    p=struct.unpack_from('<i',d,o-4)[0]
    if 0<p<220:
        s=d[o:o+p]
        if all(32<=c<127 or c in (9,10,13) for c in s): return s.decode('latin1')
    return ''
print('=== format strings in .text (codeless strings) ===')
for va in (0x6d5b28,0x6d69ac,0x6d69c4,0x6d69d8,0x6d69ec,0x6d6a00,0x6d6434,0x6d6498,0x6d64a4,0x6d64b0,0x6d64c0,0x6d6a74,0x6d6ac8,0x6d6ad4):
    print('  %#08x  %r'%(va, raw(va,140).split(b'\x00')[0][:120]))
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
                L=lab(int(tok,16))
                if L: note+='   ; "%s"'%L
        print('%s%08x  %-16s %-7s %-34s%s'%(ind,ins.address,ins.bytes.hex(),ins.mnemonic,ins.op_str,note))
print('\n=== attach branch 0x2d5c15..0x2d5f90 ===')
dis(0x2d5c15, 0x2d5f90)
