
import sys, os
src = open(r'research\mobaxterm\pe.py','r',encoding='utf-8').read()
src = src[:src.index("for path in [")]
ns={}; exec(compile(src,'pe','exec'), ns)
PE=ns['PE']; parse_imports=ns['parse_imports']; parse_resources=ns['parse_resources']

paths = [(r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaXterm.exe','MobaXterm.exe'),
         (r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaRTE.exe','MobaRTE.exe')]

for path,name in paths:
    pe=PE(path); imp=parse_imports(pe)
    bydll={}
    for d,fs in imp:
        bydll.setdefault(d.lower(),set()).update(fs)
    print('='*72); print(name)
    for dll in ('user32.dll','ole32.dll','oleaut32.dll','ws2_32.dll','kernel32.dll','advapi32.dll','shell32.dll'):
        if dll in bydll:
            fs=sorted(bydll[dll])
            print('\n[%s] %d:'%(dll,len(fs)))
            print('  '+', '.join(fs))
