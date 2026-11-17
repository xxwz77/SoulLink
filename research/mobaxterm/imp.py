
import struct, os, sys
sys.path.insert(0,r'research\mobaxterm')
from pe import PE, u16, u32, u64

paths = {r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaXterm.exe':'MobaXterm.exe',
         r'D:\AAA挂机专区\远程本地多用户\MobaXterm\MobaRTE.exe':'MobaRTE.exe'}

import importlib.util
spec = importlib.util.spec_from_file_location('pemod', r'research\mobaxterm\pe.py')
pemod = importlib.util.module_from_spec(spec)
# pe.py runs analysis at import; guard by re-reading source minus the tail
src = open(r'research\mobaxterm\pe.py','r',encoding='utf-8').read()
cut = src.index("for path in [")
src = src[:cut]
ns = {}
exec(compile(src,'pe','exec'), ns)
PE = ns['PE']; parse_imports=ns['parse_imports']; parse_resources=ns['parse_resources']

INTEREST = ['SetParent','GetParent','SetWindowLong','GetWindowLong','SetWindowPos','DeferWindowPos',
 'IsChild','IsWindowVisible','ShowWindow','SetForegroundWindow','GetForegroundWindow','SetActiveWindow',
 'SetFocus','GetFocus','AttachThreadInput','SetThreadDesktop','CreateWindowEx','CreateWindow',
 'MoveWindow','RegisterWindowMessage','SendMessage','PostMessage','SendMessageTimeout','PeekMessage',
 'WaitForInputIdle','CreateProcess','OpenProcess','ReadProcessMemory','WriteProcessMemory',
 'SetWindowsHookEx','CallNextHookEx','EnumWindows','EnumChildWindows','GetWindowThreadProcessId',
 'ChangeWindowMessageFilter','ChangeWindowMessageFilterEx','SetWinEventHook','UnhookWinEvent',
 'DwmEnableComposition','DwmSetWindowAttribute','SetWindowRgn','UpdateLayeredWindow','CreateComposition',
 'PrintWindow','BitBlt','StretchBlt','SetLayeredWindowAttributes','CreateDesktop','CreateProcessAsUser',
 'WTSQueryUserToken','DuplicateTokenEx','CreateWindowStation','AdjustTokenPrivileges','OpenInputDesktop',
 'SwitchDesktop','EnumDesktops','SetThreadDesktop','UiaHostProviderFromHwnd']

for path,name in paths.items():
    pe = PE(path)
    imp = parse_imports(pe)
    allfuncs=set()
    for dll,funcs in imp:
        for f in funcs: allfuncs.add((dll.lower(),f))
    print('='*72)
    print(name, '- interesting imports present:')
    fset = {f for d,f in allfuncs}
    for k in INTEREST:
        if k in fset: print('   +',k)
    print()
    print(name,'- full unique import count:', len(fset))
    bydll={}
    for d,f in allfuncs: bydll.setdefault(d,[]).append(f)
    for d in sorted(bydll):
        print('  [%s] (%d): %s'%(d,len(bydll[d]),', '.join(sorted(bydll[d]))))
    print()
    res = parse_resources(pe)
    print(name,'resources detail by type/name:')
    from collections import defaultdict
    g=defaultdict(list)
    for p,drva,dsize in res:
        g[str(p[0])].append((p[1:],dsize))
    for t in sorted(g):
        items=g[t]
        if t in ('6','10','12','1','2','3','3'):
            print('   type %s (%d):'%(t,len(items)), [ (nm,sz) for nm,sz in items ][:60])
        else:
            print('   type %s (%d):'%(t,len(items)), [ (nm,sz) for nm,sz in items ][:30])
