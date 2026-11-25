package com.soullink.android.ui.player

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.text.drawText
import androidx.compose.ui.unit.dp
import com.soullink.android.player.ControlsLayout
import com.soullink.android.player.InputAction
import com.soullink.android.player.InputSender
import com.soullink.android.player.VControl
import com.soullink.android.player.JoystickLogic
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * 虚拟按键层（T41d A 档）：Canvas 自绘 + pointerInput 命中/consume/透传。
 *
 * ★ 层序契约（消费感知落地指令）：本层放 TouchGestureLayer **之后**（兄弟节点，
 *   代码顺序 = z 上层）——Compose Main pass 子→父冒泡：后绘制者先收事件。
 *   命中控件 → change.consume()（手势层 isConsumed 过滤触发，整组作废）；
 *   未命中 → 不 consume，透传给手势层（空白区仍可拖光标/呼出工具栏）。
 * ★ 多指独立：pointerId → 控件 id 的映射（摇杆按住 + 按钮同时按）。
 * ★ 摇杆 8 方向：死区 30%（居中不发键）；方向切换先 KeyUp 旧再 KeyDown 新；
 *   粘滞 150ms 防抖；回中 KeyUp 全部 4 方向键（否则角色一直走）。
 */
@Composable
fun VirtualControlsLayer(
    layout: ControlsLayout,
    inputSender: InputSender,
    modifier: Modifier = Modifier,
) {
    if (layout.controls.isEmpty()) return   // desktop 预设：无控件不渲染
    val opacity = layout.opacity.coerceIn(10, 100) / 100f

    // 按钮按下态（id → pressed）与摇杆头偏移（id → 归一化偏移）
    val pressedIds = remember { mutableStateOf(setOf<String>()) }
    val stickOffsets = remember { mutableStateOf(mapOf<String, Offset>()) }
    Box(
        modifier = modifier
            .pointerInput(layout) {
                awaitEachGesture {
                    val down = awaitFirstDown(requireUnconsumed = false)
                    val nx = down.position.x / size.width.toDouble()
                    val ny = down.position.y / size.height.toDouble()
                    val hit = layout.controls.filter { c ->
                        val hw = c.size.first / 2; val hh = c.size.second / 2
                        nx >= c.anchor.first - hw && nx <= c.anchor.first + hw &&
                        ny >= c.anchor.second - hh && ny <= c.anchor.second + hh
                    }.maxByOrNull { it.layer }
                    if (hit == null) return@awaitEachGesture
                    down.consume()
                    val pid = down.id
                    when (hit) {
                        is VControl.Button -> {
                            pressedIds.value = pressedIds.value + hit.id
                            inputSender.sendNoWait(InputAction.KeyDown(hit.key))
                        }
                        is VControl.Joystick -> {
                            stickOffsets.value = stickOffsets.value + (hit.id to Offset(0f, 0f))
                        }
                    }
                    var lastDir = -1
                    var lastDirAt = 0L
                    while (true) {
                        val ev = awaitPointerEvent()
                        val ch = ev.changes.firstOrNull { it.id == pid } ?: break
                        if (!ch.pressed) {
                            ch.consume()
                            when (hit) {
                                is VControl.Button -> {
                                    pressedIds.value = pressedIds.value - hit.id
                                    inputSender.sendNoWait(InputAction.KeyUp(hit.key))
                                }
                                is VControl.Joystick -> {
                                    stickOffsets.value = stickOffsets.value - hit.id
                                    hit.keys.forEach { k -> inputSender.sendNoWait(InputAction.KeyUp(k)) }
                                }
                            }
                            break
                        }
                        ch.consume()
                        if (hit is VControl.Joystick) {
                            val cxx = hit.anchor.first; val cyy = hit.anchor.second
                            val ox = (ch.position.x / size.width.toDouble() - cxx) / (hit.size.first / 2)
                            val oy = (ch.position.y / size.height.toDouble() - cyy) / (hit.size.second / 2)
                            val len = kotlin.math.hypot(ox, oy)
                            val cl = if (len > 1.0) 1.0 else len
                            val kx = if (len > 0) ox / len * cl else 0.0
                            val ky = if (len > 0) oy / len * cl else 0.0
                            stickOffsets.value = stickOffsets.value + (hit.id to Offset(kx.toFloat(), ky.toFloat()))
                            val dir: Int = if (len < hit.deadzone) -1 else JoystickLogic.dirFromAngle(Math.toDegrees(kotlin.math.atan2(ky, kx)))
                            val now = System.currentTimeMillis()
                            if (dir != lastDir && (dir == -1 || now - lastDirAt >= hit.stickyMs)) {
                                if (lastDir >= 0) inputSender.sendNoWait(InputAction.KeyUp(hit.keys[JoystickLogic.dirKeyIndex(lastDir)]))
                                if (dir >= 0) inputSender.sendNoWait(InputAction.KeyDown(hit.keys[JoystickLogic.dirKeyIndex(dir)]))
                                lastDir = dir
                                lastDirAt = now
                            }
                        }
                    }
                }
            }
    )

    // 渲染（T41i）：TextMeasurer drawText 画散落圆底+白字标签（半透明透画面，按下更亮）
    val textMeasurer = rememberTextMeasurer()
    Canvas(modifier = Modifier.fillMaxSize()) {
        val alpha = opacity
        layout.controls.forEach { c ->
            if (c is VControl.Button) {
                val cx = (c.anchor.first * size.width).toFloat()
                val cy = (c.anchor.second * size.height).toFloat()
                val w = (c.size.first * size.width).toFloat()
                val h = (c.size.second * size.height).toFloat()
                val pressed = c.id in pressedIds.value
                val r = (minOf(w, h) / 2f) * (if (pressed) 0.92f else 1f)
                drawCircle(color = Color(0xFF1A1A2E).copy(alpha = 0.55f * alpha), radius = r, center = Offset(cx, cy))
                drawCircle(color = Color.White.copy(alpha = (if (pressed) 0.75f else 0.3f) * alpha), radius = r, center = Offset(cx, cy), style = Stroke(2f))
                // ★ 标签白字（居中；按下加亮）
                val style = androidx.compose.ui.text.TextStyle(
                    color = Color.White.copy(alpha = (if (pressed) 1f else 0.75f) * alpha),
                    fontSize = with(this) { (r * 0.55f).toSp() },
                )
                val measured = textMeasurer.measure(c.label, style)
                drawText(
                    textLayoutResult = measured,
                    topLeft = Offset(cx - measured.size.width / 2f, cy - measured.size.height / 2f),
                )
            } else if (c is VControl.Joystick) {
                // ★ P0-4 摇杆轮盘视觉（用户定案「轮盘操作」）：半透明圆环底座 +
                //   WASD 四方向字标 + 摇杆头圆点跟随
                val cx = (c.anchor.first * size.width).toFloat()
                val cy = (c.anchor.second * size.height).toFloat()
                val w = (c.size.first * size.width).toFloat()
                val br = w / 2f
                drawCircle(color = Color(0xFF1A1A2E).copy(alpha = 0.45f * alpha), radius = br, center = Offset(cx, cy))
                drawCircle(color = Color.White.copy(alpha = 0.25f * alpha), radius = br, center = Offset(cx, cy), style = Stroke(2f))
                // WASD 四方向字标（固定位置，Moonlight AnalogStick 同款语义）
                val keyStyle = androidx.compose.ui.text.TextStyle(
                    color = Color.White.copy(alpha = 0.8f * alpha),
                    fontSize = with(this) { (br * 0.28f).toSp() },
                )
                val dirs = listOf(
                    c.keys[0] to Offset(cx, cy - br * 0.6f),   // W 上
                    c.keys[3] to Offset(cx - br * 0.6f, cy),   // A 左
                    c.keys[2] to Offset(cx, cy + br * 0.6f),   // S 下
                    c.keys[1] to Offset(cx + br * 0.6f, cy),   // D 右
                )
                dirs.forEach { (label, pos) ->
                    val m = textMeasurer.measure(label, keyStyle)
                    drawText(textLayoutResult = m, topLeft = Offset(pos.x - m.size.width / 2f, pos.y - m.size.height / 2f))
                }
                // 摇杆头（跟随拖动偏移；头径 = 底座 40%）
                val off = stickOffsets.value[c.id] ?: Offset.Zero
                val hr = br * 0.4f
                drawCircle(color = Color.White.copy(alpha = 0.65f * alpha), radius = hr, center = Offset(cx + off.x * br, cy + off.y * br))
            }
        }
    }
}

// 摇杆纯逻辑已抽到 JoystickLogic（可单测）；本层直接调用。

// 方向 → 主键索引（keys=[上,右,下,左]=W/D/S/A；斜角归主方向，双键下轮）

