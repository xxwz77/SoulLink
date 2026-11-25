package com.soullink.android.ui.player

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.background
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.size
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.wrapContentSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.Switch
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.layout.boundsInParent
import androidx.compose.ui.geometry.Rect
import android.view.SurfaceHolder
import com.soullink.android.net.SoulLinkApi
import com.soullink.android.net.dto.SessionDto
import com.soullink.android.player.AspectSurfaceView
import com.soullink.android.player.H264Player
import com.soullink.android.player.InputAction
import com.soullink.android.player.InputSender
import com.soullink.android.player.LookThrottler
import com.soullink.android.player.MoveThrottler
import com.soullink.android.player.PlayerConstants
import com.soullink.android.player.PlayerState
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import okhttp3.OkHttpClient
import java.util.concurrent.TimeUnit

/** 输入模式（T41e 用户定案）：唯一区别 = 有无虚拟按键层；光标/鼠标逻辑两模式完全一致。 */
enum class InputMode { DESKTOP, GAME }

/**
 * Player page v2 (T41b mobile form-factor redo):
 * zero persistent UI (fullscreen content), gesture-driven, transient floating toolbar.
 */
@Composable
fun PlayerPlaceholderScreen(
    host: String,
    session: SessionDto,
    api: SoulLinkApi,
    directToken: String,
    onBack: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    var playerState by remember { mutableStateOf(PlayerState.CONNECTING) }
    var errorText by remember { mutableStateOf<String?>(null) }
    var player by remember { mutableStateOf<H264Player?>(null) }
    var toolbarVisible by remember { mutableStateOf(false) }

    val context = androidx.compose.ui.platform.LocalContext.current
    val prefs = remember { context.getSharedPreferences("soullink_login", android.content.Context.MODE_PRIVATE) }

    var mouseMode by remember { mutableStateOf(prefs.getString("mouseMode", "relative") ?: "relative") }
    val isRelative = mouseMode == "relative"
    var stretch by remember { mutableStateOf(prefs.getBoolean("stretchVideo", false)) }
    var orientationMode by remember { mutableStateOf(0) }
    var videoAspect by remember { mutableStateOf(0.0) }
    // S5/S6：视频实际显示矩形（onGloballyPositioned boundsInParent，Box 坐标系）。
    // 等比模式 = AspectSurfaceView 收缩后的真实区域；拉伸 = 全屏。光标与触摸共用。
    var videoRect by remember { mutableStateOf(Rect(0f, 0f, 0f, 0f)) }
    // P0-1 虚拟鼠标：低延迟光标回显（服务端 cursor 回传 + 本地相对累积，双源）
    var cursorNx by remember { mutableStateOf(0.5) }
    var cursorNy by remember { mutableStateOf(0.5) }
    var cursorVisible by remember { mutableStateOf(false) }
    // A 影子光标状态：pressed（按下变蓝）。T41g：**常驻显示**（用户「虚拟鼠标=始终
    // 可见」；8 秒收起是误读 RustDesk 的手柄收起，删）。visible=false 或视频帧内嵌光标
    // 接管时自然不画。
    var cursorPressed by remember { mutableStateOf(false) }
    // P0-2 键盘：showKbd=true 时呼出软键盘 + 悬浮输入框（文字走 InputAction.Text）
    var showKbd by remember { mutableStateOf(false) }
    var kbdText by remember { mutableStateOf("") }
    // T41e 输入模式：DESKTOP（默认，无按键层）/ GAME（按键层 + 虚拟鼠标共存——用户定案
    // 「游戏模式也要有虚拟鼠标」：唯一区别是有无按键层，光标/空白拖动两模式一致）
    var inputMode by remember { mutableStateOf(if (prefs.getString("inputMode", "desktop") == "game") InputMode.GAME else InputMode.DESKTOP) }
    // T41g 缩放（用户「没有缩放画面的能力」）：1.0~4.0，双指捏合改变，双击复位。
    var zoom by remember { mutableStateOf(1f) }
    var zoomPanX by remember { mutableStateOf(0f) }
    var zoomPanY by remember { mutableStateOf(0f) }
    // T41h 图标列淡出态（点空白切换 75%↔30%，不消失）
    var iconsDimmed by remember { mutableStateOf(false) }
    // T41h ⚙️ 更多小面板（等比/拉伸、缩放倍率、断开）
    var morePanel by remember { mutableStateOf(false) }
    // P0-1 底部抽屉菜单（向日葵 v0194/v0205 同构 7 组；☰ 弹出，⌄ 收起）
    var drawerOpen by remember { mutableStateOf(false) }
    // P0-3 预留：影子光标显式开关（默认开——Bug2 解耦 CURSOR_SHOWING；逻辑 P0-3 接）
    var shadowCursor by remember { mutableStateOf(true) }
    // 虚拟鼠标手柄开关（默认开——用户正在用它；与影子光标独立）
    var vmouseEnabled by remember { mutableStateOf(prefs.getBoolean("vmouseEnabled", true)) }
    // P0-3 首次 cursor 回传日志标志
    var cursorEchoLogged by remember { mutableStateOf(false) }

    val streamUrl = api.streamUrl(session.directPort, directToken)
    val idrUrl = api.idrUrl(session.directPort)
    val inputBase = "http://" + host + ":" + session.directPort

    val inputSender = remember(inputBase, directToken, session.sessionId) {
        InputSender(client = defaultInputClient(), baseUrl = inputBase, token = directToken, sessionId = session.sessionId.toString()).also { s ->
            s.onCursor = { nx, ny, visible ->
                // 服务端实测回传（输入 RTT 级低延迟）：主线程更新。T41g：常驻显示，
                // visible=false（光标隐藏/出抓屏区）时不画。
                if (!cursorEchoLogged) {
                    cursorEchoLogged = true
                    android.util.Log.i("SoulLinkCursor", "首次 cursor 回传 nx=$nx ny=$ny visible=$visible")
                }
                scope.launch {
                    cursorNx = nx
                    cursorNy = ny
                    cursorVisible = visible
                }
            }
        }
    }

    val moveThrottler = remember(inputSender) {
        MoveThrottler(scope) { nx, ny -> inputSender.sendNoWait(InputAction.Move(nx, ny)) }
    }
    val lookThrottler = remember(inputSender) {
        LookThrottler(scope) { dx, dy -> if (dx != 0 || dy != 0) inputSender.sendNoWait(InputAction.MoveRel(dx, dy)) }
    }

    LaunchedEffect(toolbarVisible) {
        if (toolbarVisible) {
            delay(4000)
            toolbarVisible = false
        }
    }

    BackHandler { onBack() }

    Box(Modifier.fillMaxSize().background(Color.Black)) {

        AndroidView(
            factory = { ctx ->
                AspectSurfaceView(ctx).apply {
                    holder.addCallback(object : SurfaceHolder.Callback {
                        override fun surfaceCreated(holder: SurfaceHolder) {
                            if (player == null) {
                                player = H264Player(
                                    streamUrl = streamUrl,
                                    token = directToken,
                                    surface = holder.surface,
                                    scope = scope,
                                    idrUrl = idrUrl,
                                    client = defaultStreamClient(),
                                ).also { p ->
                                    p.onState = { playerState = it }
                                    p.onError = { errorText = it.message ?: "播放错误" }
                                    // ★ T41g 修「默认铺满」根因：onFrame(w,h) → videoAspect。
                                    //   之前 videoAspect 声明了但没人写 → desiredAspectRatio
                                    //   恒 0 = 永远铺满拉伸。现在首帧即设比例 → 等比生效
                                    //   （stretch=false 默认，对齐 DEFAULT_STRETCH=false）。
                                    p.onFrame = { w, h ->
                                        if (w > 0 && h > 0) videoAspect = w.toDouble() / h.toDouble()
                                    }
                                    p.start()
                                }
                            }
                        }
                        override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {}
                        override fun surfaceDestroyed(holder: SurfaceHolder) {
                            player?.stop()
                            player = null
                        }
                    })
                }
            },
            update = { v ->
                v.desiredAspectRatio = if (stretch || videoAspect <= 0.0) 0.0 else videoAspect
                // T41g 缩放：View 属性中心缩放（pivot 默认中心）
                v.scaleX = zoom
                v.scaleY = zoom
            },
            modifier = Modifier.wrapContentSize(Alignment.Center).onGloballyPositioned { coords ->
                // ★ T44 修等比（真根因）：fillMaxSize 会把布局槽位锁成全屏，
                //   wrapContentSize 排在它后面只影响子元素摆放——onMeasure 收到的
                //   仍是填满约束，收缩结果被丢弃。**只用 wrapContentSize**：槽位由
                //   AspectSurfaceView.onMeasure 自己决定（等比收缩生效）。
                videoRect = coords.boundsInParent()
            },
        )

        TouchGestureLayer(
            videoRect = videoRect,
            zoom = zoom,
            onTapToolbar = {
                toolbarVisible = !toolbarVisible
                if (toolbarVisible) iconsDimmed = false else iconsDimmed = !iconsDimmed   // 弹出亮、收起淡出
            },
            onZoom = { factor -> zoom = (zoom * factor).coerceIn(1f, 4f) },
            onResetZoom = { zoom = 1f },
            onLookDelta = { dx, dy ->
                if (isRelative) lookThrottler.add(dx, dy)
                // P0-1 光标回显：moverel 走服务端回传（RTT ~10-30ms 内网，可用）；
                // v2 再加本地零延迟累积（需视频矩形换算，见 S5/S6 同批）
            },
            onDragStart = { nx, ny ->
                cursorPressed = true
                if (!isRelative) inputSender.sendNoWait(InputAction.Down(nx, ny))
            },
            onDragMove = { nx, ny -> if (!isRelative) moveThrottler.add(nx, ny) },
            onDragEnd = { nx, ny ->
                cursorPressed = false
                if (!isRelative) {
                    moveThrottler.flushNow()   // ★ T41g：先发压着的最终位置，再发 up——
                    //   防「up 带的终坐标被 8ms 窗口里迟到的旧 move 覆盖」（快速拖动落点偏移根因）
                    inputSender.sendNoWait(InputAction.Up(nx, ny))
                }
            },
            onClick = { nx, ny, button ->
                scope.launch { inputSender.send(InputAction.Click(nx, ny, button)) }
                Unit
            },
            onWheel = { delta -> inputSender.sendNoWait(InputAction.Wheel(delta)) },
            modifier = Modifier.fillMaxSize(),
        )

        // ★ T41e 虚拟按键层（仅 GAME 模式；放在手势层**之后** = z 上层先收，命中 consume，
        //   空白透传给手势层——虚拟鼠标与按键层共存（用户定案））。
        //   预设：A 档先固定 fps（跳/蹲/弹/火 + WASD 摇杆）；预设选择下拉下轮。
        if (inputMode == InputMode.GAME) {
            com.soullink.android.player.ControlsLayout.preset("fps").let { controls ->
                VirtualControlsLayer(
                    layout = controls,
                    inputSender = inputSender,
                    modifier = Modifier.fillMaxSize(),
                )
            }
        }

        // P0-1 虚拟鼠标光标（对齐网页端 rcCursor 十字准星：竖线2x14+横线14x2）。
        // Canvas 无 pointerInput → 不消费触摸。TODO(S5/S6)：等比模式按 videoRect 换算。
        // ★ T41g 菜单入口③：悬浮球（常驻右下角半透明圆点——最不会丢的入口）。
        //   视觉提示「点这里有菜单」；点它=点屏幕=tap→工具栏（手势层全屏覆盖）。
        androidx.compose.foundation.Canvas(modifier = Modifier.fillMaxSize()) {
            val bx = size.width - 60f
            val by = size.height - 60f
            drawCircle(color = Color(0x88000000), radius = 22f, center = androidx.compose.ui.geometry.Offset(bx, by))
            drawCircle(color = Color(0xCCFFFFFF), radius = 20f, center = androidx.compose.ui.geometry.Offset(bx, by))
            drawCircle(color = Color(0x882196F3), radius = 9f, center = androidx.compose.ui.geometry.Offset(bx, by))
        }

        // ★ T41g 虚拟鼠标（用户定案：**常驻可见**的鼠标形态）——箭头 + 按下变蓝 + 涟漪。
        //   位置=videoRect 换算（服务端 cursor 回传驱动，输入 RTT 级）；visible=false
        //   （系统隐藏光标，如 3D 游戏全屏）时不画。8 秒收起已删（误读 RustDesk 手柄机制）。
        // ★ P0-3 光标常驻：shadowCursor 开=无视 visible 常驻绘制（visible=false 半透明——
        //   Bug2 解耦 CURSOR_SHOWING，游戏全屏不再消失）；shadowCursor 关=完全不画。
        if (shadowCursor) {
            androidx.compose.foundation.Canvas(modifier = Modifier.fillMaxSize()) {
                // ★ 光标形态=箭头（Lead 00:30 指令③：原生应用用箭头，十字是网页无资源时的选择）
                val cx = videoRect.left + videoRect.width / 2f + (cursorNx.toFloat() - 0.5f) * videoRect.width * zoom
                val cy = videoRect.top + videoRect.height / 2f + (cursorNy.toFloat() - 0.5f) * videoRect.height * zoom
                val path = androidx.compose.ui.graphics.Path().apply {
                    // ★ T41g 标准箭头（用户「样式不好看」修法）：Windows 经典轮廓
                    //   （24x24 网格比例），按 1.3x 缩放绘制，对称规整。
                    val s = 1.3f
                    moveTo(cx, cy)                      // 顶点
                    lineTo(cx + 0f * s, cy + 17.5f * s)  // 左斜边直落
                    lineTo(cx + 4.6f * s, cy + 13.0f * s) // 左下内凹
                    lineTo(cx + 7.3f * s, cy + 19.2f * s) // 尾巴外斜
                    lineTo(cx + 9.8f * s, cy + 18.1f * s) // 尾巴内斜
                    lineTo(cx + 7.1f * s, cy + 12.1f * s) // 尾根回收
                    lineTo(cx + 12.1f * s, cy + 12.1f * s) // 右斜边内凹点
                    close()
                }
                // 黑描边 + 主体（按下蓝 0.7 / 常态灰 0.7；P0-3：visible=false 时再减半——
                //   半透明而非消失，游戏里仍能感知光标位置）
                val ghost = if (cursorVisible) 1f else 0.5f
                drawPath(path, color = Color(0x99000000).copy(alpha = 0.6f * ghost), style = androidx.compose.ui.graphics.drawscope.Stroke(width = 2.5f))
                drawPath(path, color = if (cursorPressed) Color(0xB32196F3) else Color(0xB3E0E0E0).copy(alpha = 0.7f * ghost))
                // 点击涟漪（按压态外圈扩散环——静态双圈，动画下批）
                if (cursorPressed) {
                    drawCircle(color = Color(0x662196F3), radius = 18f, center = androidx.compose.ui.geometry.Offset(cx, cy))
                }
            }
        }

        // ★ 虚拟鼠标手柄（用户三次澄清定案）：拖中圆=移控件+光标一体；
        //   左弧=左键（按住+拖=真拖拽，超阈值才 down 防三连击）；右弧=右键；顶圆=滚轮。
        //   位置持久化 vmouseX/vmouseY。默认开（用户正在用它）。
        VirtualMousePad(
            inputSender = inputSender,
            enabled = vmouseEnabled,
            modifier = Modifier.fillMaxSize(),
        )

        // ★ T41h 向日葵式右侧图标列：常驻右缘垂直排，一图标一功能，半透明黑底白图标
        //   44dp 圆钮；只占窄条宽（图标外画面触摸不受拦截）；点空白淡出至 30% 不消失。

        androidx.compose.foundation.layout.Column(
            horizontalAlignment = androidx.compose.ui.Alignment.CenterHorizontally,
            verticalArrangement = androidx.compose.foundation.layout.Arrangement.spacedBy(10.dp),
            modifier = Modifier
                .align(androidx.compose.ui.Alignment.CenterEnd)
                .padding(end = 10.dp),
        ) {
            DockIcon(emoji = "☰", active = drawerOpen) { drawerOpen = true }   // P0-1 菜单入口（不再隐蔽）
            DockIcon(emoji = "🎮", active = inputMode == InputMode.GAME) {
                inputMode = if (inputMode == InputMode.DESKTOP) InputMode.GAME else InputMode.DESKTOP
                prefs.edit().putString("inputMode", if (inputMode == InputMode.GAME) "game" else "desktop").apply()
            }
            DockIcon(emoji = "⌨️", active = showKbd) { showKbd = !showKbd }
            DockIcon(emoji = "🖼", active = !stretch) { stretch = !stretch; prefs.edit().putBoolean("stretchVideo", stretch).apply() }
            DockIcon(emoji = "✕", active = false, danger = true) {
                scope.launch { inputSender.send(InputAction.ReleaseAll) }
                player?.stop()
                onBack()
            }
        }

        // ★ P0-1 底部抽屉菜单（向日葵 v0194/v0205 同构；☰ 弹出；单击空白区外=收起）
        if (drawerOpen) {
            androidx.compose.foundation.layout.Column(
                modifier = Modifier
                    .align(Alignment.BottomCenter)
                    .fillMaxWidth()
                    .background(Color(0xF01A1A22), androidx.compose.foundation.shape.RoundedCornerShape(topStart = 16.dp, topEnd = 16.dp))
                    .padding(14.dp),
            ) {
                // 顶行：⌄ 收起
                androidx.compose.material3.TextButton(
                    onClick = { drawerOpen = false },
                    modifier = Modifier.align(Alignment.CenterHorizontally),
                ) { Text("⌄", color = Color(0xCCFFFFFF), fontSize = 18.sp) }

                // 组1：快捷操作（5 图标行）
                Text("快捷操作", color = Color(0x88FFFFFF), fontSize = 10.sp, modifier = Modifier.padding(top = 4.dp, start = 4.dp))
                androidx.compose.foundation.layout.Row(horizontalArrangement = androidx.compose.foundation.layout.Arrangement.SpaceEvenly, modifier = Modifier.fillMaxWidth()) {
                    DrawerItem("✕", "退出远控", danger = true) { scope.launch { inputSender.send(InputAction.ReleaseAll) }; player?.stop(); onBack() }
                    DrawerItem("⏺", "开启录像") { }
                    DrawerItem("📷", "屏幕截图") { }
                    DrawerItem("🔊", "开启声音") { }
                    DrawerItem("🖥", "返回桌面") { }
                }

                // 组2：系统操作（4 图标行）
                Text("系统操作", color = Color(0x88FFFFFF), fontSize = 10.sp, modifier = Modifier.padding(top = 6.dp, start = 4.dp))
                androidx.compose.foundation.layout.Row(horizontalArrangement = androidx.compose.foundation.layout.Arrangement.SpaceEvenly, modifier = Modifier.fillMaxWidth()) {
                    DrawerItem("🔒", "锁定桌面") { }
                    DrawerItem("🔄", "旋转屏幕") { orientationMode = when (orientationMode) { 0 -> 1; 1 -> 2; else -> 0 } }
                    DrawerItem("⏻", "关闭设备") { }
                    DrawerItem("⟳", "重启设备") { }
                }

                // 组3：远控方式（指针/触控 两钮 + 手势指南）
                androidx.compose.foundation.layout.Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth().padding(top = 12.dp)) {
                    Text("远控方式", color = Color(0x88FFFFFF), fontSize = 10.sp, modifier = Modifier.weight(1f))
                    androidx.compose.material3.TextButton(onClick = { }) { Text("手势指南 >", color = Color(0x88FFFFFF), fontSize = 10.sp) }
                }
                androidx.compose.foundation.layout.Row(modifier = Modifier.fillMaxWidth()) {
                    androidx.compose.material3.FilledTonalButton(
                        onClick = { mouseMode = "absolute"; prefs.edit().putString("mouseMode", "absolute").apply() },
                        modifier = Modifier.weight(1f).padding(end = 4.dp),
                    ) { Text(if (!isRelative) "指针模式 ✓" else "指针模式", fontSize = 12.sp) }
                    androidx.compose.material3.FilledTonalButton(
                        onClick = { mouseMode = "relative"; prefs.edit().putString("mouseMode", "relative").apply() },
                        modifier = Modifier.weight(1f).padding(start = 4.dp),
                    ) { Text(if (isRelative) "触控模式 ✓" else "触控模式", fontSize = 12.sp) }
                }

                // 组4：开关项（虚拟鼠标=影子光标开关 P0-3 预留；锁定显示）
                androidx.compose.foundation.layout.Row(modifier = Modifier.fillMaxWidth().padding(top = 10.dp)) {
                    Text("虚拟鼠标", color = Color.White, fontSize = 12.sp, modifier = Modifier.weight(1f))
                    androidx.compose.material3.Switch(checked = shadowCursor, onCheckedChange = { shadowCursor = it })
                }
                androidx.compose.foundation.layout.Row(modifier = Modifier.fillMaxWidth()) {
                    Text("锁定显示", color = Color.White, fontSize = 12.sp, modifier = Modifier.weight(1f))
                    androidx.compose.material3.Switch(checked = false, onCheckedChange = { })
                }

                // 组5：键盘三入口
                Text("键盘", color = Color(0x88FFFFFF), fontSize = 10.sp, modifier = Modifier.padding(top = 6.dp, start = 4.dp))
                androidx.compose.foundation.layout.Row(horizontalArrangement = androidx.compose.foundation.layout.Arrangement.SpaceEvenly, modifier = Modifier.fillMaxWidth()) {
                    DrawerItem("⌨", "输入键盘") { showKbd = !showKbd }
                    DrawerItem("▦", "自定键盘") { }
                    DrawerItem("🎮", "游戏键盘") { inputMode = if (inputMode == InputMode.GAME) InputMode.DESKTOP else InputMode.GAME; prefs.edit().putString("inputMode", if (inputMode == InputMode.GAME) "game" else "desktop").apply() }
                }

                // 组6：画质四档（P3 占位）
                Text("远控质量", color = Color(0x88FFFFFF), fontSize = 10.sp, modifier = Modifier.padding(top = 6.dp, start = 4.dp))
                androidx.compose.foundation.layout.Row(horizontalArrangement = androidx.compose.foundation.layout.Arrangement.SpaceEvenly, modifier = Modifier.fillMaxWidth()) {
                    DrawerItem("🖥", "桌面") { }
                    DrawerItem("✨", "高清") { }
                    DrawerItem("🎮", "游戏") { }
                    DrawerItem("⚡", "极速") { }
                }
            }
        }

        // morePanel 已由 P0-1 底部抽屉取代（画质组含等比/缩放档）

        if (toolbarVisible) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier
                    .align(Alignment.TopCenter)
                    .fillMaxWidth()
                    .padding(horizontal = 12.dp, vertical = 8.dp)
                    .background(Color(0xAA000000)),
            ) {
                androidx.compose.material3.FilledTonalButton(onClick = onBack) { Text("←") }
                Text(playerState.name, Modifier.padding(horizontal = 4.dp), color = Color(0xCCFFFFFF), fontSize = 10.sp)
                androidx.compose.material3.FilledTonalButton(onClick = {
                    val next = if (isRelative) "absolute" else "relative"
                    mouseMode = next
                    prefs.edit().putString("mouseMode", next).apply()
                }) { Text(if (isRelative) "相对" else "绝对", fontSize = 11.sp) }
                androidx.compose.material3.FilledTonalButton(onClick = {
                    stretch = !stretch
                    prefs.edit().putBoolean("stretchVideo", stretch).apply()
                }) { Text(if (stretch) "拉伸" else "等比", fontSize = 11.sp) }
                androidx.compose.material3.FilledTonalButton(onClick = {
                    orientationMode = when (orientationMode) { 0 -> 1; 1 -> 2; else -> 0 }
                    val act = context as? android.app.Activity
                    act?.requestedOrientation = when (orientationMode) {
                        1 -> android.content.pm.ActivityInfo.SCREEN_ORIENTATION_USER_LANDSCAPE
                        2 -> android.content.pm.ActivityInfo.SCREEN_ORIENTATION_USER_PORTRAIT
                        else -> android.content.pm.ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
                    }
                }) { Text(when (orientationMode) { 0 -> "自动"; 1 -> "横屏" else -> "竖屏" }, fontSize = 11.sp) }
                androidx.compose.material3.FilledTonalButton(onClick = {
                    inputMode = if (inputMode == InputMode.DESKTOP) InputMode.GAME else InputMode.DESKTOP
                    prefs.edit().putString("inputMode", if (inputMode == InputMode.GAME) "game" else "desktop").apply()
                }) { Text(if (inputMode == InputMode.GAME) "🎮游戏" else "🖱桌面", fontSize = 11.sp) }
                androidx.compose.material3.FilledTonalButton(onClick = { showKbd = !showKbd }) { Text("键盘", fontSize = 11.sp) }
                androidx.compose.material3.FilledTonalButton(onClick = {
                    scope.launch { inputSender.send(InputAction.ReleaseAll) }
                    player?.stop()
                    onBack()
                }) { Text("断开", color = Color(0xFFFF8A80), fontSize = 11.sp) }
            }
            errorText?.let {
                Text(it, Modifier.align(Alignment.TopStart).padding(start = 12.dp, top = 56.dp),
                    color = MaterialTheme.colorScheme.error, fontSize = 10.sp)
            }
            // P0-2 键盘浮层：半透明输入框（文字发送走 Text 注入；Enter=回车键）
            if (showKbd) {
                androidx.compose.material3.OutlinedTextField(
                    value = kbdText,
                    onValueChange = { kbdText = it },
                    modifier = Modifier.align(Alignment.BottomCenter).fillMaxWidth().padding(horizontal = 12.dp, vertical = 8.dp),
                    placeholder = { Text("输入文字，回车发送", color = Color(0x88FFFFFF), fontSize = 11.sp) },
                    textStyle = androidx.compose.ui.text.TextStyle(color = Color.White, fontSize = 13.sp),
                    singleLine = true,
                    keyboardOptions = androidx.compose.foundation.text.KeyboardOptions(imeAction = androidx.compose.ui.text.input.ImeAction.Send),
                    keyboardActions = androidx.compose.foundation.text.KeyboardActions(onSend = {
                        if (kbdText.isNotEmpty()) {
                            scope.launch { inputSender.send(InputAction.Text(kbdText)) }
                            kbdText = ""
                        }
                    }),
                )
            }
        }
    }

    DisposableEffect(Unit) {
        onDispose {
            player?.stop()
            player = null
            inputSender.shutdown()
        }
    }
}

/**
 * 触屏手势层 v2（T41b）：消费感知 = awaitFirstDown(requireUnconsumed=true) + changes.any{isConsumed} 整组作废；
 *   Compose Main pass 子→父冒泡：兄弟节点后绘制（z 上层）先收——按键层放代码顺序后即先收 consume。
 *  - tap=远端左键；双击=呼出/隐藏悬浮工具栏；长按=右键（600ms）；
 *  - 拖动=down+move(节流)+up（S7：收尾 up 用 lastMovePos）；
 *  - 双指=wheel；三指 tap=中键；三指下滑=呼出工具栏（onTapToolbar）。
 */
@Composable
private fun TouchGestureLayer(
    videoRect: androidx.compose.ui.geometry.Rect,
    zoom: Float,
    onTapToolbar: () -> Unit,
    onZoom: (Float) -> Unit,
    onResetZoom: () -> Unit,
    onLookDelta: (dx: Int, dy: Int) -> Unit,
    onDragStart: (nx: Double, ny: Double) -> Unit,
    onDragMove: (nx: Double, ny: Double) -> Unit,
    onDragEnd: (nx: Double, ny: Double) -> Unit,
    onClick: (nx: Double, ny: Double, button: String) -> Unit,
    onWheel: (delta: Int) -> Unit,
    modifier: Modifier = Modifier,
) {
    val density = androidx.compose.ui.platform.LocalDensity.current
    val dragThresholdPx = with(density) { PlayerConstants.DRAG_THRESHOLD_DP.dp.toPx() }
    // ★ 单位修正：tap 阈值官方是物理 px（Moonlight），在 3x 屏≈6.7dp——统一 dp 换算，
    //   密度无关（2x 屏 14px、3x 屏 21px，与官方 20px 同量级）。
    val tapMovementPx = with(density) { PlayerConstants.TAP_MOVEMENT_DP.dp.toPx() }
    val tapPathPx = with(density) { PlayerConstants.TAP_PATH_DP.dp.toPx() }
    Box(
        modifier = modifier.pointerInput(Unit) {
            awaitEachGesture {
                // S5/S6：归一化按 videoRect（等比模式真实视频区）；rect 未就绪回退布局全屏。
                // 黑边区坐标 clamp 到 [0,1]：点视频边缘对应位置（黑边不是死区），
                // 双击/三指呼出工具栏不受坐标影响。
                val nxOf: (Float) -> Double = { x ->
                    val r = videoRect
                    val z = zoom
                    if (r.width > 0f) {
                        // T41g 缩放坐标反算：graphicsLayer 中心缩放后，屏幕点 x 对应
                        // 视频归一化 = ((x - 中心) / z + 半宽) / 宽
                        val cxr = r.left + r.width / 2f
                        (((((x - cxr) / z) + r.width / 2f)) / r.width).coerceIn(0f, 1f).toDouble()
                    } else (x / size.width.toFloat()).toDouble().coerceIn(0.0, 1.0)
                }
                val nyOf: (Float) -> Double = { y ->
                    val r = videoRect
                    val z = zoom
                    if (r.height > 0f) {
                        val cyr = r.top + r.height / 2f
                        (((((y - cyr) / z) + r.height / 2f)) / r.height).coerceIn(0f, 1f).toDouble()
                    } else (y / size.height.toFloat()).toDouble().coerceIn(0.0, 1.0)
                }
                val down = awaitFirstDown(requireUnconsumed = true)   // 消费感知：上层（未来虚拟按键层）已消费的 down 不进手势层
                val downPos = down.position
                var totalDx = 0f
                var totalDy = 0f
                var wasTwo = false
                var wasThree = false
                var threeDownY: Float? = null
                var pinchY: Float? = null
                var pinchBaseDist: Double? = null   // T41g：捏合基准距离
                var lastX = downPos.x
                var lastY = downPos.y
                // T41f：位移累积（防 toInt 截断丢精细增量）+ 本帧整数增量
                var accDx = 0f
                var accDy = 0f
                var dragDx = 0
                var dragDy = 0
                // T41f 验证：手指位移累计（Float）vs 产出增量累计（Int）
                var fingerTotalDx = 0f
                var fingerTotalDy = 0f
                var sentTotalDx = 0
                var sentTotalDy = 0
                var lastMovePos = downPos     // S7：收尾坐标源
                var isDragging = false
                var longPressFired = false
                var downAt = System.currentTimeMillis()
                var longPressChecked = false
                var lastTapAt = 0L   // 双击检测：300ms 内两次 tap = 呼出/隐藏工具栏
                val tapNxF = { nxOf(lastMovePos.x) }
                val tapNyF = { nyOf(lastMovePos.y) }

                while (true) {
                    val event = awaitPointerEvent()
                    val changes = event.changes
                    // ★ 消费感知：任一指已被上层（虚拟按键层）消费 → 整组手势作废，
                    //   立即收尾退出（不发任何输入，防止按钮 keydown 与手势 click 双发）。
                    if (changes.any { it.isConsumed }) {
                        if (isDragging) {
                            // 已在拖动中被打断：只发 up 收尾（不发送残余 move）；
                            // onDragEnd 回调层会把 cursorPressed 置回 false
                            onDragEnd(nxOf(lastMovePos.x), nyOf(lastMovePos.y))
                        }
                        break
                    }
                    val active = changes.filter { it.pressed }

                    if (active.size >= 2) {
                        wasTwo = true
                        if (active.size >= 3) {
                            wasThree = true
                            if (threeDownY == null) threeDownY = active.map { it.position.y }.average().toFloat()
                        }
                        if (isDragging) {
                            onDragEnd(nxOf(lastMovePos.x), nyOf(lastMovePos.y))
                            isDragging = false
                        }
                        longPressChecked = true
                    }

                    if (active.isEmpty()) {
                        // T41f 验证日志：累计手指位移（Float 精确）vs 累计产出增量（发送口径）
                        android.util.Log.i("SoulLinkGesture",
                            "手势结束：手指位移=" + fingerTotalDx + "," + fingerTotalDy +
                            " 产出增量=" + sentTotalDx + "," + sentTotalDy +
                            "（应基本相等；余量=" + accDx + "," + accDy + "）")
                        // B 工具栏呼出①：顶部 1/8 区域起手 + 下滑（移动端下拉肌肉记忆，零冲突）
                        val topZone = size.height / 8f
                        if (!isDragging && !wasTwo && !wasThree && downPos.y < topZone && totalDy > 60f * density.density) {
                            onTapToolbar()
                            break
                        }
                        if (isDragging) {
                            onDragEnd(nxOf(lastMovePos.x), nyOf(lastMovePos.y))
                        } else if (!longPressFired) {
                            val maxMove = kotlin.math.hypot(totalDx, totalDy)
                            val elapsed = System.currentTimeMillis() - downAt
                            // A3 对齐 Moonlight：tap = 位移<20px 且 累计路径<25px 且 <250ms
                            val isTap = maxMove < tapMovementPx &&
                                (kotlin.math.abs(totalDx) + kotlin.math.abs(totalDy)) < tapPathPx &&
                                elapsed < PlayerConstants.TAP_TIME_MS
                            if (isTap) {
                                val now = System.currentTimeMillis()
                                val isDoubleTap = now - lastTapAt < 300
                                lastTapAt = now
                                if (isDoubleTap && !wasThree && !wasTwo) {
                                    if (zoom > 1f) {
                                        onResetZoom()   // ★ T41g 双击复位（缩放后双击回到 1x）
                                    } else {
                                        onClick(tapNxF(), tapNyF(), "left")
                                        onClick(tapNxF(), tapNyF(), "left")
                                    }
                                } else if (wasThree) {
                                    onClick(tapNxF(), tapNyF(), "middle")
                                } else if (wasTwo) {
                                    onClick(tapNxF(), tapNyF(), "right")
                                } else {
                                    // ★ P0-1 单击回归远端左键（用户三次澄清定案：
                                    //   专业软件 100% 单击=左键；菜单呼出=右侧 ☰ 图标列）。
                                    //   双击=远端双击（上分支），长按=右键，拖动=move/moverel。
                                    onClick(tapNxF(), tapNyF(), "left")
                                }
                            }
                        }
                        break
                    }

                    if (active.size == 1) {
                        val change = active.first()
                        val pos = change.position
                        // ★ T41f 修「相对位移不能漏」（用户点破 + Lead 定位）：
                        //   旧 (pos.x-lastX).toInt() 每帧截断小数——0.6px/帧的精细移动
                        //   永远发 0（光标不动）；1.6px/帧丢 37.5%。
                        //   修法（Moonlight RelativeTouchContext :153-158 零增量保护 + 余量累积）：
                        //   ① 累积精确 Float 增量，只在取整后发送，余数留到下一帧；
                        //   ② 零增量保护：delta 取整为 0 时不更新基准（下一帧补回）。
                        accDx += (pos.x - lastX)
                        accDy += (pos.y - lastY)
                        fingerTotalDx += (pos.x - lastX)
                        fingerTotalDy += (pos.y - lastY)
                        val ix = accDx.toInt()
                        val iy = accDy.toInt()
                        if (ix != 0 || iy != 0) {
                            accDx -= ix   // ★ 余数保留（关键：0.6+0.6+0.6 → 第2帧发1）
                            accDy -= iy
                            dragDx = ix
                            dragDy = iy
                            sentTotalDx += ix
                            sentTotalDy += iy
                            // 零增量保护：只在真的产出增量时才更新基准位置
                            lastX = pos.x
                            lastY = pos.y
                        }
                        lastMovePos = pos
                        val dx = dragDx
                        val dy = dragDy
                        totalDx += dx
                        totalDy += dy

                        if (!longPressChecked && !isDragging && !longPressFired) {
                            val moved = kotlin.math.hypot(totalDx, totalDy)
                            if (moved >= dragThresholdPx) {
                                longPressChecked = true
                            } else if (System.currentTimeMillis() - downAt >= PlayerConstants.LONG_PRESS_MS) {
                                longPressFired = true
                                longPressChecked = true
                                onClick(nxOf(lastMovePos.x), nyOf(lastMovePos.y), "right")
                            }
                        }

                        val maxMove = kotlin.math.hypot(totalDx, totalDy)
                        if (!isDragging && maxMove >= dragThresholdPx && !wasTwo) {
                            isDragging = true
                            onDragStart(nxOf(downPos.x), nyOf(downPos.y))
                        }
                        if (isDragging && (dx != 0 || dy != 0)) {
                            onLookDelta(dx, dy)
                            onDragMove(nxOf(pos.x), nyOf(pos.y))
                        }
                    } else if (active.size >= 3) {
                        // 三指下滑 → 呼出工具栏（累计下滑超 60px）
                        val midY = active.map { it.position.y }.average().toFloat()
                        val base = threeDownY ?: midY
                        if (midY - base > 60f * density.density) {   // 20dp 物理换算
                            onTapToolbar()
                            threeDownY = midY
                        }
                    } else if (active.size == 2) {
                        val p0 = active[0].position
                        val p1 = active[1].position
                        val dist = kotlin.math.hypot((p1.x - p0.x).toDouble(), (p1.y - p0.y).toDouble())
                        // ★ T41g 缩放（用户「没有缩放能力」）：双指距离变化区分——
                        //   捏合（距离变化 >12% 基准）→ zoom；同向滑动（距离稳定）→ 滚轮。
                        if (pinchBaseDist == null) {
                            pinchBaseDist = dist
                        } else {
                            val base = pinchBaseDist ?: dist
                            val ratio = dist / base
                            if (ratio > 1.12f || ratio < 0.88f) {
                                // 捏合模式：zoom 跟随比例（1.0~4.0 clamp）
                                onZoom((dist / base).toFloat())
                                pinchBaseDist = dist   // 基准滚动更新（增量捏合）
                            } else {
                                // 同向滑动：滚轮（中点 Y 位移）
                                val ys = active.map { it.position.y }
                                val midY = ys.average().toFloat()
                                if (pinchY != null) {
                                    val steps = kotlin.math.round((midY - pinchY) / (8f * density.density)).toInt()
                                    if (steps != 0) onWheel(steps * 120)
                                }
                                pinchY = midY
                            }
                        }
                    }
                }
            }
        },
    )
}

private fun defaultInputClient(): OkHttpClient = OkHttpClient.Builder()
    .connectTimeout(5, TimeUnit.SECONDS)
    .readTimeout(10, TimeUnit.SECONDS)
    .build()

/**
 * T41h 向日葵式 Dock 图标：44dp 半透明圆（黑底 0.5f）+ emoji 文字；active 亮边。
 * clickable 消费自身点击（图标外不拦截）。
 */
@Composable
private fun DockIcon(
    emoji: String,
    active: Boolean,
    danger: Boolean = false,
    onClick: () -> Unit,
) {
    androidx.compose.foundation.layout.Box(
        contentAlignment = androidx.compose.ui.Alignment.Center,
        modifier = Modifier
            .size(44.dp)
            .clickable { onClick() },
    ) {
        androidx.compose.foundation.Canvas(modifier = Modifier.size(44.dp)) {
            drawCircle(color = Color.Black.copy(alpha = 0.5f), radius = 22.dp.toPx())
            if (active) drawCircle(
                color = if (danger) Color(0xCCFF5252) else Color(0xCC2196F3),
                radius = 22.dp.toPx(),
                style = Stroke(3.dp.toPx()),
            )
        }
        Text(emoji, fontSize = 16.sp, color = if (danger) Color(0xFFFF8A80) else Color.White)
    }
}

/** P0-1 抽屉菜单项：图标+小文字竖排（无圆底，纯文字列风格对齐向日葵）。 */
@Composable
private fun DrawerItem(
    emoji: String,
    label: String,
    danger: Boolean = false,
    onClick: () -> Unit,
) {
    androidx.compose.foundation.layout.Column(
        horizontalAlignment = androidx.compose.ui.Alignment.CenterHorizontally,
        modifier = Modifier
            .padding(horizontal = 6.dp)
            .clickable { onClick() },
    ) {
        Text(emoji, fontSize = 20.sp, color = if (danger) Color(0xFFFF8A80) else Color.White)
        Text(label, fontSize = 9.sp, color = Color(0xCCFFFFFF))
    }
}

private fun defaultStreamClient(): OkHttpClient = OkHttpClient.Builder()
    .connectTimeout(5, TimeUnit.SECONDS)
    .readTimeout(0, TimeUnit.MILLISECONDS)
    .build()
