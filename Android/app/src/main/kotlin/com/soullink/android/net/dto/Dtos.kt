package com.soullink.android.net.dto

import org.json.JSONObject

/**
 * 会话列表响应：GET /api/sessions 返回的顶层结构。
 * 字段对应 Services/WebServer.cs ApiSessions() 的匿名对象。
 */
data class SessionListDto(
    val ok: Boolean,
    val version: String,
    val themeDark: Boolean,
    val directToken: String,
    val sessions: List<SessionDto>,
) {
    companion object {
        fun fromJson(json: JSONObject): SessionListDto {
            val arr = json.optJSONArray("sessions") ?: org.json.JSONArray()
            val list = mutableListOf<SessionDto>()
            for (i in 0 until arr.length()) {
                list.add(SessionDto.fromJson(arr.optJSONObject(i) ?: JSONObject()))
            }
            return SessionListDto(
                ok = json.optBoolean("ok"),
                version = json.optString("version"),
                themeDark = json.optBoolean("themeDark", true),
                directToken = json.optString("directToken"),
                sessions = list,
            )
        }
    }
}

/**
 * 单个账号会话。directPort=0 表示该账号无直连代理，需走主程序转发（本脚手架先占位）。
 */
data class SessionDto(
    val id: String,
    val user: String,
    val nick: String,
    val label: String,
    val sessionId: Int,
    val online: Boolean,
    val directPort: Int,
    val screen: String,
) {
    companion object {
        fun fromJson(o: JSONObject): SessionDto = SessionDto(
            id = o.optString("id"),
            user = o.optString("user"),
            nick = o.optString("nick"),
            label = o.optString("label"),
            sessionId = o.optInt("sessionId"),
            online = o.optBoolean("online"),
            directPort = o.optInt("directPort"),
            screen = o.optString("screen"),
        )
    }
}

data class LoginResult(val ok: Boolean, val error: String?) {
    companion object {
        fun fromJson(json: JSONObject) = LoginResult(
            ok = json.optBoolean("ok"),
            error = if (json.optBoolean("ok")) null else json.optString("error"),
        )
    }
}
