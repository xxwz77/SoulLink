package com.soullink.android.player

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.asCoroutineDispatcher
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import okhttp3.Call
import okhttp3.Callback
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import org.json.JSONObject
import java.io.IOException
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

/**
 * POST /api/input（代理直连，X-SoulLink-Token 头 + body 带 sid）。
 *
 * ★ T36（2026-11-24）修「点击闪退」：
 *   崩溃栈 = NetworkOnMainThreadException —— send() 的同步 okhttp execute() 被 Compose
 *   主线程调用（PlayerPlaceholderScreen :128 scope.launch 后仍走 AndroidUiDispatcher
 *   trampoline → 主线程联网）。StrictMode 直接抛异常闪退。
 *
 * ★ 线程模型（方案 B，对齐 Lead 指示）：
 *   - send（保序阻塞等回包）→ 内部 withContext(inputDispatcher)：**单线程 executor**
 *     = 天然 FIFO 保序（这是 down/up/click 的核心语义，多线程池会乱序）；
 *   - sendNoWait（高频 fire-and-forget：moverel/wheel/absMove）→ 同一单线程队列
 *     enqueue（不阻塞调用方、也保序），空闲自动退出线程（10s）。**不再直接在调用线程
 *     execute()——旧实现虽然 okhttp enqueue 异步，但 toBody()/toString() 与请求构造
 *     本身安全，真正的主线程网络只在 send()；一并收口，两路径都不碰主线程网络。**
 */
class InputSender(
    private val client: OkHttpClient,
    private val baseUrl: String,
    private val token: String,
    private val sessionId: String,
) {
    private val jsonMediaType = "application/json; charset=utf-8".toMediaType()

    /** 保序单线程：send 与 sendNoWait 共用（FIFO），点击/按键顺序不会被线程池打乱。 */
    private val inputDispatcher: ExecutorService = Executors.newSingleThreadExecutor { r ->
        Thread(r, "sll-input").apply { isDaemon = true }
    }

    /** 低延迟光标回显（P0-1）：服务端每条响应带 cursor{nx,ny,visible}（GetCursorPos 实测）。 */
    var onCursor: ((nx: Double, ny: Double, visible: Boolean) -> Unit)? = null

    private fun parseCursor(body: String?) {
        if (body == null) return
        try {
            val obj = org.json.JSONObject(body)
            val cur = obj.optJSONObject("cursor") ?: return
            val nx = cur.optDouble("nx", -1.0)
            val ny = cur.optDouble("ny", -1.0)
            val visible = cur.optBoolean("visible", false)
            if (nx >= 0 && ny >= 0) onCursor?.invoke(nx, ny, visible)
        } catch (_: Exception) { }
    }

    fun toBody(a: InputAction): JSONObject {
        val j = JSONObject()
        j.put("action", a.action)
        when (a) {
            is InputAction.Click -> { j.put("x", a.x); j.put("y", a.y); j.put("button", a.button) }
            is InputAction.Down -> { j.put("x", a.x); j.put("y", a.y); j.put("button", a.button) }
            is InputAction.Up -> {
                j.put("button", a.button)
                // T36b：带坐标的 up（拖动收尾）——后端 :661-663 dto.x >= 0 时先定位再抬起；
                // 缺省 -1 不写字段（纯抬起，JSON 缺字段 → dto.x 默认 -1，与旧行为一致）。
                if (a.x >= 0) j.put("x", a.x)
                if (a.y >= 0) j.put("y", a.y)
            }
            is InputAction.Move -> { j.put("x", a.x); j.put("y", a.y) }
            is InputAction.MoveRel -> { j.put("dx", a.dx); j.put("dy", a.dy) }
            is InputAction.Wheel -> j.put("delta", a.delta)
            // ★ T36：键盘/文本动作（字段契约见 AgentWebServer.cs ExecuteInput :653/:664/:671：
            //   按键名走 "key"、文本走 "text"、重复次数走 "count"）。
            is InputAction.Key -> { j.put("key", a.key); j.put("count", a.count) }
            is InputAction.KeyDown -> j.put("key", a.key)
            is InputAction.KeyUp -> j.put("key", a.key)
            is InputAction.Text -> j.put("text", a.text)
            InputAction.Noop, InputAction.ReleaseAll -> {}
        }
        j.put("sid", sessionId)
        return j
    }

    /**
     * 阻塞等回包（调用方应在协程里调，如 scope.launch { send(...) }）。
     * ★ 网络全部在 sll-input 单线程执行——主线程零网络，StrictMode 不再触发。
     */
    suspend fun send(a: InputAction): Boolean = withContext(inputDispatcher.asCoroutineDispatcher()) {
        val body = toBody(a).toString().toRequestBody(jsonMediaType)
        try {
            client.newCall(buildRequest(body)).execute().use { 
                if (it.isSuccessful) parseCursor(it.body?.string())
                it.isSuccessful 
            }
        } catch (e: IOException) {
            false
        }
    }

    /**
     * 高频 fire-and-forget（moverel/wheel/absMove）。进同一单线程队列（保序、不占主线程），
     * 不等回包。okhttp 异步回调在 sll-input 线程收尾。
     */
    fun sendNoWait(a: InputAction) {
        inputDispatcher.execute {
            try {
                val body = toBody(a).toString().toRequestBody(jsonMediaType)
                client.newCall(buildRequest(body)).enqueue(object : Callback {
                    override fun onFailure(call: Call, e: IOException) {}
                    override fun onResponse(call: Call, response: Response) {
                        try { if (response.isSuccessful) parseCursor(response.body?.string()) } catch (_: Exception) {}
                        response.close()
                    }
                })
            } catch (_: Exception) {
                // 队列关闭后（release 后）到达的残余事件：静默
            }
        }
    }

    /** 断线/后台释放所有按住（幂等，重发无害）。 */
    fun releaseAll() { sendNoWait(InputAction.ReleaseAll) }

    /** 页面销毁时停线程（PlayerPlaceholderScreen onDispose 调；幂等）。 */
    fun shutdown() {
        try { inputDispatcher.shutdown() } catch (_: Exception) {}
    }

    private fun buildRequest(body: okhttp3.RequestBody): Request = Request.Builder()
        .url(baseUrl + "/api/input")
        .header("Content-Type", "application/json")
        .header("X-SoulLink-Token", token)
        .post(body)
        .build()
}