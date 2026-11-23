package com.soullink.android.ui.player

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.soullink.android.net.SoulLinkApi
import com.soullink.android.net.dto.SessionDto

/**
 * 播放页占位：MediaCodec 解码核心留待 viewer-auditor。
 * 此处仅展示连接参数（streamUrl / inputUrl），验证协议连通。
 */
@Composable
fun PlayerPlaceholderScreen(
    host: String,
    session: SessionDto,
    api: SoulLinkApi,
    onBack: () -> Unit,
) {
    val stream = api.streamUrl(session.directPort, directTokenOf(api))
    Column(Modifier.fillMaxSize().padding(16.dp)) {
        TextButton(onClick = onBack) { Text("← 返回列表") }
        Text("播放页（占位）", style = MaterialTheme.typography.titleLarge)
        Text("账号：${session.label.ifBlank { session.user }}", modifier = Modifier.padding(top = 8.dp))
        Text("directPort：${session.directPort}", modifier = Modifier.padding(top = 4.dp))
        Text("在线：${if (session.online) "是" else "否"}", modifier = Modifier.padding(top = 4.dp))
        Text("streamUrl：$stream", modifier = Modifier.padding(top = 4.dp))
        Text("inputUrl：${api.inputUrl(session.directPort)}", modifier = Modifier.padding(top = 4.dp))
        Text(
            "画面渲染（MediaCodec + SurfaceView）由 viewer-auditor 实现。",
            style = MaterialTheme.typography.bodySmall,
            modifier = Modifier.padding(top = 16.dp),
        )
    }
}

/** directToken 需从 /api/sessions 下发；本占位页从 api 的会话缓存取，失败则空串。 */
private fun directTokenOf(api: SoulLinkApi): String {
    // 简化：占位页不缓存 token，由 viewer 层实现时从 SessionListDto.directToken 传递。
    return ""
}
