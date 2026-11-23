package com.soullink.android

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import com.soullink.android.ui.AppNav
import com.soullink.android.ui.theme.SoulLinkTheme

/**
 * 单 Activity 脚手架：登录 → 会话列表 → 播放页占位。
 * 画面/摇杆留待传统 View + SurfaceView（viewer-auditor 负责 MediaCodec 解码）。
 */
class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            SoulLinkTheme {
                AppNav()
            }
        }
    }
}
