package com.soullink.android.ui.player

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.dp
import com.soullink.android.player.InputAction
import com.soullink.android.player.InputSender
import com.soullink.android.player.VMouseLogic

/**
 * 虚拟鼠标手柄 v2（T45 修「形状丑+用不了」——Lead 截图定位三缺陷）：
 * · ★ 坐标系统一：渲染 Canvas 全屏 + 命中用同一 padX/padY 绝对中心——旧版渲染 Box
 *   offset 与命中判定两套坐标（首次 padX=-1 时渲染(0,0)/命中 0.60 屏宽）= 错位根因；
 * · ★ 形状照向日葵 v0044：左右**月牙弧**（drawArc，不是正圆）+ 中圆 + 顶圆（滚轮）；
 * · ★ 尺寸 0.22 屏宽（旧 0.25 偏大）；默认位 0.62/0.72。
 */
@Composable
fun VirtualMousePad(
    inputSender: InputSender,
    enabled: Boolean,
    modifier: Modifier = Modifier,
) {
    if (!enabled) return
    val density = LocalDensity.current
    val context = androidx.compose.ui.platform.LocalContext.current
    val padD = with(density) { 150.dp.toPx() }          // 控件直径（≈0.22 屏宽）
    val ballR = padD * 0.18f                            // 中圆半径（拖动区）
    val wheelR = padD * 0.10f                           // 顶圆半径（滚轮）
    var padX by remember { mutableStateOf(-1f) }        // 控件中心绝对坐标（-1=未初始化）
    var padY by remember { mutableStateOf(-1f) }
    var pressedArea by remember { mutableStateOf("") }

    LaunchedEffect(Unit) {
        try {
            val sp = context.getSharedPreferences("soullink_login", android.content.Context.MODE_PRIVATE)
            padX = sp.getFloat("vmouseX", -1f)
            padY = sp.getFloat("vmouseY", -1f)
        } catch (_: Exception) { }
    }

    // 全屏 Canvas：以 padX/padY 为绝对中心绘制（命中判定用同一坐标）
    Canvas(modifier = modifier.fillMaxSize()) {
        val cx = if (padX <= 0f) size.width * 0.62f else padX
        val cy = if (padY <= 0f) size.height * 0.72f else padY
        val bodyAlpha = 0.55f
        val ringR = padD / 2f
        // 外圈细环（替代向日葵点环）
        drawCircle(color = Color(0xFF1A1A2E).copy(alpha = bodyAlpha), radius = ringR, center = Offset(cx, cy))
        drawCircle(color = Color.White.copy(alpha = 0.25f), radius = ringR, center = Offset(cx, cy), style = Stroke(1.5f))
        // ★ 左右月牙弧（drawArc 环形扇段——左键/右键；按下变粉）
        val arcColor = { pressed: Boolean -> if (pressed) Color(0xCCFF6B8B) else Color(0xFFE0E0E0).copy(alpha = bodyAlpha) }
        val arcStroke = padD * 0.11f
        val arcRect = Size(ringR * 1.6f, ringR * 1.6f)
        val topLeft = Offset(cx - arcRect.width / 2f, cy - arcRect.height / 2f)
        drawArc(
            color = arcColor(pressedArea == "left"),
            startAngle = 115f, sweepAngle = 130f, useCenter = false,
            topLeft = topLeft, size = arcRect,
            style = Stroke(width = arcStroke),
        )
        drawArc(
            color = arcColor(pressedArea == "right"),
            startAngle = -65f, sweepAngle = 130f, useCenter = false,
            topLeft = topLeft, size = arcRect,
            style = Stroke(width = arcStroke),
        )
        // 中圆（拖动区；按下变粉）
        val midColor = if (pressedArea == "mid") Color(0xCCFF6B8B) else Color(0xFFE0E0E0).copy(alpha = bodyAlpha)
        drawCircle(color = midColor, radius = ballR, center = Offset(cx, cy))
        drawCircle(color = Color.White.copy(alpha = 0.5f), radius = ballR, center = Offset(cx, cy), style = Stroke(1.5f))
        // 顶圆（滚轮；按下变粉）
        val wheelColor = if (pressedArea == "wheel") Color(0xCCFF6B8B) else Color(0xFFE0E0E0).copy(alpha = bodyAlpha)
        drawCircle(color = wheelColor, radius = wheelR, center = Offset(cx, cy - ringR - wheelR * 0.4f))
    }

    // 交互层：命中判定与渲染用**同一套几何**（cx/cy/ringR/ballR/wheelR 完全一致）
    androidx.compose.foundation.layout.Box(modifier = Modifier.fillMaxSize().pointerInput(padX, padY, inputSender) {
        awaitEachGesture {
            val down = awaitFirstDown(requireUnconsumed = false)
            val cx = if (padX <= 0f) size.width * 0.62f else padX
            val cy = if (padY <= 0f) size.height * 0.72f else padY
            val dx = down.position.x - cx
            val dy = down.position.y - cy
            val dist = kotlin.math.hypot(dx, dy)
            val area = when {
                // 顶圆（滚轮）：控件上缘（cy - padD/2 - padD*0.10*0.4 为圆心）
                kotlin.math.hypot(dx, dy + padD / 2f + padD * 0.10f * 0.4f) <= padD * 0.10f + 20f -> "wheel"
                // 中圆（拖动区）
                dist <= ballR + 16f -> "mid"
                // 左弧：dx<0 且在弧环带内（ringR = padD/2）
                dx < 0 && dist in (padD * 0.25f)..(padD * 0.575f) -> "left"
                // 右弧
                dx > 0 && dist in (padD * 0.25f)..(padD * 0.575f) -> "right"
                else -> null
            }
            // ★ T45 命中诊断日志（Lead 要求：看命中的是哪个区域）
            android.util.Log.i("VMouseHit", "hit area=$area at (${down.position.x.toInt()},${down.position.y.toInt()}) pad=($cx.toInt(),$cy.toInt())")
            if (area == null) return@awaitEachGesture
            down.consume()
            pressedArea = area

            var dragStarted = false
            var movedPx = 0f
            val downAt = System.currentTimeMillis()
            var lastX = down.position.x
            var lastY = down.position.y
            var wheelAccumPx = 0f
            val wheelStepPx = 8f * density.density

            while (true) {
                val ev = awaitPointerEvent()
                val ch = ev.changes.firstOrNull { it.id == down.id } ?: break
                if (!ch.pressed) {
                    ch.consume()
                    val elapsed = System.currentTimeMillis() - downAt
                    val nx = VMouseLogic.offsetToNorm(if (padX <= 0f) size.width * 0.62f else padX, size.width.toFloat())
                    val ny = VMouseLogic.offsetToNorm(if (padY <= 0f) size.height * 0.72f else padY, size.height.toFloat())
                    when (area) {
                        "left" -> if (!dragStarted && VMouseLogic.isTap(movedPx, elapsed, 12f * density.density)) {
                            inputSender.sendNoWait(InputAction.Click(nx, ny, "left"))
                        } else if (dragStarted) inputSender.sendNoWait(InputAction.Up(nx, ny, "left"))
                        "right" -> if (VMouseLogic.isTap(movedPx, elapsed, 12f * density.density)) {
                            inputSender.sendNoWait(InputAction.Click(nx, ny, "right"))
                        }
                    }
                    pressedArea = ""
                    break
                }
                ch.consume()
                val mdx = ch.position.x - lastX
                val mdy = ch.position.y - lastY
                lastX = ch.position.x; lastY = ch.position.y
                movedPx += kotlin.math.hypot(mdx, mdy)
                when (area) {
                    "mid" -> {
                        if (padX <= 0f) { padX = size.width * 0.62f; padY = size.height * 0.72f }
                        padX = VMouseLogic.clampOffset(padX + mdx, padD / 2f, size.width.toFloat())
                        padY = VMouseLogic.clampOffset(padY + mdy, padD / 2f, size.height.toFloat())
                        inputSender.sendNoWait(InputAction.Move(
                            VMouseLogic.offsetToNorm(padX, size.width.toFloat()),
                            VMouseLogic.offsetToNorm(padY, size.height.toFloat())))
                    }
                    "left" -> {
                        if (!dragStarted && movedPx >= 12f * density.density) {
                            dragStarted = true
                            inputSender.sendNoWait(InputAction.Down(nx0(padX, size.width), ny0(padY, size.height)))
                        }
                        if (dragStarted) inputSender.sendNoWait(InputAction.Move(
                            VMouseLogic.offsetToNorm((if (padX <= 0f) size.width * 0.62f else padX) + ch.position.x - down.position.x, size.width.toFloat()),
                            VMouseLogic.offsetToNorm((if (padY <= 0f) size.height * 0.72f else padY) + ch.position.y - down.position.y, size.height.toFloat())))
                    }
                    "wheel" -> {
                        wheelAccumPx += mdy
                        val (steps, remain) = VMouseLogic.wheelAccum(wheelAccumPx, wheelStepPx)
                        if (steps != 0) { inputSender.sendNoWait(InputAction.Wheel(steps * 120)); wheelAccumPx = remain }
                    }
                }
            }
            try {
                context.getSharedPreferences("soullink_login", android.content.Context.MODE_PRIVATE)
                    .edit().putFloat("vmouseX", padX).putFloat("vmouseY", padY).apply()
            } catch (_: Exception) { }
        }
    })
}

private fun nx0(padX: Float, w: Int) = VMouseLogic.offsetToNorm(if (padX <= 0f) w * 0.62f else padX, w.toFloat())
private fun ny0(padY: Float, h: Int) = VMouseLogic.offsetToNorm(if (padY <= 0f) h * 0.72f else padY, h.toFloat())