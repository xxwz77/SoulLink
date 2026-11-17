
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

print('### search [reg+0x358] in UnitRDP region 0x304000..0x314000 ###')
pat=b'\x58\x03\x00\x00'
res=[]
i=pe.va2off(0x304000); end=pe.va2off(0x314000)
while i<end:
    i=d.find(pat,i)
    if i<0 or i>=end: break
    va=pe.off2va(i)
    # check preceding bytes look like disp32 form: 8b 8? / 89 8?  (mod=10)
    if i>=1 and d[i-1] in (0x80,0x81,0x82,0x83,0x86,0x87,0x88,0x89,0x8a,0x8b):
        res.append(va)
    i+=1
print([hex(x) for x in res])
for t in res[:6]:
    print('\n---- around %#x ----'%t)
    dis(max(0x304000,t-0x70), t+0x90)
