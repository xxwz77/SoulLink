
import struct, json, re
src = open(r'research\mobaxterm\pe.py','r',encoding='utf-8').read()
src = src[:src.index("for path in [")]
ns={}; exec(compile(src,'pe','exec'), ns); PE=ns['PE']
IB=0x400000
IMP=json.load(open(r'research\mobaxterm\imports.json'))
def s_at(pe,d,va):
    if not (IB<=va<IB+0x7e0000): return None
    o=pe.va2off(va-IB)
    if o is None or o<4: return None
    p=struct.unpack_from('<i',d,o-4)[0]
    if 0<p<260:
        s=d[o:o+p]
        if all(32<=c<127 or c in (9,10,13) for c in s): return s.decode('latin1')
    return None
def find_abs(pe,d,addr):
    pat=struct.pack('<I',addr); out=[]; i=0
    while True:
        i=d.find(pat,i)
        if i<0: break
        r=pe.off2va(i)
        if r: out.append(r)
        i+=1
    return out

for path,name,tests in [
  (r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaXterm.exe','MobaXterm',
    [(0x405ab3ec,'mobardp unit'),(0x405ab3fc,'mobarte unit'),(0x4070a91c,'UIMainClass'),
     (0x405ab4b8,'TMobaRDPForm'),(0x404f4e8c,'MobaRTE.exe'),(0x405de184,'/bin/MobaRTE.exe')]),
  (r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaRTE.exe','MobaRTE',
    [(0x4070a91c,'UIMainClass'),(0x4070a764,'THdpiRdpForm'),(0x4070a774,'TMobaRDPForm'),
     (0x4052bbf0,'?'),(0x4070a7dc,'TFormStorePasswords?')]),
]:
    pe=PE(path); d=pe.data
    print('='*70); print(name)
    for va,label in tests:
        s=s_at(pe,d,va)
        refs=find_abs(pe,d,va)
        print('  %-22s %#010x  %r\n      refs=%s'%(label,va,s,[hex(x) for x in refs][:8]))
