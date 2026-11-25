package com.soullink.android.player

/**
 * 输入动作模型：与 /api/input 的 JSON 契约一一对应。
 * T36：补 key/keydown/keyup/text；T36b：Up 带坐标（拖动收尾不丢最终位置）。
 */
sealed class InputAction {
    abstract val action: String

    data class Click(val x: Double, val y: Double, val button: String = "left") : InputAction() {
        override val action: String get() = "click"
    }

    data class Down(val x: Double, val y: Double, val button: String = "left") : InputAction() {
        override val action: String get() = "down"
    }

    data class Up(val x: Double = -1.0, val y: Double = -1.0, val button: String = "left") : InputAction() {
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

    data class Key(val key: String, val count: Int = 1) : InputAction() {
        override val action: String get() = "key"
    }

    data class KeyDown(val key: String) : InputAction() {
        override val action: String get() = "keydown"
    }

    data class KeyUp(val key: String) : InputAction() {
        override val action: String get() = "keyup"
    }

    data class Text(val text: String) : InputAction() {
        override val action: String get() = "text"
    }

    object Noop : InputAction() {
        override val action: String get() = "noop"
    }

    object ReleaseAll : InputAction() {
        override val action: String get() = "releaseall"
    }
}