
import struct, json, sys, re
src = open(r'research\mobaxterm\pe.py','r',encoding='utf-8').read()
src = src[:src.index("for path in [")]
ns={}; exec(compile(src,'pe','exec'), ns); PE=ns['PE']; u32=ns['u32']
from capstone import Cs, CS_ARCH_X86, CS_MODE_32
md=Cs(CS_ARCH_X86, CS_MODE_32)

IMP=json.load(open(r'research\mobaxterm\imports.json'))
PATHS={'MobaXterm':r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaXterm.exe',
       'MobaRTE':r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaRTE.exe'}

class Bin:
    def __init__(self,name):
        self.name=name; self.pe=PE(PATHS[name]); self.d=self.pe.data
        self.stubs={int(k,16):tuple(v) for k,v in IMP[name]['stubs'].items()}
    def lab(self,va):
        o=self.pe.va2off(va)
        if o is None or o<4: return ''
        pref=struct.unpack_from('<i',self.d,o-4)[0]
        if 0<pref<200:
            s=self.d[o:o+pref]
            if all(32<=c<127 or c in (9,10,13) for c in s):
                return s.decode('latin1')
        return ''
    def dis(self,start,end,indent='  '):
        off=self.pe.va2off(start); code=self.d[off:off+(end-start)]
        out=[]
        for ins in md.disasm(code,start):
            note=''
            m=ins.mnemonic; ops=ins.op_str
            if m in ('call','jmp') and ops.startswith('0x'):
                t=int(ops,16)
                if t in self.stubs:
                    dll,fn=self.stubs[t]; note='  ;; -> %s!%s (IMPORT)'%(dll,fn)
            toks=re.split(r'[\[\]\+\*\s,]+', ops)
            for tok in toks:
                if tok.startswith('0x'):
                    t=int(tok,16)
                    L=self.lab(t)
                    if L: note+='   ; "%s"'%L
            out.append('%s%08x  %-16s %-7s %-34s%s'%(indent,ins.address,ins.bytes.hex(),m,ops,note))
        return '\n'.join(out)

X=Bin('MobaXterm'); R=Bin('MobaRTE')

print('#### MobaRTE 0x2d4f00..0x2d5350 (RTE form: args, AppMessage) ####')
print(R.dis(0x2d4f00,0x2d5350))
