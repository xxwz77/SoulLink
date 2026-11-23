package com.soullink.android.player

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import okhttp3.Call
import okhttp3.Callback
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import org.json.JSONObject
import java.io.IOException

/**
 * POST /api/input（代理直连，X-SoulLink-Token 头 + body 带 sid）。
 * send = 阻塞等回包（down/up/click 保序用）；sendNoWait = fire-and-forget（move/moverel 高频用）。
 */
class InputSender(
    private val client: OkHttpClient,
    private val baseUrl: String,
    private val token: String,
    private val sessionId: String,
) {
    private val jsonMediaType = "application/json; charset=utf-8".toMediaType()

    fun toBody(a: InputAction): JSONObject {
        val j = JSONObject()
        j.put("action", a.action)
        when (a) {
            is InputAction.Click -> { j.put("x", a.x); j.put("y", a.y); j.put("button", a.button) }
            is InputAction.Down -> { j.put("x", a.x); j.put("y", a.y); j.put("button", a.button) }
            is InputAction.Up -> j.put("button", a.button)
            is InputAction.Move -> { j.put("x", a.x); j.put("y", a.y) }
            is InputAction.MoveRel -> { j.put("dx", a.dx); j.put("dy", a.dy) }
            is InputAction.Wheel -> j.put("delta", a.delta)
            InputAction.Noop, InputAction.ReleaseAll -> {}
        }
        j.put("sid", sessionId)
        return j
    }

    suspend fun send(a: InputAction): Boolean {
        val body = toBody(a).toString().toRequestBody(jsonMediaType)
        return try {
            client.newCall(buildRequest(body)).execute().use { it.isSuccessful }
        } catch (e: IOException) {
            false
        }
    }

    fun sendNoWait(a: InputAction) {
        val body = toBody(a).toString().toRequestBody(jsonMediaType)
        client.newCall(buildRequest(body)).enqueue(object : Callback {
            override fun onFailure(call: Call, e: IOException) {}
            override fun onResponse(call: Call, response: Response) { response.close() }
        })
    }

    /** 断线/后台释放所有按住（幂等，重发无害）。 */
    fun releaseAll() { sendNoWait(InputAction.ReleaseAll) }

    private fun buildRequest(body: okhttp3.RequestBody): Request = Request.Builder()
        .url(baseUrl + "/api/input")
        .header("Content-Type", "application/json")
        .header("X-SoulLink-Token", token)
        .post(body)
        .build()
}
