using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace SoulLink.Services
{
    /// <summary>
    /// 临时关闭系统「指针加速」（增强指针精确度）。
    ///
    /// 为什么需要：不关的话转视角的手感会很飘——
    /// 实测推 70 像素会被系统按加速度曲线放大成 154，角度就不可控了。
    ///
    /// ★ 设计要点：
    ///   - 用「谁开的谁关」的**代次**机制，而不是简单计数。
    ///     计数在多实例 / 异常路径下很容易漏减或错配，最后把用户系统设置留在关闭状态。
    ///   - 每次强制恢复都会递增代次，旧的持有者失效，不会把已恢复的设置又关掉。
    /// </summary>
    public static class PointerAcceleration
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSE_PARAMS
        {
            public int cbSize;
            public int MouseSpeed;
            public int MouseThreshold1;
            public int MouseThreshold2;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(int uiAction, int uiParam, ref MOUSE_PARAMS pvParam, int fWinIni);

        private const int SPI_GETMOUSE = 0x0003;
        private const int SPI_SETMOUSE = 0x0004;
        private const int SPIF_UPDATEINIFILE = 0x01;
        private const int SPIF_SENDCHANGE = 0x02;

        // 本机原值兜底（读不到时用）：speed=10 / t1=1 / t2=0
        private static readonly MOUSE_PARAMS Fallback = new MOUSE_PARAMS
        {
            MouseSpeed = 10,
            MouseThreshold1 = 1,
            MouseThreshold2 = 0
        };

        private static readonly Lock Gate = new();
        private static int _ownerCount;
        private static int _generation;
        private static MOUSE_PARAMS _saved;
        private static bool _savedValid;

        /// <summary>当前是否处于「已关闭加速」状态。</summary>
        public static bool IsDisabled
        {
            get { lock (Gate) return _ownerCount > 0; }
        }

        /// <summary>
        /// 申请关闭加速。返回一个**代次令牌**，释放时必须带回来，
        /// 这样旧持有者不会误关掉别人刚恢复的设置。
        /// </summary>
        public static int PushDisabled()
        {
            lock (Gate)
            {
                try
                {
                    if (_ownerCount == 0)
                    {
                        var cur = new MOUSE_PARAMS { cbSize = Marshal.SizeOf<MOUSE_PARAMS>() };
                        if (SystemParametersInfo(SPI_GETMOUSE, 0, ref cur, 0))
                        {
                            _saved = cur;
                            _savedValid = true;
                        }

                        var off = new MOUSE_PARAMS
                        {
                            cbSize = Marshal.SizeOf<MOUSE_PARAMS>(),
                            MouseSpeed = 0,
                            MouseThreshold1 = 0,
                            MouseThreshold2 = 0
                        };
                        SystemParametersInfo(SPI_SETMOUSE, 0, ref off, SPIF_SENDCHANGE);
                    }

                    _ownerCount++;
                    return _generation;
                }
                catch { return -1; }
            }
        }

        /// <summary>释放（仅当代次仍然有效时）。</summary>
        public static void PopRestore(int token)
        {
            lock (Gate)
            {
                try
                {
                    if (token < 0) return;
                    if (token != _generation) return;   // 已被强制恢复过，忽略

                    if (_ownerCount > 0) _ownerCount--;
                    if (_ownerCount > 0) return;

                    RestoreLocked();
                }
                catch { }
            }
        }

        /// <summary>
        /// 无条件恢复（程序退出 / 异常兜底）。
        /// 会递增代次，让所有未释放的持有者失效。
        /// </summary>
        public static void ForceRestore()
        {
            lock (Gate)
            {
                try
                {
                    _ownerCount = 0;
                    _generation++;
                    RestoreLocked();
                }
                catch { }
            }
        }

        private static void RestoreLocked()
        {
            try
            {
                var restore = _savedValid ? _saved : Fallback;
                restore.cbSize = Marshal.SizeOf<MOUSE_PARAMS>();
                SystemParametersInfo(SPI_SETMOUSE, 0, ref restore, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            }
            catch { }
        }
    }
}
