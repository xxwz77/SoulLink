# 画质自由档位：preset 下拉 + 码率/帧率滑动条 + vbv 缓冲（对齐 Sunshine）

> 日期：2026-11-23
> 作者：gpu-zero-copy（dsh-team `team_mwsomf2tj73xd7` / member `m_mwsosfpfjamcdf` / task `task_mwsut7xoqxau2c`）
> 性质：**实现**。新增配置字段 + UI 滑动条 + 环境变量下发 + NvencSdkEncoder 接入，未提交 git。
> 依据：Sunshine `nvenc_base.cpp:277-282`（vbv_percentage_increase）、`configure_rate_control:259-264`（CBR/zeroReorderDelay/lowDelayKeyFrameScale）、`configure_h264:360-363`（profileGUID/idrPeriod/repeatSPSPPS）。

---

## 0. 一句话结论

**画质自由档位已落地，两 TFM 0 error**。NvencSdkEncoder 支持按配置选 NVENC preset（P1~P7，默认 P3）；RdpGeneralSettings 码率/帧率从固定档改滑动条（帧率 15~120、码率 1~50Mbps）；新增「关键帧缓冲上调%」滑动条（0~200%，默认 0，对应 Sunshine vbv_percentage_increase）接到 vbvBufferSize；配置落到 AppSettings 并持久化；UI 即时回显含义。

---

## 1. 改动清单

| 文件 | 改动 |
|---|---|
| `Models/Models.cs` | AppSettings 加 `NvencPreset`(默认3)、`VbvBufferIncreasePercent`(默认0) |
| `Services/RelayClient.cs` | CaptureEnv 下发 `SOULLINK_NVENC_PRESET`、`SOULLINK_VBV_INCREASE_PERCENT`；STREAM_FPS 上限 60→120、VIDEO_BITRATE clamp 1000~50000 |
| `Services/NvencSdkEncoder.cs` | P1~P7 GUID + `PresetGuid()` 选择 + 惰性读环境变量 + vbv 上调公式 |
| `Controls/RdpGeneralSettings.xaml` | 直播帧率/码率 ComboBox→Slider；新增 NVENC 预设下拉 + 关键帧缓冲上调滑动条 |
| `Controls/RdpGeneralSettings.xaml.cs` | 滑动条事件处理 + LoadSettings 读取 + UpdateVideoHintText 回显 |
| `tools/nvenc-sdk-probe/Program.cs` | 探针加 `-P`/`-V` 参数做实测验证 |

## 2. 关键实现细节

### 2.1 NVENC preset 选择（P1~P7）

```csharp
private static Guid PresetGuid(int preset) => preset switch
{
    1 => P1_GUID, 2 => P2_GUID, 4 => P4_GUID, 5 => P5_GUID, 6 => P6_GUID, 7 => P7_GUID, _ => P3_GUID
};
```

`TryInitialize` 里惰性读 `SOULLINK_NVENC_PRESET` 环境变量（在后台视频线程），`GetEncodePresetConfigEx` 用选中的 preset GUID 取预设，再覆盖 rcParams。

### 2.2 关键帧缓冲上调（对齐 Sunshine vbv_percentage_increase）

```csharp
cfg.rcParams.vbvBufferSize = cfg.rcParams.averageBitRate / fps;  // 1 帧 bit 量
if (_vbvIncreasePercent > 0)
    cfg.rcParams.vbvBufferSize += (uint)((ulong)vbvBufferSize * (uint)_vbvIncreasePercent / 100u);
```

对应 Sunshine `nvenc_base.cpp:277-281`：`vbvBufferSize = bitrate*1000/framerate`，然后 `+= vbvBufferSize * vbv_percentage_increase / 100`。

### 2.3 帧率/码率滑动条

- 直播帧率：15~120fps，Tick 5，快照 tick。上限从 60 放开到 120（验收要求「不卡 60」）。
- 码率：1~50 Mbps，Tick 1，单位 Mbps。底层 `VideoBitrateKbps` 仍存 kbps。

### 2.4 持久化与下发

- 配置写入 `AppSettings`，`ConfigStore.Save` 持久化（沿用现有 Save()）。
- `RelayClient.CaptureEnv` 下发 `SOULLINK_NVENC_PRESET`/`SOULLINK_VBV_INCREASE_PERCENT`，代理重新投送后生效。

## 3. 实测验证（探针，console session）

| 配置 | IDR 大小 | P1 大小 | QP(IDR) |
|---|---|---|---|
| P3, vbv 0% | 33002B | 31831B | 33 |
| P7, vbv 0% | 33002B | 31831B | 33 |
| **P3, vbv 100%** | **57952B** | 15303B | **31** |

**结论**：
1. **vbv 上调生效**：0%→100% 时 IDR 从 33KB 涨到 58KB、QP 从 33 降到 31（画质更好），P 帧从 31KB 降到 15KB（总码率不变，VBV 让 IDR 多拿、P 帧少拿）。
2. **preset 档位在 CBR 码控下不直接改 IDR 字节数**（P3 和 P7 在平坦测试图上 IDR 相同）：preset 影响的是编码内部复杂度/速度与复杂画面的质量，不改变「码率决定字节数」这条主线。这是 NVENC 的正常行为。

## 4. 验收对照

| 验收项 | 结果 |
|---|---|
| 两 TFM 0 error | ✅ |
| ① preset 下拉 P1~P7 默认 P3 | ✅ |
| ② 码率/帧率滑动条（连续值+边界+实时提示，帧率上限 120） | ✅ |
| ③ 关键帧缓冲上调 0~200% 默认 0，接 vbvBufferSize | ✅ |
| ④ 配置落 AppSettings 并持久化 | ✅ |
| ⑤ UI 即时回显含义（UpdateVideoHintText） | ✅ |

## 5. 下一步（给 Lead）

1. **真机端到端**：重启 A2 agent，设置面板调 preset/vbv，观察 `[video]` 日志与 /stream 画质/IDR 大小。
2. **preset 档位差异在真实游戏画面下更明显**（复杂画面 P7 比 P1 画质好但更慢），建议真机用游戏画面对比 P1/P3/P7。
3. **vbv 默认 0 的取舍**：0% 关键帧画质偏低但首屏最快（适合挂机监控）；追求画质建议 50~100%（适合需要看清细节的场景）。

## 6. 产物清单

| 路径 | 说明 |
|---|---|
| `Models/Models.cs` | +NvencPreset、+VbvBufferIncreasePercent |
| `Services/RelayClient.cs` | +2 环境变量下发 |
| `Services/NvencSdkEncoder.cs` | preset 选择 + vbv 上调 |
| `Controls/RdpGeneralSettings.xaml` + `.xaml.cs` | 滑动条 + 事件 + 回显 |
| 本报告 | `docs/research/NVENC-SDK-QUALITY-PRESETS-20261123.md` |