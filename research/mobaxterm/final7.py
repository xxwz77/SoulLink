
import struct, json, re
src = open(r'research/mobaxterm/pe.py','r',encoding='utf-8').read()
src = src[:src.index("for path in [")]
ns={}; exec(compile(src,'pe','exec'), ns); PE=ns['PE']
from capstone import Cs, CS_ARCH_X86, CS_MODE_32
md=Cs(CS_ARCH_X86, CS_MODE_32)
IB=0x400000
IMP=json.load(open(r'research/mobaxterm/imports.json'))
X=r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaXterm.exe'
R=r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaRTE.exe'
def mk(path,which):
    pe=PE(path); d=pe.data
    stubs={int(k,16):tuple(v) for k,v in IMP[which]['stubs'].items()}
    def s_at(va):
        if not (IB<=va<IB+0x7e0000): return None
        o=pe.va2off(va-IB)
        if o is None or o<4: return None
        p=struct.unpack_from('<i',d,o-4)[0]
        if 0<p<260:
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
            print('%s%08x  %-16s %-7s %-32s%s'%(ind,ins.address,ins.bytes.hex(),ins.mnemonic,ins.op_str,note))
    return dis
disX=mk(X,'MobaXterm'); disR=mk(R,'MobaRTE')
print('### MobaXterm: the "already embedded" check 0x69cd20 ###')
disX(0x69cd00,0x69cd60)
print()
print('### MobaXterm: handle getter 0x81774 ###')
disX(0x81750,0x817b0)
print()
print('### MobaRTE RDP unit: where host hwnd (+0x358) is consumed (0x30aee0..0x30afd0) ###')
disR(0x30aee0,0x30afd0)
