package com.soullink.android.net

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities

/**
 * 双地址自动探测（ATM-GI Detector.kt 思想，最小版）：
 * 在 LAN → 先 local，连续失败 >=2 次切 remote（防抖动）；非 LAN → 直接 remote；
 * 成功记住 Kind（SharedPreferences netKind）下次优先；探测短超时 4s。
 */
object DualAddressProbe {

    /** 探测专用短超时 client（4s——切换要快，不用登录的 15s）。 */
    val probeClient: okhttp3.OkHttpClient = okhttp3.OkHttpClient.Builder()
        .connectTimeout(4, java.util.concurrent.TimeUnit.SECONDS)
        .readTimeout(4, java.util.concurrent.TimeUnit.SECONDS)
        .build()

    /** 快速探测某 host:port 的 /api/login 是否可达（HTTP 任意响应=可达）。 */
    fun probe(host: String, port: Int, password: String): Boolean = try {
        val url = "http://$host:$port/api/login?pw=" + java.net.URLEncoder.encode(password, "UTF-8")
        android.util.Log.i("SoulLinkNet", "探测 GET $url")
        val req = okhttp3.Request.Builder().url(url).get().build()
        probeClient.newCall(req).execute().use { it.code in 200..499 }   // 4xx 也算可达（服务在响应）
    } catch (e: Exception) {
        android.util.Log.e("SoulLinkNet", "探测失败 $host:$port", e)
        false
    }

    enum class Kind { LOCAL, REMOTE }

    /** 是否在局域网（WiFi/以太网）。需要 ACCESS_WIFI_STATE。 */
    fun isOnLan(ctx: Context): Boolean {
        return try {
            val cm = ctx.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager ?: return false
            val caps = cm.getNetworkCapabilities(cm.activeNetwork) ?: return false
            caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) ||
                caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)
        } catch (_: Exception) { false }
    }

    /** 尝试顺序（纯逻辑可单测）：local 空→[remote]；remote 空→[local]；LAN→[local,remote]；否则[remote]。 */
    fun order(onLan: Boolean, local: String?, remote: String?): List<String> {
        val l = local?.trim().orEmpty()
        val r = remote?.trim().orEmpty()
        return when {
            l.isBlank() && r.isBlank() -> emptyList()
            l.isBlank() -> listOf(r)
            r.isBlank() -> listOf(l)
            onLan -> listOf(l, r)
            else -> listOf(r)
        }
    }

    /** 是否该切换（连续失败 >= threshold）。 */
    fun shouldSwitch(failStreak: Int, threshold: Int = 2): Boolean = failStreak >= threshold

    /** 上次成功的 Kind 提到队首。 */
    fun prioritize(order: List<String>, lastKind: Kind?, local: String?, remote: String?): List<String> {
        if (order.size < 2) return order
        val l = local?.trim().orEmpty()
        val r = remote?.trim().orEmpty()
        val first = when (lastKind) {
            Kind.LOCAL -> l
            Kind.REMOTE -> r
            null -> ""
        }
        return if (first.isNotBlank() && order.firstOrNull() != first) {
            listOf(first) + order.filter { it != first }
        } else order
    }
}