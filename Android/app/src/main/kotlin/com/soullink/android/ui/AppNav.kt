package com.soullink.android.ui

import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import com.soullink.android.net.SoulLinkApi
import com.soullink.android.net.dto.SessionDto
import com.soullink.android.ui.login.LoginScreen
import com.soullink.android.ui.player.PlayerPlaceholderScreen
import com.soullink.android.ui.session.SessionListScreen

/**
 * 极简导航状态机（无 Navigation 库）：Login → SessionList → PlayerPlaceholder。
 */
@Composable
fun AppNav() {
    var host by remember { mutableStateOf("") }
    var port by remember { mutableStateOf("") }
    var api by remember { mutableStateOf<SoulLinkApi?>(null) }
    var selectedSession by remember { mutableStateOf<SessionDto?>(null) }
    var directToken by remember { mutableStateOf("") }

    when {
        api == null -> LoginScreen(onConnected = { h, p, a ->
            host = h; port = p; api = a
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
            onSessionClick = { selectedSession = it },
            onDirectToken = { directToken = it },
            onDisconnect = { api = null; selectedSession = null },
        )
    }
}
