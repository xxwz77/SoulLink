
import struct, json, re
src = open(r'research\mobaxterm\pe.py','r',encoding='utf-8').read()
src = src[:src.index("for path in [")]
ns={}; exec(compile(src,'pe','exec'), ns); PE=ns['PE']
IB=0x400000
def s_at(pe,d,va):
    if not (IB<=va<IB+0x7e0000): return None
    o=pe.va2off(va-IB)
    if o is None or o<4: return None
    p=struct.unpack_from('<i',d,o-4)[0]
    if 0<p<260:
        s=d[o:o+p]
        if all(32<=c<127 or c in (9,10,13) for c in s): return s.decode('latin1')
    return None
def find_abs(pe,d,addr,lo=None,hi=None):
    pat=struct.pack('<I',addr); out=[]; i=0
    while True:
        i=d.find(pat,i)
        if i<0: break
        r=pe.off2va(i)
        if r is not None and (lo is None or lo<=r<hi): out.append(r)
        i+=1
    return out
for path,name,tests,code in [
  (r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaXterm.exe','MobaXterm',
    [(0x5ab3ec,'mobardp unit'),(0x5ab3fc,'mobarte unit'),(0x70a91c,'UIMainClass'),
     (0x5ab4b8,'TMobaRDPForm'),(0x4f4e8c,'MobaRTE.exe'),(0x5de184,'/bin/MobaRTE.exe'),
     (0x5ab428,'-disabletopbar'),(0x5ab494,'-rdp '),(0x5ab4a4,'-numtab ')],(0x1000,0x75aab0)),
  (r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaRTE.exe','MobaRTE',
    [(0x70a91c,'UIMainClass'),(0x70a764,'THdpiRdpForm'),(0x70a774,'TMobaRDPForm')],(0x1000,0x335e8c)),
]:
    pe=PE(path); d=pe.data
    print('='*70); print(name)
    for rva,label in tests:
        va=IB+rva; s=s_at(pe,d,va)
        refs=find_abs(pe,d,va,code[0],code[1])
        print('  %-22s VA=%#010x %r'%(label,va,s))
        print('      code refs=%s'%([hex(x) for x in refs][:8] or 'none'))
