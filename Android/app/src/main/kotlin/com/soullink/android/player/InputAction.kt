package com.soullink.android.player

/**
 * 输入动作定义。字段对齐 /api/input 的 JSON body（Relay/AgentWebServer.cs）。
 * 动作已全部在后端接线：click/down/up/move/wheel/keydown/keyup/text/moverel/releaseall/noop。
 */
sealed class InputAction {
    abstract val action: String

    data class Click(val x: Double, val y: Double, val button: String = "left") : InputAction() {
        override val action: String get() = "click"
    }
    data class Down(val x: Double, val y: Double, val button: String = "left") : InputAction() {
        override val action: String get() = "down"
    }
    data class Up(val button: String = "left") : InputAction() {
        override val action: String get() = "up"
    }
    data class Move(val x: Double, val y: Double) : InputAction() {
        override val action: String get() = "move"
    }
    data class MoveRel(val dx: Int, val dy: Int) : InputAction() {
        override val action: String get() = "moverel"
    }
    data class Wheel(val delta: Int) : InputAction() {
        override val action: String get() = "wheel"
    }
    object Noop : InputAction() {
        override val action: String get() = "noop"
    }
    object ReleaseAll : InputAction() {
        override val action: String get() = "releaseall"
    }
}
