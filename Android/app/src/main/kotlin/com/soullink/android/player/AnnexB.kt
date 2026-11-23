package com.soullink.android.player

import java.io.ByteArrayOutputStream

/**
 * H.264 Annex-B NAL（已去掉起始码，含 1 字节 NAL header）。
 */
class Nal(val type: Int, val bytes: ByteArray) {
    val isSps: Boolean get() = type == 7
    val isPps: Boolean get() = type == 8
    val isKey: Boolean get() = type == 5
    val isVcl: Boolean get() = type in 1..5
    val size: Int get() = bytes.size
}

/**
 * 一个 access unit：一个可独立解码的编码帧（关键帧含 SPS/PPS/IDR）。
 */
class Au(val nals: List<Nal>, val hasKey: Boolean, val timestampUs: Long) {
    val hasData: Boolean get() = nals.any { it.isVcl }
}

/**
 * Annex-B 字节流 → NAL 切分。
 * 逐字节状态扫描，起始码区 = 遇到 1 时的整段连零 + 这个 1（3/4 字节起始码及前导零都正确）。
 * 对齐 web-index.html 的 splitAnnexB（docs/web-index.html L1002-1033）。
 */
class AnnexBSplitter {
    private val pending = ByteArrayOutputStream()

    fun push(data: ByteArray, offset: Int, length: Int): List<Nal> {
        pending.write(data, offset, length)
        return drain(full = false)
    }

    /** 流结束：把最后一段（最后一个起始码之后）也切出。 */
    fun flush(): List<Nal> = drain(full = true)

    private fun drain(full: Boolean): List<Nal> {
        val buf = pending.toByteArray()
        pending.reset()

        val starts = ArrayList<IntArray>()
        var zeros = 0
        for (i in buf.indices) {
            val b = buf[i].toInt() and 0xFF
            if (b == 0) { zeros++; continue }
            if (b == 1 && zeros >= 2) {
                starts.add(intArrayOf(i - zeros, zeros + 1))
                zeros = 0
                continue
            }
            zeros = 0
        }

        if (starts.isEmpty()) {
            // 无起始码：流头垃圾或前缀未到齐，全部保留；超限清空防内存涨。
            if (buf.size > MAX_PENDING) return emptyList()
            pending.write(buf)
            return emptyList()
        }

        val nals = ArrayList<Nal>()
        for (k in 0 until starts.size - 1) {
            val from = starts[k][0] + starts[k][1]
            val to = starts[k + 1][0]
            if (to > from) nals.add(makeNal(buf, from, to))
        }

        val last = starts[starts.size - 1]
        if (full) {
            if (buf.size - last[0] - last[1] > 0) nals.add(makeNal(buf, last[0] + last[1], buf.size))
        } else {
            val tailLen = buf.size - last[0]
            if (tailLen > MAX_PENDING) return nals
            pending.write(buf, last[0], tailLen)
        }
        return nals
    }

    private fun makeNal(buf: ByteArray, from: Int, to: Int): Nal {
        val bytes = buf.copyOfRange(from, to)
        val type = (bytes[0].toInt() and 0xFF) and 0x1F
        return Nal(type, bytes)
    }

    companion object {
        private const val MAX_PENDING = 4 * 1024 * 1024
    }
}

/**
 * AU（access unit）攒批：SPS/PPS/SEI/新 VCL 出现 = 上一个 AU 结束。
 * 对齐 web-index.html 的 flushAu（docs/web-index.html L1144-1189）。
 */
class AuBuilder {
    private val nals = ArrayList<Nal>()
    private var hasKey = false
    private var hasData = false
    private var timestampUs = 0L

    /** 喂入一个 NAL；若触发了 AU 边界，返回被「结掉」的 AU（null = 还在攒）。 */
    fun push(nal: Nal, timestampUs: Long): Au? {
        var flushed: Au? = null
        if (nal.isVcl && hasData) flushed = flush()
        else if ((nal.isSps || nal.isPps) && hasData) flushed = flush()

        nals.add(nal)
        if (nal.isVcl) hasData = true
        if (nal.isKey) hasKey = true
        this.timestampUs = timestampUs
        return flushed
    }

    fun flush(): Au? {
        if (!hasData || nals.isEmpty()) {
            reset()
            return null
        }
        val au = Au(nals.toList(), hasKey, timestampUs)
        reset()
        return au
    }

    private fun reset() {
        nals.clear()
        hasKey = false
        hasData = false
    }
}
