
import struct
src = open(r'research\mobaxterm\pe.py','r',encoding='utf-8').read()
src = src[:src.index("for path in [")]
ns={}; exec(compile(src,'pe','exec'), ns); PE=ns['PE']

def label_at(pe, va):
    off=pe.va2off(va)
    if off is None: return '?'
    d=pe.data
    # is it a Delphi length-prefixed header?
    if off>=4:
        pref=struct.unpack_from('<i',d,off-4)[0]
        if 0<=pref<200:
            s=d[off:off+pref]
            if all(32<=c<127 or c in (9,10,13) for c in s):
                return 'S%d:%r'%(pref, s.decode('latin1'))
    return '?'

def scan(pe, lo, hi):
    d=pe.data; out=[]
    for i in range(len(d)-4):
        v=struct.unpack_from('<I',d,i)[0]
        if lo<=v<hi:
            r=pe.off2va(i)
            if r is not None:
                sec=None
                for s in pe.secs:
                    if s['va']<=r<s['va']+max(s['vsize'],s['rsize'],1): sec=s['name']
                out.append((r,v,sec))
    return out

for path,name,ranges in [
  (r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaXterm.exe','MobaXterm',
    [(0x004d8800,0x004d8c00),(0x004f4e00,0x004f5000),(0x005de000,0x005de600)]),
  (r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaRTE.exe','MobaRTE',
    [(0x00309000,0x00309600),(0x00139000,0x00139200)]),
]:
    pe=PE(path)
    print('='*70); print(name)
    for lo,hi in ranges:
        res=scan(pe,lo,hi)
        print('\n range %#x..%#x : %d refs'%(lo,hi,len(res)))
        for a,v,sec in res[:40]:
            print('    %#010x (%s) -> %#010x  %s'%(a,sec,v,label_at(pe,v)))
