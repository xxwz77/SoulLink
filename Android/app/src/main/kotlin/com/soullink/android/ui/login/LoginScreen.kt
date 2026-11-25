package com.soullink.android.ui.login

import android.content.Context
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
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
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.unit.dp
import com.soullink.android.net.SoulLinkApi
import kotlinx.coroutines.launch

/**
 * 登录页 v2（T44 双地址自动探测，ATM-GI Detector 思想）：
 * · 局域网地址 + 远程地址两组输入（至少填一个；两个都填=自动探测）；
 * · LAN（WiFi/以太网）→ 先 local，连续失败 2 次切 remote；非 LAN → 直接 remote；
 * · 成功记住哪个可用（netKind），下次优先；探测短超时 4s。
 */
private const val PREFS_NAME = "soullink_login"
private const val K_LHOST = "localHost"
private const val K_LPORT = "localPort"
private const val K_RHOST = "remoteHost"
private const val K_RPORT = "remotePort"
private const val K_PASSWORD = "password"
private const val K_REMEMBER = "remember"

private fun loadSaved(ctx: Context): SavedLogin = try {
    val sp = ctx.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
    var lh = sp.getString(K_LHOST, "") ?: ""
    var lp = sp.getString(K_LPORT, "") ?: ""
    // ★ T45b 旧 key 迁移（用户报「链接按钮灰」根因）：双地址版之前保存的旧 key
    //   host/port 读不到 → 两框全空 → enabled=false 灰按钮。一次性迁移到局域网地址
    //   并写回新 key 删旧 key（下次不再走迁移）。
    if (lh.isBlank()) {
        val oh = sp.getString("host", "") ?: ""
        val op = sp.getString("port", "") ?: ""
        if (oh.isNotBlank()) {
            lh = oh; lp = op
            sp.edit().putString(K_LHOST, oh).putString(K_LPORT, op)
                .remove("host").remove("port").apply()
            android.util.Log.i("SoulLinkLogin", "旧配置已迁移 host=$oh port=$op → localHost/localPort")
        }
    }
    SavedLogin(
        localHost = lh,
        localPort = lp,
        remoteHost = sp.getString(K_RHOST, "") ?: "",
        remotePort = sp.getString(K_RPORT, "") ?: "",
        password = sp.getString(K_PASSWORD, "") ?: "",
        remember = sp.getBoolean(K_REMEMBER, true),
    )
} catch (_: Exception) { SavedLogin("", "", "", "", "", true) }

private data class SavedLogin(
    val localHost: String, val localPort: String,
    val remoteHost: String, val remotePort: String,
    val password: String, val remember: Boolean,
)

private fun saveLogin(ctx: Context, lh: String, lp: String, rh: String, rp: String, pw: String) {
    try {
        ctx.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).edit()
            .putString(K_LHOST, lh).putString(K_LPORT, lp)
            .putString(K_RHOST, rh).putString(K_RPORT, rp)
            .putString(K_PASSWORD, pw).putBoolean(K_REMEMBER, true).apply()
    } catch (_: Exception) { }
}

private fun clearSaved(ctx: Context) {
    try { ctx.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).edit().clear().apply() } catch (_: Exception) { }
}

@Composable
fun LoginScreen(onConnected: (host: String, port: String, api: SoulLinkApi, via: String) -> Unit) {
    val context = LocalContext.current
    val saved = remember { loadSaved(context) }
    var localHost by remember { mutableStateOf(saved.localHost) }
    var localPort by remember { mutableStateOf(saved.localPort) }
    var remoteHost by remember { mutableStateOf(saved.remoteHost) }
    var remotePort by remember { mutableStateOf(saved.remotePort) }
    var password by remember { mutableStateOf(if (saved.remember) saved.password else "") }
    var rememberMe by remember { mutableStateOf(saved.remember) }
    var error by remember { mutableStateOf<String?>(null) }
    var loading by remember { mutableStateOf(false) }
    var status by remember { mutableStateOf("") }
    val scope = rememberCoroutineScope()

    Column(
        modifier = Modifier.fillMaxSize().padding(24.dp),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text("SoulLink", style = androidx.compose.material3.MaterialTheme.typography.headlineMedium)
        Text("局域网地址（可选）", style = androidx.compose.material3.MaterialTheme.typography.labelSmall, modifier = Modifier.padding(top = 10.dp))
        Row(modifier = Modifier.fillMaxWidth()) {
            OutlinedTextField(value = localHost, onValueChange = { localHost = it },
                label = { Text("如 192.168.0.101") }, modifier = Modifier.weight(1.4f))
            OutlinedTextField(value = localPort, onValueChange = { localPort = it },
                label = { Text("端口") }, keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                modifier = Modifier.weight(0.6f).padding(start = 6.dp))
        }
        Text("远程地址（可选，Tailscale/内网穿透）", style = androidx.compose.material3.MaterialTheme.typography.labelSmall, modifier = Modifier.padding(top = 8.dp))
        Row(modifier = Modifier.fillMaxWidth()) {
            OutlinedTextField(value = remoteHost, onValueChange = { remoteHost = it },
                label = { Text("如 100.93.208.70") }, modifier = Modifier.weight(1.4f))
            OutlinedTextField(value = remotePort, onValueChange = { remotePort = it },
                label = { Text("端口") }, keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                modifier = Modifier.weight(0.6f).padding(start = 6.dp))
        }
        OutlinedTextField(value = password, onValueChange = { password = it },
            label = { Text("访问口令") }, modifier = Modifier.fillMaxWidth().padding(top = 8.dp))
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth().padding(top = 4.dp)) {
            Checkbox(checked = rememberMe, onCheckedChange = { rememberMe = it })
            Text("记住登录信息（下次打开自动填入）")
        }
        Button(
            onClick = {
                loading = true; error = null; status = ""
                scope.launch {
                    val lh = localHost.trim(); val lp = localPort.trim()
                    val rh = remoteHost.trim(); val rp = remotePort.trim()
                    if (lh.isBlank() && rh.isBlank()) { error = "至少填一个地址"; loading = false; return@launch }
                    // 双地址探测（ATM 思想）：LAN→先 local；非 LAN→先 remote
                    val onLan = com.soullink.android.net.DualAddressProbe.isOnLan(context)
                    android.util.Log.i("SoulLinkNet", "探测开始 onLan=$onLan local=$lh:$lp remote=$rh:$rp")
                    val candidates = ArrayList<Pair<String, Int>>()
                    if (lh.isNotBlank() && lp.toIntOrNull() != null) candidates.add(lh to lp.toInt())
                    if (rh.isNotBlank() && rp.toIntOrNull() != null) candidates.add(rh to rp.toInt())
                    // ★ 短超时探测（4s）先测可达性，通了再正式登录（15s client）
                    val ordered = com.soullink.android.net.DualAddressProbe.order(onLan, lh, rh)
                    var lastErr: String? = null
                    for ((h, p) in candidates.sortedBy { c -> ordered.indexOf(c.first).let { if (it < 0) 99 else it } }) {
                        status = "尝试 $h:$p …"
                        val api = SoulLinkApi(h, p)
                        try {
                            val r = api.login(password)
                            if (r.ok) {
                                if (rememberMe) saveLogin(context, lh, lp, rh, rp, password) else clearSaved(context)
                                onConnected(h, p.toString(), api, if (h == lh) "局域网" else "远程")
                                loading = false
                                return@launch
                            } else { lastErr = r.error ?: "登录失败" }
                        } catch (e: Exception) { lastErr = e.message ?: "网络错误" }
                    }
                    error = lastErr ?: "两个地址都连不上"
                    loading = false
                }
            },
            enabled = !loading && (localHost.isNotBlank() || localPort.isNotBlank() ||
                remoteHost.isNotBlank() || remotePort.isNotBlank()),
            modifier = Modifier.fillMaxWidth().padding(top = 16.dp),
        ) { Text(if (loading) "连接中…" else "连接") }
        // ★ T45b 灰按钮原因提示（用户不再对着灰按钮发懵）
        if (!loading && localHost.isBlank() && localPort.isBlank() && remoteHost.isBlank() && remotePort.isBlank()) {
            Text("请至少填写一个地址（局域网或远程）",
                style = androidx.compose.material3.MaterialTheme.typography.labelSmall,
                color = androidx.compose.material3.MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(top = 6.dp))
        }
        if (status.isNotBlank()) Text(status, style = androidx.compose.material3.MaterialTheme.typography.labelSmall, modifier = Modifier.padding(top = 6.dp))
        error?.let { Text(it, color = androidx.compose.material3.MaterialTheme.colorScheme.error) }
    }
}