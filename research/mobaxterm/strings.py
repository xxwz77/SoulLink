
import sys, os, re, struct

def u16(b,o): return struct.unpack_from('<H',b,o)[0]
def u32(b,o): return struct.unpack_from('<I',b,o)[0]

src = open(r'research\mobaxterm\pe.py','r',encoding='utf-8').read()
src = src[:src.index("for path in [")]
ns={}; exec(compile(src,'pe','exec'), ns)
PE=ns['PE']

paths=[(r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaXterm.exe','MobaXterm'),
       (r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaRTE.exe','MobaRTE')]

os.makedirs('research/mobaxterm/out', exist_ok=True)

def ascii_strings(data, minlen=4):
    out=[]
    i=0; n=len(data)
    cur=bytearray(); start=0
    for i,c in enumerate(data):
        if 0x20<=c<0x7f or c==9:
            if not cur: start=i
            cur.append(c)
        else:
            if len(cur)>=minlen:
                out.append((start,cur.decode('latin1')))
            cur=bytearray()
    if len(cur)>=minlen: out.append((start,cur.decode('latin1')))
    return out

def utf16_strings(data, minlen=3):
    out=[]
    i=0; n=len(data)-1
    while i<n:
        # look for a run of printable ascii chars followed by 0
        if 0x20<=data[i]<0x7f and data[i+1]==0:
            j=i; chars=[]
            while j+1<n and 0x20<=data[j]<0x7f and data[j+1]==0:
                chars.append(chr(data[j])); j+=2
            if len(chars)>=minlen:
                out.append((i,''.join(chars)))
            i=j
        else:
            i+=1
    return out

for path,name in paths:
    pe=PE(path)
    data=pe.data
    a=ascii_strings(data,4)
    w=utf16_strings(data,3)
    with open('research/mobaxterm/out/%s_ascii.txt'%name,'w',encoding='utf-8') as f:
        for off,s in a:
            rva=pe.off2va(off)
            f.write('%08x\t%s\n'%(rva if rva else off, s.replace('\t','\\t')))
    with open('research/mobaxterm/out/%s_utf16.txt'%name,'w',encoding='utf-8') as f:
        for off,s in w:
            rva=pe.off2va(off)
            f.write('%08x\t%s\n'%(rva if rva else off, s.replace('\t','\\t')))
    print('%s: ascii=%d utf16=%d'%(name,len(a),len(w)))

    # targeted keyword search
    kw = ['mobahwnd','MobaHWND','IsChildWindow','ParentWindow','RaiseRootWindow','WMMouseActivate',
          'MobaRDP','mobardp','TFormMobaRDP','TFrameRDP','SmartSizing','DesktopResize','DesktopWidth',
          'MobaRTE','mobar','MobaXterm.exe','moba','MOBA','RDP','rdp']
    allstr = [(o,s,'A') for o,s in a] + [(o,s,'W') for o,s in w]
    seen=set()
    res=[]
    for k in kw:
        for o,s,t in allstr:
            if k in s:
                res.append((k,o,t,s))
    print('  matched %d lines for keywords'%len(res))
