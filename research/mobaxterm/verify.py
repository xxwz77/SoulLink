
import struct, json, re
src = open(r'research/mobaxterm/pe.py','r',encoding='utf-8').read()
src = src[:src.index("for path in [")]
ns={}; exec(compile(src,'pe','exec'), ns); PE=ns['PE']
IB=0x400000
for path,name,climit in [(r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaRTE.exe','MobaRTE',0x335e8c),
                         (r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaXterm.exe','MobaXterm',0x752ab0)]:
    pe=PE(path); d=pe.data
    print('='*66); print(name)
    # WS_CHILD bit patterns
    for pat,desc in ((b'\x0d\x00\x00\x00\x40','or eax,0x40000000'),
                     (b'\x81\xc8\x00\x00\x00\x40','or eax,0x40000000 (imm32)'),
                     (b'\x00\x00\x00\x40\x50','push 0x40000000'),
                     (b'\x00\x00\x00\x40','dword 0x40000000')):
        c=0; ex=[]
        i=0
        while True:
            i=d.find(pat,i)
            if i<0: break
            r=pe.off2va(i)
            if r is not None and r<climit:
                c+=1
                if len(ex)<5: ex.append(hex(r))
            i+=1
        print('  %-26s %s count=%d %s'%(desc,pat.hex(),c,ex))
    # SWP flag constants near SetParent sites
    for pat,desc in ((b'\x44\x42\x00\x00','SWP 0x4244'),(b'\x24\x42\x00\x00','SWP 0x4224'),
                     (b'\x84\x42\x00\x00','SWP 0x4284'),(b'\x06\x00\x00\x00' ,'MSG?')):
        c=0; ex=[]; i=0
        while True:
            i=d.find(pat,i)
            if i<0: break
            r=pe.off2va(i)
            if r is not None and r<climit:
                c+=1
                if len(ex)<5: ex.append(hex(r))
            i+=1
        print('  %-26s count=%d %s'%(desc,c,ex))
