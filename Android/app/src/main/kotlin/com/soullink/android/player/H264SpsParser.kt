package com.soullink.android.player

import android.util.Log

/**
 * 精简 H.264 SPS 解析器：提取 profile/level/宽高。
 * 宽高用于 MediaFormat KEY_WIDTH/KEY_HEIGHT（解析失败则传 0，由解码器从 csd 推断）。
 * 覆盖 base + High profile 的 chroma/bitdepth/scaling 扩展。
 */
object H264SpsParser {
    data class SpsInfo(val profileIdc: Int, val levelIdc: Int, val width: Int, val height: Int)

    private val HIGH_PROFILES = intArrayOf(100, 110, 122, 244, 44, 83, 86, 118, 128, 138, 139, 134, 135)

    fun parse(sps: Nal): SpsInfo? {
        val bytes = sps.bytes
        if (bytes.size < 4) return null
        val br = BitReader(bytes)

        br.skipBits(8)                       // NAL header（type=7）
        val profileIdc = br.readU8()
        br.skipBits(8)                       // constraint flags + reserved_zero
        val levelIdc = br.readU8()
        br.readUe()                          // seq_parameter_set_id

        if (HIGH_PROFILES.contains(profileIdc)) {
            val idc = br.readUe()                       // chroma_format_idc
            if (idc == 3) br.skipBits(1)                // separate_colour_plane_flag
            br.readUe()                                 // bit_depth_luma_minus8
            br.readUe()                                 // bit_depth_chroma_minus8
            br.skipBits(1)                              // qpprime_y_zero_transform_bypass_flag
            if (br.readBit() == 1) {                    // seq_scaling_matrix_present_flag
                val count = if (idc != 3) 8 else 12
                for (i in 0 until count) {
                    if (br.readBit() == 1) skipScalingList(br, if (i < 6) 16 else 64)
                }
            }
        }

        br.readUe()                          // log2_max_frame_num_minus4
        val picOrderCntType = br.readUe()
        if (picOrderCntType == 0) {
            br.readUe()                      // log2_max_pic_order_cnt_lsb_minus4
        } else if (picOrderCntType == 1) {
            br.skipBits(1)                   // delta_pic_order_always_zero_flag
            br.readSe()                      // offset_for_non_ref_pic
            br.readSe()                      // offset_for_top_to_bottom_field
            val n = br.readUe()
            repeat(n) { br.readSe() }        // offset_for_ref_frame[]
        }

        br.readUe()                          // max_num_ref_frames
        br.skipBits(1)                       // gaps_in_frame_num_value_allowed_flag
        val picWidthInMbs = br.readUe() + 1
        val picHeightInMapUnits = br.readUe() + 1
        val frameMbsOnly = br.readBit()

        val width = picWidthInMbs * 16
        val height = picHeightInMapUnits * 16 * (if (frameMbsOnly == 1) 1 else 2)
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384) return null
        return SpsInfo(profileIdc, levelIdc, width, height)
    }

    private fun skipScalingList(br: BitReader, size: Int) {
        var lastScale = 8
        var nextScale = 8
        for (j in 0 until size) {
            if (nextScale != 0) {
                val delta = br.readSe()
                nextScale = (lastScale + delta + 256) % 256
            }
            if (nextScale != 0) lastScale = nextScale
        }
    }

    /**
     * ★ T35b（对齐 Moonlight MediaCodecDecoderRenderer.java:1441-1455）：
     * SPS level_idc 下修——部分解码器按 level 估算 DPB/输入 buffer 容量，
     * 服务端给的高 level 反而让「大 IDR 塞不进 input buffer」被判容量不足整 AU 丢弃。
     * 官方映射：720p→31（Level 3.1）、1080p→42（Level 4.2）；且只降不升。
     *
     * level_idc 位于 SPS 第 4 字节（NAL header + profileIdc + constraint 之后），
     * 固定字节、不经过 exp-golomb，直接改写安全。
     * 返回 null = 无需修改（原样用）。
     */
    fun downgradeLevelIdc(sps: Nal, width: Int, height: Int): Nal? {
        return try {
            val info = parse(sps) ?: return null
            val current = info.levelIdc
            val target = when {
                height <= 720 -> 31
                height <= 1080 -> 42
                else -> 51
            }
            if (current <= target || sps.bytes.size < 4) return null   // 已低于目标或异常：不动
            val out = sps.bytes.copyOf()
            out[3] = target.toByte()
            Log.i("SoulLinkPlayer", "SPS level_idc 下修：$current → $target（${width}x${height}，对齐 Moonlight）")
            Nal(sps.type, out)
        } catch (_: Exception) {
            null
        }
    }
}

/** 位读取器：ue(v)/se(v)/u(n)（越界返回 0 防崩，正常 SPS 不会越界）。 */
private class BitReader(private val bytes: ByteArray) {
    private var pos = 0

    fun readBit(): Int {
        if (pos / 8 >= bytes.size) return 0
        val byte = bytes[pos / 8].toInt() and 0xFF
        val bit = (byte shr (7 - (pos % 8))) and 1
        pos++
        return bit
    }

    fun readBits(n: Int): Int {
        var v = 0
        repeat(n) { v = (v shl 1) or readBit() }
        return v
    }

    fun readU8(): Int = readBits(8)

    fun readUe(): Int {
        var zeros = 0
        while (readBit() == 0) {
            zeros++
            if (zeros > 32) return 0
        }
        if (zeros == 0) return 0
        return (1 shl zeros) - 1 + readBits(zeros)
    }

    fun readSe(): Int {
        val ue = readUe()
        val k = (ue + 1) / 2
        return if (ue % 2 == 0) -k else k
    }

    fun skipBits(n: Int) { repeat(n) { readBit() } }
}
