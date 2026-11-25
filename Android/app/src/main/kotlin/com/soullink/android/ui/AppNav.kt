package com.soullink.android.ui

import android.app.Activity
import android.content.pm.ActivityInfo
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.platform.LocalContext
import com.soullink.android.net.SoulLinkApi
import com.soullink.android.net.dto.SessionDto
import com.soullink.android.ui.login.LoginScreen
import com.soullink.android.ui.player.PlayerPlaceholderScreen
import com.soullink.android.ui.session.SessionListScreen

@Composable
fun AppNav(
    onEnterPlayer: () -> Unit = {},
    onLeavePlayer: () -> Unit = {},
) {
    var host by remember { mutableStateOf("") }
    var port by remember { mutableStateOf("") }
    var via by remember { mutableStateOf("") }   // T44 连接类型（局域网/远程）显示
    var api by remember { mutableStateOf<SoulLinkApi?>(null) }
    var selectedSession by remember { mutableStateOf<SessionDto?>(null) }
    var directToken by remember { mutableStateOf("") }

    val ctx = LocalContext.current
    val act = ctx as? Activity

    DisposableEffect(selectedSession != null) {
        if (selectedSession != null) {
            onEnterPlayer()
            act?.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_USER_LANDSCAPE
        } else {
            onLeavePlayer()
            act?.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
        }
        onDispose {
            onLeavePlayer()
            act?.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
        }
    }

    when {
        api == null -> LoginScreen(onConnected = { h, p, a, v ->
            host = h; port = p; api = a; via = v
        })
        selectedSession != null -> PlayerPlaceholderScreen(
            host = host,
            session = selectedSession!!,
            api = api!!,
            directToken = directToken,
            onBack = { selectedSession = null },
        )
        else -> SessionListScreen(
            api = api!!,
            via = via,
            onSessionClick = { selectedSession = it },
            onDirectToken = { directToken = it },
            onDisconnect = { api = null; selectedSession = null },
        )
    }
}