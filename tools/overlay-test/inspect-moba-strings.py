import re,os,json
for path in [r"D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaXterm.exe",r"D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaRTE.exe"]:
 data=open(path,'rb').read()
 strings=[m.group().decode('latin1',errors='ignore') for m in re.finditer(rb'[ -~]{5,}',data)]
 pats=re.compile(r'(mstsc|mstsclib|msrdp|freerdp|rdpclient|rdp.*dll|split|multiexec|terminal|enhanced.graphics|flicker|directdraw|opengl|gdi\+)',re.I)
 matches=[]
 for s in strings:
  if pats.search(s) and s not in matches:matches.append(s)
 print("\nFILE="+os.path.basename(path))
 for s in matches[:180]:print(s[:500])
