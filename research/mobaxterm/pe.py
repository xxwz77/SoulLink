
import struct, sys, os

def u16(b,o): return struct.unpack_from('<H',b,o)[0]
def u32(b,o): return struct.unpack_from('<I',b,o)[0]
def u64(b,o): return struct.unpack_from('<Q',b,o)[0]

class PE:
    def __init__(self, path):
        self.path=path
        self.data=open(path,'rb').read()
        d=self.data
        assert d[:2]==b'MZ'
        pe=u32(d,0x3c)
        assert d[pe:pe+4]==b'PE\0\0'
        self.pe=pe
        nsec=u16(d,pe+6); optsz=u16(d,pe+20)
        opt=pe+24
        self.magic=u16(d,opt)
        self.pe32plus = self.magic==0x20b
        self.imagebase = u64(d,opt+24) if self.pe32plus else u32(d,opt+28)
        self.sizeofimage=u32(d,opt+56)
        self.entry=u32(d,opt+16)
        self.checksum=u32(d,opt+64)
        # data directories
        ddoff = opt + (112 if self.pe32plus else 96)
        self.dirs=[]
        for i in range(16):
            self.dirs.append((u32(d,ddoff+i*8), u32(d,ddoff+i*8+4)))
        secs=[]
        so=opt+optsz
        for i in range(nsec):
            o=so+i*40
            name=d[o:o+8].rstrip(b'\0').decode('latin1')
            vsize=u32(d,o+8); va=u32(d,o+12); rsize=u32(d,o+16); rptr=u32(d,o+20)
            chars=u32(d,o+36)
            secs.append(dict(name=name,vsize=vsize,va=va,rsize=rsize,rptr=rptr,chars=chars))
        self.secs=secs
    def off2va(self,off):
        for s in self.secs:
            if s['rptr']<=off<s['rptr']+max(s['rsize'],1):
                return s['va']+(off-s['rptr'])
        return None
    def va2off(self,va):
        for s in self.secs:
            if s['va']<=va<s['va']+max(s['vsize'],s['rsize'],1):
                return s['rptr']+(va-s['va'])
        return None
    def read_str(self,off):
        e=self.data.find(b'\0',off)
        return self.data[off:e].decode('latin1')

def rva_to_off(pe,rva):
    return pe.va2off(rva)

def parse_imports(pe):
    out=[]
    rva,size = pe.dirs[1]
    if rva==0: return out
    off=pe.va2off(rva)
    i=0
    while True:
        o=off+i*20
        oft=u32(pe.data,o); name=u32(pe.data,o+12); ft=u32(pe.data,o+16)
        if name==0: break
        dll=pe.read_str(pe.va2off(name))
        funcs=[]
        thunk = oft if oft else ft
        to=pe.va2off(thunk)
        j=0
        while True:
            if pe.pe32plus:
                v=u64(pe.data,to+j*8)
                if v==0: break
                if v & (1<<63):
                    funcs.append('#%d'%(v&0xffff))
                else:
                    funcs.append(pe.read_str(pe.va2off(v&0x7fffffff)+2))
            else:
                v=u32(pe.data,to+j*4)
                if v==0: break
                if v & (1<<31):
                    funcs.append('#%d'%(v&0xffff))
                else:
                    funcs.append(pe.read_str(pe.va2off(v&0x7fffffff)+2))
            j+=1
        out.append((dll,funcs))
        i+=1
    return out

def parse_delay(pe):
    out=[]
    rva,size=pe.dirs[13]
    if rva==0: return out
    off=pe.va2off(rva); i=0
    while True:
        o=off+i*32
        attrs=u32(pe.data,o)
        name=u32(pe.data,o+4)
        if name==0 and attrs==0: break
        dll=pe.read_str(pe.va2off(name))
        out.append(dll)
        i+=1
    return out

def parse_exports(pe):
    rva,size=pe.dirs[0]
    if rva==0: return []
    off=pe.va2off(rva)
    n=u32(pe.data,off+24)
    names=u32(pe.data,off+32)
    no=pe.va2off(names)
    out=[]
    for i in range(n):
        r=u32(pe.data,no+i*4)
        out.append(pe.read_str(pe.va2off(r)))
    return out

def parse_resources(pe):
    rva,size=pe.dirs[2]
    if rva==0: return []
    base=pe.va2off(rva)
    found=[]
    def walk(off, depth, path):
        hdr=off
        nnamed=u16(pe.data,hdr+12); nid=u16(pe.data,hdr+14)
        n=nnamed+nid
        for i in range(n):
            e=hdr+16+i*8
            nameid=u32(pe.data,e); offs=u32(pe.data,e+4)
            if nameid & 0x80000000:
                so=base+(nameid&0x7fffffff)
                ln=u16(pe.data,so)
                nm=pe.data[so+2:so+2+ln*2].decode('utf-16le','replace')
            else:
                nm=nameid
            if offs & 0x80000000:
                walk(base+(offs&0x7fffffff), depth+1, path+[nm])
            else:
                do=base+offs
                drva=u32(pe.data,do); dsize=u32(pe.data,do+4)
                found.append((path+[nm], drva, dsize))
    walk(base,0,[])
    return found

for path in [r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaXterm.exe',
             r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaRTE.exe']:
    pe=PE(path)
    print('='*70)
    print(os.path.basename(path), 'magic=%#x'%pe.magic, 'imagebase=%#x'%pe.imagebase, 'entry=%#x'%pe.entry)
    print('sections:')
    for s in pe.secs:
        print('   %-8s va=%#010x vsize=%#010x rsize=%#010x raw=%#010x chars=%#x'%(s['name'],s['va'],s['vsize'],s['rsize'],s['rptr'],s['chars']))
    print('exports:', parse_exports(pe)[:40])
    imp=parse_imports(pe)
    print('import DLLs (%d):'%len(imp))
    for dll,funcs in imp:
        print('   %-28s %4d funcs'%(dll,len(funcs)))
    print('delay DLLs:', parse_delay(pe))
    res=parse_resources(pe)
    print('resources: %d entries'%len(res))
    kinds={}
    for p,drva,dsize in res:
        t=p[0]
        kinds.setdefault(str(t),0)
        kinds[str(t)]+=1
    print('   by type:', kinds)
