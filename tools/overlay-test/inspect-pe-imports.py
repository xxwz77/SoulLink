import struct, json, os
files=[r"D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaXterm.exe",r"D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaRTE.exe"]
for path in files:
    data=open(path,'rb').read()
    pe=struct.unpack_from('<I',data,0x3c)[0]
    coff=pe+4
    machine,nsections,timestamp,psym,nsym,optsize,chars=struct.unpack_from('<HHIIIHH',data,coff)
    opt=coff+20
    magic=struct.unpack_from('<H',data,opt)[0]
    dd=opt+(112 if magic==0x20b else 96)
    import_rva,import_size=struct.unpack_from('<II',data,dd+8)
    sections=[]
    sec=opt+optsize
    for i in range(nsections):
        name=data[sec:sec+8].split(b'\0')[0].decode(errors='replace')
        vs,va,rs,rp=struct.unpack_from('<IIII',data,sec+8)
        sections.append((va,max(vs,rs),rp))
        sec+=40
    def rva_to_off(rva):
        for va,size,rp in sections:
            if va<=rva<va+size:return rp+(rva-va)
        return None
    imports=[]
    if import_rva:
        off=rva_to_off(import_rva)
        for i in range(4096):
            desc=off+i*20
            original,ts,fwd,name_rva,first=struct.unpack_from('<IIIII',data,desc)
            if not any((original,ts,fwd,name_rva,first)):break
            no=rva_to_off(name_rva)
            if no is None:continue
            end=data.find(b'\0',no)
            imports.append(data[no:end].decode(errors='replace'))
    print(json.dumps({"file":os.path.basename(path),"machine":hex(machine),"imports":imports},ensure_ascii=False))
