package com.soullink.android

import android.os.Bundle
import android.view.View
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import com.soullink.android.ui.AppNav
import com.soullink.android.ui.theme.SoulLinkTheme

/**
 * 单 Activity 脚手架：登录 → 会话列表 → 播放页。
 *
 * ★ T36b（对齐 Moonlight Game.java:190-206）：全屏 + 布局扩展到屏幕（无标题、
 *   状态栏/导航栏区域由播放页的沉浸开关控制盖不盖内容）。
 *   沉浸式（隐藏系统栏）只在播放页启用，返回列表页恢复——Compose 单 Activity
 *   无法像 Moonlight 独立 Game Activity 那样天然隔离，用 AppNav 页面回调切换。
 */
class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // Moonlight Game.java:190-206：无标题 + FULLSCREEN + LAYOUT_IN_SCREEN
        window.addFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN)
        window.addFlags(WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN)
        // A4③：铺满刘海/挖孔（V+ Game.kt 同款，API 28+）
        if (android.os.Build.VERSION.SDK_INT >= 28) {
            window.attributes.layoutInDisplayCutoutMode =
                WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_ALWAYS
        }
        // A4⑤：现代全屏（API 30+）：内容延伸到系统栏后面
        if (android.os.Build.VERSION.SDK_INT >= 30) {
            window.setDecorFitsSystemWindows(false)
        }
        setContent {
            SoulLinkTheme {
                AppNav(
                    onEnterPlayer = { applyImmersive(true) },
                    onLeavePlayer = { applyImmersive(false) },
                )
            }
        }
    }

    /**
     * 沉浸式开关（对齐 Moonlight hideSystemUi Runnable :980-1003）：
     * IMMERSIVE_STICKY = 用户边缘滑动临时呼出后自动再隐藏。
     */
    private fun applyImmersive(immersive: Boolean) {
        runOnUiThread {
            window.decorView.systemUiVisibility = if (immersive) {
                (View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                    or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                    or View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                    or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                    or View.SYSTEM_UI_FLAG_FULLSCREEN
                    or View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY)
            } else {
                (View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                    or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                    or View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN)
            }
        }
    }
}