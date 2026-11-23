# 非N卡低延迟：手写 ICodecAPI 设 AVLowLatencyMode/关B帧/CBR（码流实测验证）

> 日期：2026-11-23
> 作者：gpu-zero-copy（dsh-team `team_mwsomf2tj73xd7` / member `m_mwsosfpfjamcdf` / task `task_mwsxb4jktyfsh4`）
> 性质：**实现 + 码流实测**。新增 `Services/CodecApi.cs`（ICodecAPI 手写互操作）+ 接入 `MfFrameEncoder`；独立码流验证探针 `tools/mf-lowlatency-probe/`。未提交 git。
> 依据：FFmpeg `mfenc.c` / `mf_utils.h`（GitHub 抓取，代理已通）+ MSDN `codecapi-avlowlatencymode` + 已有 `ICODECAPI-PROBE-20261123.md`（SetValue 读回 E_NOTIMPL 的坑）。

---

## 0. 一句话结论

**手写 ICodecAPI 已落地并接入 MF 编码器，两 TFM 0 error**。但**码流级实测证明：微软软 H.264 MFT 对这些低延迟码控属性「SetValue 返回 S_OK 但完全忽略」**——设不设 ICodecAPI 产出的 H.264 字节级完全相同（B 帧都还是 20 个）。**这是软 MFT 的固有限制，不是代码 bug**；QSV/AMF 硬件编码器才真正响应这些属性（逻辑已同构，留待有硬件机器验证）。

---

## 1. 关键发现（码流实测，最重要）

用独立探针 `tools/mf-lowlatency-probe/` 对微软软 H.264 MFT（`H264 Encoder MFT`）编码 60 帧固定图案，三种模式对比：

| 模式 | 产出帧 | 总字节 | MD5 | B 帧数 | I 帧数 | P 帧数 |
|---|---|---|---|---|---|---|
| 不设 ICodecAPI（基线） | 43 | 168748 | 98824D53... | **20** | 2 | 21 |
| 设 ICodecAPI（SetOutputType 前） | 43 | 168748 | **98824D53...（相同）** | **20** | 2 | 21 |
| 设 ICodecAPI（SetOutputType 后） | 43 | 168748 | **98824D53...（相同）** | **20** | 2 | 21 |

**结论：三个模式的输出字节级完全相同（MD5 一致），B 帧数量纹丝不动（都是 20 个 IBBP 结构）。**

四个属性的 SetValue 都返回 `0x00000000`（S_OK），但：
1. `AVLowLatencyMode=1` → 不生效（B 帧照旧）
2. `AVEncMPVDefaultBPictureCount=0` → 不生效（B 帧照旧 20 个）
3. `AVEncCommonRateControlMode=CBR` → 不生效（码流字节相同）
4. `AVEncCommonMeanBitRate` → 不生效（码流字节相同）

> ★ 这比前序 `ICODECAPI-PROBE` 的「GetValue 返回 E_NOTIMPL」更硬：**SetValue 返回 S_OK 也不代表生效**。软 MFT 对低延迟码控属性是「静默忽略」的。

---

## 2. FFmpeg 权威参考（GitHub 已抓取，代理已通）

抓取了 FFmpeg 三个权威实现（存 `artifacts/`，GPL/LGPL 只读参考）：

| 文件 | 关键结论 | file:line |
|---|---|---|
| `libavcodec/mfenc.c` | **微软 MF 编码器封装**：ICodecAPI 所有 SetValue 必须在 `SetOutputType` **之前**；`AVLowLatencyMode` 用 **VT_UI4(1)** 非 VT_BOOL；`AVEncMPVDefaultBPictureCount=avctx->max_b_frames`；`AVEncCommonRateControlMode=opt_enc_rc` | L869-911 |
| `libavcodec/mf_utils.h` | GUID 定义：AVLowLatencyMode=`9c27891a...`、AVEncMPVDefaultBPictureCount=`8d390aac...`、AVEncCommonRateControlMode=`1c0608e9...`；`ff_eAVEncCommonRateControlMode_CBR=0` | L86,100,105,129 |
| `libavcodec/qsvenc.c` | **QSV 低延迟**：`LowDelayBRC`（extco3）、`AsyncDepth`、`GopPicSize=avctx->gop_size`、`GopRefDist=max_b_frames+1` | L1146-1147, 1649, 815-817 |
| `libavcodec/amfenc_h264.c` | **AMF 低延迟**：`usage=AMF_VIDEO_ENCODER_USAGE_ULTRA_LOW_LATENCY`、`max_consecutive_b_frames` | L35-41, 125 |

### 2.1 mfenc.c 关键时序（决定了我探针的正确写法）

```c
// mfenc.c L869-870：ICodecAPI 必须在 SetOutputType 之前设
// Note that some of the ICodecAPI options must be set before SetOutputType.
if (c->codec_api) {
    ICodecAPI_SetValue(c->codec_api, &ff_CODECAPI_AVEncCommonMeanBitRate, FF_VAL_VT_UI4(avctx->bit_rate));
    ICodecAPI_SetValue(c->codec_api, &ff_CODECAPI_AVEncCommonRateControlMode, FF_VAL_VT_UI4(c->opt_enc_rc));
    ICodecAPI_SetValue(c->codec_api, &ff_CODECAPI_AVEncMPVDefaultBPictureCount, FF_VAL_VT_UI4(avctx->max_b_frames));
    ...
    // mfenc.c L911：AVLowLatencyMode 用 VT_UI4(1)，不是 VT_BOOL
    if (avctx->flags & AV_CODEC_FLAG_LOW_DELAY)
        ICodecAPI_SetValue(c->codec_api, &ff_CODECAPI_AVLowLatencyMode, FF_VAL_VT_UI4(1));
}
```

### 2.2 QSV/AMF 低延迟要点（同构逻辑，留待有硬件验证）

- **QSV**：`LowDelayBRC`（低延迟码率控制）+ `GopPicSize=0`（无限 GOP）+ `GopRefDist=max_b_frames+1`（关 B 帧 → 1）+ `AsyncDepth=1`（最小流水线）。
- **AMF**：`usage=ULTRA_LOW_LATENCY` + `max_consecutive_b_frames=0`（关 B 帧）。

---

## 3. 代码落地

### 3.1 新增 `Services/CodecApi.cs`（ICodecAPI 手写互操作）

- IID `901db4c7-31ce-41a2-85dc-8fa0bf41b8da`；IUnknown 占槽位 0~2，SetValue/GetValue 是槽位 3/4。
- `VariantUi4()` 造 16 字节 VARIANT（VT_UI4，对齐 ffmpeg）。
- `TrySet()` 静默降级：失败只记 HRESULT，不抛不报错。

### 3.2 `MfFrameEncoder` 接入点（SetOutputType 之前）

在 `_transform.SetOutputType` 之前（L173-198），QI 出 `CodecApi` 并设 4 个属性：
`AVLowLatencyMode=1`、`AVEncMPVDefaultBPictureCount=0`、`AVEncCommonRateControlMode=CBR(0)`、`AVEncCommonMeanBitRate=配置码率`。

> ★ 顺序已按 ffmpeg mfenc.c L869 的坑做到「SetOutputType 之前」。对软 MFT 无效（§1 实测），但对 NVIDIA MF MFT / QSV / AMF 硬件编码器这是正确的调用时序。

---

## 4. 验收对照

| 验收项 | 结果 |
|---|---|
| 两 TFM 0 error | ✅ |
| ① 手写 ICodecAPI 设 AVLowLatencyMode + DefaultBPictureCount=0 + CBR | ✅（CodecApi.cs + MfFrameEncoder 接入） |
| ② 不依赖 GetValue，改用 ffprobe 码流实测（数 B 帧） | ✅（探针三模式对比 + ffprobe B 帧统计） |
| ③ 参考 icodecapi-probe + MSDN codecapi-avlowlatencymode | ✅（复用 IID/槽位/VARIANT 结论 + 补 ffmpeg 权威） |
| ④ SetValue 失败静默降级 | ✅（TrySet 只记 HRESULT 不抛） |
| ⑤ 本机无 QSV/AMF，用软 MFT 验证，QSV/AMF 同构留待硬件 | ✅（软 MFT 实测 + QSV/AMF 结论已记 §2.2） |
| ⑥ 不引框架不碰运行进程 | ✅（只加 Vortice/SharpGen 已有依赖） |

---

## 5. 诚实边界（给 Lead 的决策输入）

1. **微软软 H.264 MFT 不响应低延迟码控属性**（码流级实锤，SetValue S_OK 但完全忽略）。这意味着：**纯软编路径下，AVLowLatencyMode/关B帧/CBR 这条路走不通**，软编的低延迟只能靠「换硬编」或「拉小 GOP」（`MaxKeyframeSpacing`，这个软 MFT 是响应的，已在 MfFrameEncoder L172 设 60）。
2. **真正能吃到这些属性的，是 NVIDIA/QSV/AMF 硬件编码器**。NVENC 已经走 SDK 直通（NvencSdkEncoder，rcParams 精确控制 CBR/关B帧/GOP），不需要 ICodecAPI；QSV/AMF 的 MF 封装 MFT 才需要这套 ICodecAPI 逻辑，但本机无 A/I 卡无法验证。
3. **`MaxKeyframeSpacing=60` 是软 MFT 唯一确证生效的低延迟手段**（GOP 拉小，新观众重同步快，不依赖 ICodecAPI）。

---

## 6. 产物清单

| 路径 | 说明 |
|---|---|
| `Services/CodecApi.cs` | ICodecAPI 手写互操作（IID/槽位/VARIANT，对齐 ffmpeg） |
| `Services/MfFrameEncoder.cs` | SetOutputType 前接入 CodecApi（低延迟/关B帧/CBR） |
| `tools/mf-lowlatency-probe/` | 码流验证探针（三模式对比 + ffprobe） |
| `artifacts/mfenc.c`、`mf_utils.h`、`qsvenc.c`、`amfenc_h264.c` | FFmpeg 权威参考（GPL/LGPL 只读） |
| 本报告 | `docs/research/MF-LOWLATENCY-20261123.md` |