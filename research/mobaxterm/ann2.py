
import struct, json, re
src = open(r'research\mobaxterm\pe.py','r',encoding='utf-8').read()
src = src[:src.index("for path in [")]
ns={}; exec(compile(src,'pe','exec'), ns); PE=ns['PE']
from capstone import Cs, CS_ARCH_X86, CS_MODE_32
md=Cs(CS_ARCH_X86, CS_MODE_32)
IMP=json.load(open(r'research\mobaxterm\imports.json'))
PATHS={'MobaXterm':r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaXterm.exe',
       'MobaRTE':r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaRTE.exe'}
class Bin:
    def __init__(self,n):
        self.n=n; self.pe=PE(PATHS[n]); self.d=self.pe.data
        self.stubs={int(k,16):tuple(v) for k,v in IMP[n]['stubs'].items()}
    def lab(self,va):
        o=self.pe.va2off(va)
        if o is None or o<4: return ''
        p=struct.unpack_from('<i',self.d,o-4)[0]
        if 0<p<200:
            s=self.d[o:o+p]
            if all(32<=c<127 or c in (9,10,13) for c in s): return s.decode('latin1')
        return ''
    def dis(self,a,b,ind='  '):
        off=self.pe.va2off(a); code=self.d[off:off+(b-a)]
        for ins in md.disasm(code,a):
            note=''
            if ins.mnemonic in ('call','jmp') and ins.op_str.startswith('0x'):
                t=int(ins.op_str,16)
                if t in self.stubs:
                    dll,fn=self.stubs[t]; note='   ;; %s!%s'%(dll.split('.')[0],fn)
            for tok in re.split(r'[\[\]\+\*\s,]+', ins.op_str):
                if tok.startswith('0x'):
                    L=self.lab(int(tok,16))
                    if L: note+='   ; "%s"'%L
            print('%s%08x  %-14s %-7s %-32s%s'%(ind,ins.address,ins.bytes.hex(),ins.mnemonic,ins.op_str,note))
X=Bin('MobaXterm')
print('### MobaXterm 0x30f540..0x30f680 : FindWindowA/EnumChildWindows cluster ###')
X.dis(0x30f540,0x30f680)
print()
print('### MobaXterm SetParent @0x583367 ###')
X.dis(0x583320,0x583460)
print()
print('### MobaXterm SetParent @0x55ff68 ###')
X.dis(0x55ff20,0x55ffe0)
