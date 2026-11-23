package com.soullink.android.ui.login

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.soullink.android.net.SoulLinkApi
import kotlinx.coroutines.launch

/**
 * 登录页：输入主机 + 端口 + 口令，验证后拉会话列表。
 * 走现有 GET /api/login?pw=...（Set-Cookie: soullink_sid）。
 */
@Composable
fun LoginScreen(onConnected: (host: String, port: String, api: SoulLinkApi) -> Unit) {
    var host by remember { mutableStateOf("") }
    var port by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var error by remember { mutableStateOf<String?>(null) }
    var loading by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()

    Column(
        modifier = Modifier.fillMaxSize().padding(24.dp),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text("SoulLink", style = androidx.compose.material3.MaterialTheme.typography.headlineMedium)
        OutlinedTextField(
            value = host, onValueChange = { host = it },
            label = { Text("主机（IP 或域名）") },
            modifier = Modifier.fillMaxWidth(),
        )
        OutlinedTextField(
            value = port, onValueChange = { port = it },
            label = { Text("端口") },
            modifier = Modifier.fillMaxWidth(),
        )
        OutlinedTextField(
            value = password, onValueChange = { password = it },
            label = { Text("访问口令") },
            modifier = Modifier.fillMaxWidth(),
        )
        Button(
            onClick = {
                loading = true; error = null
                scope.launch {
                    val p = port.toIntOrNull() ?: 0
                    if (p <= 0) { error = "端口无效"; loading = false; return@launch }
                    val api = SoulLinkApi(host.trim(), p)
                    try {
                        val r = api.login(password)
                        if (r.ok) onConnected(host.trim(), port.trim(), api)
                        else error = r.error ?: "登录失败"
                    } catch (e: Exception) {
                        error = e.message ?: "网络错误"
                    } finally { loading = false }
                }
            },
            enabled = !loading && host.isNotBlank() && port.isNotBlank(),
            modifier = Modifier.fillMaxWidth().padding(top = 16.dp),
        ) { Text(if (loading) "连接中…" else "登录") }
        error?.let { Text(it, color = androidx.compose.material3.MaterialTheme.colorScheme.error) }
    }
}
