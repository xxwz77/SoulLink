
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
    def find_alignment(self, va):
        """find start offset s such that linear decode from s lands exactly on va"""
        best=None
        for back in range(1,33):
            s=va-back
            off=self.pe.va2off(s)
            if off is None: continue
            code=self.d[off:off+back+64]
            ok=False
            for ins in md.disasm(code,s):
                if ins.address==va:
                    ok=True; break
                if ins.address>va: break
            if ok:
                return s
        return va
    def dis(self,va,before=120,after=200,title=''):
        s=self.find_alignment(va)
        off=self.pe.va2off(s); code=self.d[off:off+(va-s)+after]
        print('\n--- %s  (align from %#x) ---'%(title or ('%#x'%va), s))
        for ins in md.disasm(code,s):
            mark='  ' 
            note=''
            if ins.address==va: mark='>>'
            if ins.mnemonic in ('call','jmp') and ins.op_str.startswith('0x'):
                t=int(ins.op_str,16)
                if t in self.stubs:
                    dll,fn=self.stubs[t]; note='   ;; %s!%s'%(dll.split('.')[0],fn)
            for tok in re.split(r'[\[\]\+\*\s,]+', ins.op_str):
                if tok.startswith('0x'):
                    L=self.lab(int(tok,16))
                    if L: note+='   ; "%s"'%L
            print('%s %08x  %-14s %-7s %-30s%s'%(mark,ins.address,ins.bytes.hex(),ins.mnemonic,ins.op_str,note))
X=Bin('MobaXterm'); R=Bin('MobaRTE')
print('############ MobaXterm SetParent call sites ############')
for va in (0x7ed7c, 0x55ff68, 0x56d833, 0x583367):
    X.dis(va, before=110, after=120, title='SetParent@%#x'%va)
