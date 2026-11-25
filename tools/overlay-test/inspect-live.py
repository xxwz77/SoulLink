import ctypes, json
from ctypes import wintypes
u = ctypes.windll.user32
u.SetProcessDPIAware()
PID = 73808
EnumWindows = u.EnumWindows
EnumWindows.argtypes = [ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM), wintypes.LPARAM]
GetWindowThreadProcessId = u.GetWindowThreadProcessId
GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
class RECT(ctypes.Structure): _fields_ = [("left", wintypes.LONG), ("top", wintypes.LONG), ("right", wintypes.LONG), ("bottom", wintypes.LONG)]
rows=[]
def cb(hwnd, lp):
    pid=wintypes.DWORD()
    GetWindowThreadProcessId(hwnd,ctypes.byref(pid))
    if pid.value == PID:
        title=ctypes.create_unicode_buffer(512); u.GetWindowTextW(hwnd,title,512)
        cls=ctypes.create_unicode_buffer(256); u.GetClassNameW(hwnd,cls,256)
        r=RECT(); u.GetWindowRect(hwnd,ctypes.byref(r))
        rows.append({"hwnd":int(hwnd),"visible":bool(u.IsWindowVisible(hwnd)),"title":title.value,"class":cls.value,"rect":[r.left,r.top,r.right,r.bottom],"exstyle":u.GetWindowLongW(hwnd,-20),"owner":int(u.GetWindowLongPtrW(hwnd,-8))})
    return True
EnumWindows(ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)(cb),0)
print(json.dumps(rows,ensure_ascii=False))