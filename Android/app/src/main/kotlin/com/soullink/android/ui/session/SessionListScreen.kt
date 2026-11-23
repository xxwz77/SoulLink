package com.soullink.android.ui.session

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.soullink.android.net.SoulLinkApi
import com.soullink.android.net.dto.SessionDto
import com.soullink.android.net.dto.SessionListDto

/**
 * 会话列表页：GET /api/sessions → 展示账号列表，点击进入播放页占位。
 */
@Composable
fun SessionListScreen(
    api: SoulLinkApi,
    onSessionClick: (SessionDto) -> Unit,
    onDirectToken: (String) -> Unit,
    onDisconnect: () -> Unit,
) {
    var state by remember { mutableStateOf<SessionListDto?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var loading by remember { mutableStateOf(true) }
    var retryTick by remember { mutableStateOf(0) }

    fun refresh() {
        loading = true; error = null
        // 用协程拉取（简单起见用 LaunchedEffect 触发一次 + 手动重试按钮）
        retryTick++
    }

    LaunchedEffect(retryTick) {
        try {
            val dto = api.sessions()
            state = dto
            if (dto.directToken.isNotBlank()) onDirectToken(dto.directToken)
        } catch (e: Exception) {
            error = e.message ?: "加载失败"
        } finally { loading = false }
    }

    Column(Modifier.fillMaxSize().padding(16.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("会话列表", style = MaterialTheme.typography.titleLarge, modifier = Modifier.weight(1f))
            TextButton(onClick = onDisconnect) { Text("断开") }
        }
        when {
            loading -> CircularProgressIndicator(Modifier.align(Alignment.CenterHorizontally))
            error != null -> Column {
                Text(error!!, color = MaterialTheme.colorScheme.error)
                TextButton(onClick = { refresh() }) { Text("重试") }
            }
            else -> LazyColumn {
                items(state?.sessions ?: emptyList()) { s ->
                    SessionCard(s, onClick = { onSessionClick(s) })
                }
            }
        }
    }
}

@Composable
private fun SessionCard(s: SessionDto, onClick: () -> Unit) {
    Card(
        modifier = Modifier.fillMaxWidth().padding(vertical = 4.dp).clickable(onClick = onClick),
    ) {
        Column(Modifier.padding(12.dp)) {
            Text(s.label.ifBlank { s.user }, style = MaterialTheme.typography.titleMedium)
            Spacer(Modifier.height(4.dp))
            Text(
                "在线=${if (s.online) "是" else "否"} 端口=${s.directPort} 会话=${s.sessionId}" +
                    if (s.screen.isNotBlank()) "  画面=${s.screen}" else "",
                style = MaterialTheme.typography.bodySmall,
            )
        }
    }
}
