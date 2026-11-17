
import struct, json
src = open(r'research\mobaxterm\pe.py','r',encoding='utf-8').read()
src = src[:src.index("for path in [")]
ns={}; exec(compile(src,'pe','exec'), ns); PE=ns['PE']; u32=ns['u32']
IB=0x400000

def slots_of(pe):
    out={}
    rva,_=pe.dirs[1]; off=pe.va2off(rva); i=0
    while True:
        o=off+i*20
        nm=u32(pe.data,o+12); ft=u32(pe.data,o+16)
        if nm==0: break
        dll=pe.read_str(pe.va2off(nm))
        to=pe.va2off(ft); j=0
        while True:
            v=u32(pe.data,to+j*4)
            if v==0: break
            if not (v&(1<<31)):
                out[IB+ft+j*4]=(dll,pe.read_str(pe.va2off(v&0x7fffffff)+2))
            j+=1
        i+=1
    return out

result={}
for path,name in [(r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaXterm.exe','MobaXterm'),
                  (r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaRTE.exe','MobaRTE')]:
    pe=PE(path); slots=slots_of(pe); d=pe.data
    code=[s for s in pe.secs if s['name']=='CODE'][0]
    stubs={}
    for i in range(len(d)-6):
        if d[i]==0xff and d[i+1]==0x25:
            t=struct.unpack_from('<I',d,i+2)[0]
            if t in slots:
                r=pe.off2va(i)
                if r is not None and code['va']<=r<code['va']+code['vsize']:
                    stubs[r]=slots[t]
    result[name]={'stubs':{hex(k):v for k,v in stubs.items()}, 'slots':{hex(k):v for k,v in slots.items()}}
    print(name,'stubs',len(stubs))

json.dump(result, open(r'research\mobaxterm\imports.json','w'), indent=1)
# print full stub list for MobaRTE sorted
st=result['MobaRTE']['stubs']
for k in sorted(st, key=lambda x:int(x,16)):
    print('  %s  %s!%s'%(k, st[k][0], st[k][1]))
