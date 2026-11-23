package com.soullink.android.player

import okhttp3.Call
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.IOException

/**
 * OkHttp 流式拉取 /stream（H.264 Annex-B 裸流，chunked）。
 * 逐字节切 NAL → 攒 AU，回调 onAu。
 * 对齐 web-index.html 的「reader.read() → splitAnnexB → AU 攒批」。
 */
class H264StreamReader(
    private val client: OkHttpClient,
    private val streamUrl: String,
    private val token: String,
) {
    @Volatile private var cancelled = false
    private var call: Call? = null

    var onAu: ((Au) -> Unit)? = null
    var onError: ((Throwable) -> Unit)? = null
    var onEof: (() -> Unit)? = null

    /** 中断阻塞的 read（OkHttp Call.cancel 关闭底层连接）。 */
    fun cancel() {
        cancelled = true
        call?.cancel()
    }

    /** 阻塞式拉流（应在 IO 线程/协程调用），返回时流已结束/取消/出错。 */
    fun run() {
        val request = Request.Builder()
            .url(streamUrl)
            .header("Cache-Control", "no-store")
            .apply { if (token.isNotEmpty()) header("X-SoulLink-Token", token) }
            .build()
        val c = client.newCall(request)
        call = c
        try {
            c.execute().use { resp ->
                if (!resp.isSuccessful || resp.body == null) {
                    onError?.invoke(IOException("HTTP " + resp.code))
                    return
                }
                val source = resp.body!!.source()
                val splitter = AnnexBSplitter()
                val builder = AuBuilder()
                var ts = 0L
                val buf = ByteArray(64 * 1024)
                while (!cancelled) {
                    val n = source.read(buf)
                    if (n == -1) break
                    val nals = splitter.push(buf, 0, n)
                    for (nal in nals) {
                        val au = builder.push(nal, ts)
                        if (au != null) {
                            onAu?.invoke(au)
                            ts += TIMESTAMP_STEP_US
                        }
                    }
                }
                for (nal in splitter.flush()) {
                    val au = builder.push(nal, ts)
                    if (au != null) {
                        onAu?.invoke(au)
                        ts += TIMESTAMP_STEP_US
                    }
                }
                builder.flush()?.let { onAu?.invoke(it) }
                if (!cancelled) onEof?.invoke()
            }
        } catch (e: IOException) {
            if (!cancelled) onError?.invoke(e)
        } finally {
            call = null
        }
    }

    companion object {
        /** 30fps 节拍（服务端默认 streamFps=30，clamp 30-60）。 */
        private const val TIMESTAMP_STEP_US = 33_333L
    }
}
