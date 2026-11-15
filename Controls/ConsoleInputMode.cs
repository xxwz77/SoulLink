namespace SoulLink.Controls
{
    /// <summary>画面鼠标操作模式。</summary>
    public enum ConsoleInputMode
    {
        /// <summary>自由视角：光标隐藏并锁在画面中心，移动即转视角（可无限旋转）。</summary>
        FreeLook = 0,

        /// <summary>指针模式：绝对定位，可以精确点击画面任意位置。</summary>
        Pointer = 1
    }
}
