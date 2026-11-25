import re,os
path=r"D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaRTE.exe"
data=open(path,'rb').read()
strings=[m.group().decode('latin1',errors='ignore') for m in re.finditer(rb'[ -~]{4,}',data)]
pats=re.compile(r'(parent|child|window.*handle|hwnd|embed|container|panel|terminal|session|smartsizing|desktopwidth|desktopheight|updateSession|resize|full.?screen|connect.*rdp|rdp.*connect|showwindow|setparent|windowsForms|VCL|TForm|TPanel)',re.I)
seen=[]
for s in strings:
 if pats.search(s) and s not in seen:seen.append(s)
for s in seen[:260]:print(s[:500])
