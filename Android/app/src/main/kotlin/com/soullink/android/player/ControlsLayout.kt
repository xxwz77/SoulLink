package com.soullink.android.player

import org.json.JSONObject

/**
 * 虚拟按键数据模型（T41d，对齐 MOONLIGHT-VPLUS-ONSCREEN-CONTROLS 调研）。
 * 坐标全归一化 0~1（anchor=控件中心）——换屏/旋转不用改配置。
 * A 档：button + joystick；turbo/轮盘/组合键下轮。
 */
sealed class VControl {
    abstract val id: String
    abstract val anchor: Pair<Double, Double>
    abstract val size: Pair<Double, Double>
    abstract val layer: Int

    /** 按钮：按下 KeyDown(key)，抬起 KeyUp(key)。 */
    data class Button(
        override val id: String,
        override val anchor: Pair<Double, Double>,
        override val size: Pair<Double, Double>,
        override val layer: Int,
        val label: String,
        val key: String,
    ) : VControl()

    /** 摇杆：8 方向 WASD（keys=[上,右,下,左]）。deadzone=死区；stickyMs=粘滞。 */
    data class Joystick(
        override val id: String,
        override val anchor: Pair<Double, Double>,
        override val size: Pair<Double, Double>,
        override val layer: Int,
        val deadzone: Double,
        val stickyMs: Long,
        val keys: List<String>,
    ) : VControl()
}

/** 一套布局预设。 */
data class ControlsLayout(
    val name: String,               // desktop / fps / game3d
    val opacity: Int,               // 0~100
    val controls: List<VControl>,
) {
    companion object {
        /**
         * T41i 三套内置预设（用户 g019 截图定案：**散落圆形按键**，非摇杆网格）。
         * desktop = 无控件纯光标；摇杆类型保留在模型（VControl.Joystick）供后续预设选用。
         * anchor 归一化 0~1；size=直径占屏宽比例。
         */
        fun preset(name: String): ControlsLayout = when (name) {
            "fps" -> ControlsLayout("fps", 45, listOf(
                // ★ P0-4 摇杆替代 4 按键（Bug3 修法 + 用户「轮盘操作」定案）：
                //   左下轮盘（core-fixer 参数：圆心(0.13,0.73) 直径0.21、死区30%、粘滞150ms）
                VControl.Joystick("stick_move", 0.13 to 0.73, 0.21 to 0.21, 1, 0.30, 150L,
                    listOf("W", "D", "S", "A")),
                // 右侧技能簇（v0142 实测布局）
                VControl.Button("k_f", 0.80 to 0.30, 0.075 to 0.075, 1, "F", "F"),
                VControl.Button("k_e", 0.86 to 0.38, 0.075 to 0.075, 1, "E", "E"),
                VControl.Button("k_r", 0.92 to 0.34, 0.075 to 0.075, 1, "R", "R"),
                VControl.Button("k_c", 0.88 to 0.50, 0.075 to 0.075, 1, "C", "C"),
                VControl.Button("k_space", 0.78 to 0.60, 0.12 to 0.12, 1, "空", "Space"),
                VControl.Button("k_shift", 0.86 to 0.70, 0.08 to 0.08, 1, "Shift", "Shift"),
            ))
            "moba" -> ControlsLayout("moba", 45, listOf(
                VControl.Button("k_q", 0.80 to 0.32, 0.085 to 0.085, 1, "Q", "Q"),
                VControl.Button("k_w", 0.88 to 0.28, 0.085 to 0.085, 1, "W", "W"),
                VControl.Button("k_e", 0.96 to 0.32, 0.085 to 0.085, 1, "E", "E"),
                VControl.Button("k_r", 0.90 to 0.42, 0.085 to 0.085, 1, "R", "R"),
                VControl.Button("k_d", 0.82 to 0.44, 0.085 to 0.085, 1, "D", "D"),
                VControl.Button("k_f", 0.74 to 0.40, 0.085 to 0.085, 1, "F", "F"),
                VControl.Button("k_b", 0.96 to 0.52, 0.075 to 0.075, 1, "B", "B"),
                VControl.Button("k_space", 0.78 to 0.62, 0.12 to 0.12, 1, "空", "Space"),
            ))
            "game3d" -> ControlsLayout("game3d", 45, listOf(
                VControl.Joystick("stick_move", 0.13 to 0.73, 0.21 to 0.21, 1, 0.30, 150L,
                    listOf("W", "D", "S", "A")),
                VControl.Button("k_space", 0.78 to 0.60, 0.12 to 0.12, 1, "空", "Space"),
                VControl.Button("k_esc", 0.96 to 0.10, 0.07 to 0.07, 1, "Esc", "Escape"),
            ))
            else -> ControlsLayout("desktop", 45, emptyList())
        }

        /** JSON 解析（未来编辑态/导入用；A 档走内置 preset）。 */
        fun fromJson(text: String): ControlsLayout? {
            return try {
                val root = JSONObject(text)
                val arr = root.optJSONArray("controls")
                    ?: return ControlsLayout(root.optString("layoutPreset", "desktop"), root.optInt("opacity", 85), emptyList())
            val list = ArrayList<VControl>()
            for (i in 0 until arr.length()) {
                val o = arr.getJSONObject(i)
                val id = o.optString("id")
                val an = o.optJSONArray("anchor")
                val sz = o.optJSONArray("size")
                val anchor = (an.optDouble(0) to an.optDouble(1))
                val size = (sz.optDouble(0) to sz.optDouble(1))
                val layer = o.optInt("layer", 1)
                when (o.optString("type")) {
                    "button" -> list.add(VControl.Button(id, anchor, size, layer,
                        o.optString("label", id),
                        o.optJSONObject("mapping")?.optString("key") ?: ""))
                    "joystick" -> list.add(VControl.Joystick(id, anchor, size, layer,
                        o.optDouble("deadzone", 0.30),
                        o.optLong("stickyMs", 150L),
                        o.optJSONObject("mapping")?.optJSONArray("keys")?.let { k ->
                            (0 until k.length()).map { j -> k.optString(j) }
                        } ?: listOf("W", "D", "S", "A")))
                }
            }
            ControlsLayout(root.optString("layoutPreset", "custom"), root.optInt("opacity", 85), list)
            } catch (_: Exception) { null }
        }
    }
}