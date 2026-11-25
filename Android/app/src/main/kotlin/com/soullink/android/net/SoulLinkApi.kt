package com.soullink.android.net

import com.soullink.android.net.dto.LoginResult
import com.soullink.android.net.dto.SessionListDto
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.Cookie
import okhttp3.CookieJar
import okhttp3.HttpUrl
import okhttp3.OkHttpClient
import okhttp3.Request
import org.json.JSONObject
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit

/**
 * SoulLink 网络层：两套 base URL + 两套鉴权。
 *
 * - 主程序 base（host:mainPort）：登录、/api/sessions —— Cookie(soullink_sid) 鉴权。
 * - 代理 base（host:directPort）：/stream、/api/input —— X-SoulLink-Token / ?token= 鉴权。
 *
 * 复用现有 HTTP 契约（Services/WebServer.cs / Relay/AgentWebServer.cs），零后端改动。
 * 注意：登录接口是 GET /api/login?pw=...（WebServer.cs L330），不是 POST。
 */
class SoulLinkApi(
    private val host: String,
    private val mainPort: Int,
) {

    private val mainBase = "http://$host:$mainPort"

    /** 内存 CookieJar：只在会话内保留 soullink_sid，不落盘。 */
    private val cookieJar = object : CookieJar {
        private val store = ConcurrentHashMap<String, MutableList<Cookie>>()
        override fun saveFromResponse(url: HttpUrl, cookies: List<Cookie>) {
            store[url.host] = cookies.toMutableList()
        }
        override fun loadForRequest(url: HttpUrl): List<Cookie> {
            return store[url.host] ?: emptyList()
        }
    }

    private val client: OkHttpClient = OkHttpClient.Builder()
        .cookieJar(cookieJar)
        // ★ Tailscale 首次连接可能超过 5s（握手建链慢），放宽到 15/30
        .connectTimeout(15, TimeUnit.SECONDS)
        .readTimeout(30, TimeUnit.SECONDS)
        .build()

    /**
     * 登录：GET /api/login?pw=...。成功时后端 Set-Cookie: soullink_sid=...
     * CookieJar 自动保存，后续 /api/sessions 自动带上。
     */
    suspend fun login(password: String): LoginResult = withContext(Dispatchers.IO) {
        val url = "$mainBase/api/login?pw=" + java.net.URLEncoder.encode(password, "UTF-8")
        val request = Request.Builder().url(url).get().build()
        // ★ T34 网络诊断：实际 URL + 响应码 / 完整异常链（adb logcat -s SoulLinkNet）
        android.util.Log.i("SoulLinkNet", "GET $url")
        val t0 = System.currentTimeMillis()
        try {
            client.newCall(request).execute().use { resp ->
                android.util.Log.i("SoulLinkNet", "响应 code=" + resp.code + " url=" + resp.request.url + " 耗时=" + (System.currentTimeMillis() - t0) + "ms")
                val body = resp.body?.string() ?: "{}"
                LoginResult.fromJson(JSONObject(body))
            }
        } catch (e: Exception) {
            val elapsed = System.currentTimeMillis() - t0
            android.util.Log.e("SoulLinkNet", "连接失败 url=$url 耗时=${elapsed}ms", e)
            LoginResult(false, "连接失败（${elapsed}ms）：$url — ${e.message}")
        }
    }

    /**
     * 会话列表：GET /api/sessions（Cookie 鉴权）。返回 directToken + sessions[].directPort。
     */
    suspend fun sessions(): SessionListDto = withContext(Dispatchers.IO) {
        val request = Request.Builder().url("$mainBase/api/sessions").get().build()
        android.util.Log.i("SoulLinkNet", "GET $mainBase/api/sessions")
        client.newCall(request).execute().use { resp ->
            android.util.Log.i("SoulLinkNet", "sessions code=" + resp.code)
            if (!resp.isSuccessful) {
                throw IllegalStateException("sessions HTTP " + resp.code)
            }
            val body = resp.body?.string() ?: "{}"
            SessionListDto.fromJson(JSONObject(body))
        }
    }

    /** 代理流地址：/stream?token=...（画面裸 H.264，播放器核心留待 viewer-auditor）。 */
    fun streamUrl(directPort: Int, token: String): String =
        "http://$host:$directPort/stream?token=" + java.net.URLEncoder.encode(token, "UTF-8")

    /** 代理输入地址：POST /api/input，X-SoulLink-Token 头（留待输入层任务）。 */
    fun inputUrl(directPort: Int): String = "http://$host:$directPort/api/input"

    /**
     * T35: force IDR endpoint: POST /api/idr with X-SoulLink-Token header.
     * Exists server-side (AgentWebServer HandleIdrAsync -> SessionRecorder.RequestKeyFrame).
     * Player requests it when waiting for the first key frame times out.
     */
    fun idrUrl(directPort: Int): String = "http://$host:$directPort/api/idr"

}
