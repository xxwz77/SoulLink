package com.soullink.android.player

/**
 * P0-4 摇杆纯逻辑（可单测）：8 方向映射 / 死区 / 方向键索引。
 * 从 VirtualControlsLayer 抽出（原来 private fun 不可测）。
 */
object JoystickLogic {

    /** 8 方向：0上 1右 2下 3左 4右下 5左下 6左上 7右上（atan2 角度 → 扇区）。 */
    fun dirFromAngle(angDeg: Double): Int = when {
        angDeg >= -22.5 && angDeg < 22.5 -> 1
        angDeg >= 22.5 && angDeg < 67.5 -> 7
        angDeg >= 67.5 && angDeg < 112.5 -> 0
        angDeg >= 112.5 && angDeg < 157.5 -> 6
        angDeg >= 157.5 || angDeg < -157.5 -> 3
        angDeg >= -157.5 && angDeg < -112.5 -> 5
        angDeg >= -112.5 && angDeg < -67.5 -> 2
        else -> 4
    }

    /** 方向 → 主键索引（keys=[上,右,下,左]=W,D,S,A；斜角按主导轴分量归类）。 */
    fun dirKeyIndex(dir: Int): Int = when (dir) {
        1, 4, 7 -> 1    // 右、右下、右上 → D（x 分量主导）
        2 -> 2          // 下 → S
        3, 5 -> 3       // 左、左下 → A（x 分量主导，贴近左）
        6, 0 -> 0       // 左上、上 → W（y 分量主导）
        else -> 0
    }

    /** 死区判定：偏移长度 < deadzone → -1（居中，不发键）。 */
    fun applyDeadzone(len: Double, deadzone: Double): Int = if (len < deadzone) -1 else 1

    /**
     * 方向切换序列（防双键核心）：返回 (keyUpOld, keyDownNew)。
     * old/new 为方向码；-1 = 无。切换时必须先 KeyUp 旧再 KeyDown 新。
     */
    fun transitionKeys(oldDir: Int, newDir: Int, keys: List<String>): Pair<String?, String?> {
        val up = if (oldDir >= 0) keys.getOrNull(dirKeyIndex(oldDir)) else null
        val down = if (newDir >= 0) keys.getOrNull(dirKeyIndex(newDir)) else null
        return up to down
    }

    /** 回中：KeyUp 全部方向键（防角色一直走）。 */
    fun releaseAllKeys(keys: List<String>): List<String> = keys.toList()
}