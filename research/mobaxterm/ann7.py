
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

for pat in (b'\x90\x90\x13\x00', b'\x88\x90\x13\x00', b'\xe4\x90\x30\x00', b'\xdc\x90\x30\x00'):
    pos=[]; i=0
    while True:
        i=d.find(pat,i)
        if i<0: break
        pos.append(hex(pe.off2va(i) or 0)); i+=1
    print(pat.hex(), '->', pos[:10])

def lab(va):
    o=pe.va2off(va)
    if o is None or o<4: return ''
    p=struct.unpack_from('<i',d,o-4)[0]
    if 0<p<200:
        s=d[o:o+p]
        if all(32<=c<127 or c in (9,10,13) for c in s): return s.decode('latin1')
    return ''

def callers(target):
    out=[]; t=struct.pack('<I',target)
    for op,kind in ((b'\xe8','call'),(b'\xe9','jmp')):
        i=0
        while True:
            i=d.find(op+t,i)
            if i<0: break
            out.append((hex(pe.off2va(i)),kind)); i+=1
    return out

print('\ncallers of 0x2d5b28:', callers(0x2d5b28))
print('callers of 0x84aec :', callers(0x84aec)[:8])

def prologue_before(va,limit=0x800):
    for s in range(va-1, va-limit, -1):
        o=pe.va2off(s)
        if o is None: continue
        if d[o]==0x55 and d[o+1]==0x8b and d[o+2]==0xec:
            code=d[o:o+(va-s)+32]; 
            for ins in md.disasm(code,s):
                if ins.address==va: return s
                if ins.address>va: break
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
                L=lab(int(tok,16))
                if L: note+='   ; "%s"'%L
        print('%s%08x  %-16s %-7s %-34s%s'%(ind,ins.address,ins.bytes.hex(),ins.mnemonic,ins.op_str,note))
