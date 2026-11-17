
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
    def prologue_before(self,va,limit=0x600):
        best=None
        for s in range(va-1, va-limit, -1):
            o=self.pe.va2off(s)
            if o is None: continue
            if self.d[o]==0x55 and self.d[o+1]==0x8b and self.d[o+2]==0xec:
                code=self.d[o:o+(va-s)+32]; landed=False
                for ins in md.disasm(code,s):
                    if ins.address==va: landed=True; break
                    if ins.address>va: break
                if landed: return s
        return None
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
            print('%s%08x  %-16s %-7s %-34s%s'%(ind,ins.address,ins.bytes.hex(),ins.mnemonic,ins.op_str,note))
R=Bin('MobaRTE')
for t in (0x820f4, 0x2d5f83, 0x2d6b6a, 0x2dbbec):
    p=R.prologue_before(t)
    print('\n================ SetParent @ %#x  (func %s) ================'%(t, hex(p) if p else '?'))
    if p: R.dis(p, t+0x90)
