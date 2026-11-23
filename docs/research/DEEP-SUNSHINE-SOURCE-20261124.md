# Sunshine Windows 源码深挖：GPU 直通、设备拓扑、光标与帧节拍

> 日期：2026-11-24；作者：`deep-sunshine`；团队：`team_mwtjn0d77v8gtr`；任务：`task_mwtkvj5oe74mgq`。
> 性质：**只读源码研究，未改产品代码，未启动/停止任何程序，未使用任何账号。**
> 目标：给出可以直接对照的真实 C++，而不是二手架构描述；文末列出 SoulLink 的具体函数与预期收益。
> 证据：【源码】＝本地 `artifacts/zero-copy-src/` 的 Sunshine 官方源码，行号均指该快照；【在线复核】＝GitHub API/官方 Microsoft Learn；【SoulLink 事实】＝本次读取工作区 HEAD `c744003`；【推断】＝设计或收益判断，不是实测。
> 许可：Sunshine/OBS 为 GPL-3.0；下面的短摘录用于对照分析，**不能把 Sunshine 实现整段复制进 SoulLink**。

## 0. 先给结论，并纠正旧文档

### 六个问题的最短答案

| 问题 | 真实实现 |
|---|---|
| GPU 纹理如何直接进 NVENC，有无 CPU 回读？ | DDA/WGC 原始 surface → 采集设备上的私有 `capture_texture`（`CopyResource`）→ 编码设备打开共享纹理 → keyed mutex 内用 **D3D11 像素着色器**渲染 Y、UV 平面 → NVENC 注册的同一张 NV12 输入纹理 → `MapInputResource` → `EncodePicture`。**像素输入不走 CPU**；但压缩比特流必然经 `LockBitstream` 复制到 CPU 后再发包。 |
| VideoProcessor 用没用？ | **这条 Windows VRAM→NVENC 路径完全没用**。无 `ID3D11VideoDevice`、`ID3D11VideoContext`、VideoProcessor 对象或 `VideoProcessorBlt`。用 VS/PS、SRV/RTV、`Draw`。 |
| DDA duplication 建在哪个 device？ | 建在 `display_base_t::device`，即**采集设备**；**不是 NVENC 的编码设备**。`d3d_base_encode_device::init()` 在同一 adapter 上再次 `D3D11CreateDevice`，编码 NV12 纹理和 NVENC 会话用这个新设备。二者通过 NT 共享句柄 + keyed mutex 连接。 |
| `SetMaximumFrameLatency` 用在哪？ | `display_base_t::init()` 创建**采集设备**之后，对其 `IDXGIDevice1` 调 `SetMaximumFrameLatency(1)`；四份目标源文件中没有对编码设备调用它。它限制的是渲染命令排队，不是 DDA 出帧节流，也不是 NVENC 内部缓冲参数。 |
| 光标如何合成？ | DDA：从 `GetFramePointerShape` 取形状，在 CPU 上仅把形状拆成 alpha/XOR 两个小图并上传为 GPU 纹理；之后以两个全屏三角形、alpha blend / invert blend 把光标画到 `capture_texture`。**仅鼠标变化、没有新桌面 surface 时也会从上一帧复制底图并重新合成出帧。** WGC：启用 `IsCursorCaptureEnabled`，系统负责把光标放入 WGC 帧。 |
| pacing 与 capture 循环？ | 通用 `display_base_t::capture()` 管理 pacing group：组首 `snapshot(..., 200ms)`，组内按**源帧时间戳 + 有理数帧间隔**睡到绝对目标后 `snapshot(..., 0ms)`；错过目标或取不到帧则弃组。DDA 等帧超时后 sleep 10ms 让锁。WGC 另有 FrameArrived 生产者线程和条件变量；通用 pacing 同样适用。 |

### 必须纠正的三条旧结论

1. **错误：「Sunshine DDA 与 NVENC 是同一个 device、无需共享句柄」。**
   真实代码恰好相反：采集设备和编码设备分别创建；DDA 和 WGC 的 VRAM 路径都走 `CreateSharedHandle` / `OpenSharedResource1` / `IDXGIKeyedMutex`。受影响旧文：`DDA-TEXTURE-SHARED-HANDLE-20261124.md` §0/§2.1/§4，`AUDIT-20261124-01-SUNSHINE-CHAIN.md` §0/§2.2，`MATURE-GPU-ZERO-COPY-EXPERIENCE-20261124.md` §1 的相关概括。
2. **错误：「只有 WGC 跨设备，DDA 不跨设备」。** `display_vram_t::complete_img()` 是两类 VRAM display 共用的纹理创建代码；`d3d_base_encode_device::convert()` 是两类共用的编码侧打开/转换代码。
3. **错误：「`display_wgc.cpp` 的 staging+Map 证明 WGC→NVENC 有 CPU 回读」。** 那是独立 `display_wgc_ram_t`；NVENC 选择的是 `display_wgc_vram_t`，其实现位于 `display_vram.cpp:1860-1910`。

**对 SoulLink 的含义**：现有「独立采集 device + 独立编码 device」不是方向性错误。当前共享路径缺的是 Sunshine 已实现的 **NT handle、keyed mutex、按纹理缓存、只消费新帧**。把 DDA 改到编码 device 上可以作为 SoulLink 自己的简化方案，但不能再称为「Sunshine 同款/唯一正解」。

---

## 1. 版本、在线复核与证据边界

- 2026-11-24 在线取得 master commit [`433c876056c36f762428921a8b0a7760afe3911f`](https://github.com/SunshineStream/Sunshine/commit/433c876056c36f762428921a8b0a7760afe3911f)（2026-11-23T14:19:29Z）。
- 【在线复核】`display_base.cpp` 在线内容 SHA-256 与本地完全相同：`1F47E5D33959030D260B1E58120278869B9749A14138E8FD82FF906DDEAF3A99`；blob `f97b6933d7a82c98c2e9009cd3ebdf41ded70c8f`。因此本文的 device/pacing 引文可在线对照。
- 当前 `src/nvenc/` 在线目录有 `nvenc_base.cpp`，blob `42101b626b520b48b29783aafa28a43cdbb59963`；没有 `nvenc_encode.cpp`。对该路径的 contents 请求为 404，path-history 查询为空。
- raw.githubusercontent.com 的多次读取被连接重置，后续 GitHub API 又触发匿名限流；**其余文件没有完成逐字节 commit 比对**。它们是本仓库前序任务下载的真实官方源码，但精确对应 commit 未证实，不把本地行号冒充永久 upstream 行号。
- 文件分工已改：当前没有 `src/nvenc/nvenc_encode.cpp` 或 `src/video/nvenc_encode.cpp`。本文以 `nvenc_base.cpp` + `nvenc_d3d11_native.cpp` + `src/video.cpp`（本地名 `sunshine-video.cpp`）中的 `nvenc_encode_session_t` 覆盖其职责。结论均有真实源码，无「未取得源码」的六项空洞。
- 本文没有在本机运行 Sunshine，也没有测出 SoulLink 改动的实际毫秒收益；收益均明确标为推断。

---

## 2. 一张真实设备/纹理图

```text
采集线程（display_base_t）
  capture ID3D11Device / immediate context   ← display_base.cpp:591-602
       │
       ├─ DDA: DuplicateOutput1/DuplicateOutput(display->device)
       │        display_base.cpp:88 / :116
       ├─ WGC: CreateDirect3D11DeviceFromDXGIDevice(display->device)
       │        display_wgc.cpp:112-117
       │
       └─ 私有 img_d3d_t.capture_texture
            DEFAULT | SRV | RTV | SHARED_NTHANDLE | SHARED_KEYEDMUTEX
            display_vram.cpp:1968-1979
            CopyResource(DDA/WGC source → capture_texture)
            DDA :1643/:1728；WGC :1897
                     │ CreateSharedHandle(READ) :2005-2007
                     ▼
编码线程 / d3d_base_encode_device
  另一个 ID3D11Device / immediate context     ← display_vram.cpp:875-886
       │ OpenSharedResource1 + AcquireSync(0, INFINITE)
       │ display_vram.cpp:1002-1017 / :513-518
       │
       ├─ PS draw Y  → NVENC 自有 NV12 纹理的 R8_UNORM RTV（plane 0）
       └─ PS draw UV → 同一 NV12 纹理的 R8G8_UNORM RTV（plane 1）
                         display_vram.cpp:520-553, :788-809
                         NV12 输入纹理 : nvenc_d3d11_native.cpp:37-50
                                      │ NVENC RegisterResource(DIRECTX)
                                      ▼
                         Map → EncodePicture → LockBitstream
                         nvenc_base.cpp:715-796
                                      │ 仅编码后的 H.264/HEVC/AV1 bytes 复制到 CPU
                                      ▼
                                  packet queue
```

这不是「BGRA 直接注册给 NVENC」，而是**共享 BGRA/FP16 采集纹理，再在编码设备上 GPU 转成 NVENC 自有输入纹理**。即使采用同一 D3D11 device，NV12 转换这一步也仍然存在。

---

## 3. DDA duplication 到底在哪个 device 上

### 3.1 创建采集 device

`display_base_t::init()` 在选定的 adapter 上创建 `display_base_t::device`：

```cpp
// display_base.cpp:585-602
status = adapter->QueryInterface(IID_IDXGIAdapter, (void **) &adapter_p);
status = D3D11CreateDevice(
  adapter_p,
  D3D_DRIVER_TYPE_UNKNOWN,
  nullptr,
  D3D11_CREATE_DEVICE_FLAGS,
  featureLevels.data(), featureLevels.size(), D3D11_SDK_VERSION,
  &device, &feature_level, &device_ctx
);
```

DDA 明确使用这个 `display->device`，优先 `IDXGIOutput5::DuplicateOutput1`，没有 Output5 时用 `IDXGIOutput1::DuplicateOutput`：

```cpp
// display_base.cpp:73-89, 105-117
dxgi::output5_t output5 {};
status = display->output->QueryInterface(IID_IDXGIOutput5, (void **) &output5);
// ...
status = output5->DuplicateOutput1(
  (IUnknown *) display->device.get(), 0,
  supported_formats.size(), supported_formats.data(), &dup);

// fallback
status = output1->DuplicateOutput((IUnknown *) display->device.get(), &dup);
```

DDA `AcquireNextFrame` 同样在这个采集对象里执行，先释放上一次 DDA 帧，再取下一帧：

```cpp
// display_base.cpp:150-179
auto capture_status = release_frame();
if (capture_status != capture_e::ok) return capture_status;
auto status = dup->AcquireNextFrame(timeout.count(), &frame_info, res_p);
// S_OK => has_frame=true；WAIT_TIMEOUT => timeout；
// WAIT_ABANDONED / ACCESS_LOST / ACCESS_DENIED => reinit
```

### 3.2 编码器另建 device，并绑定同一 adapter

```cpp
// display_vram.cpp:837-886, d3d_base_encode_device::init()
HRESULT status = D3D11CreateDevice(
  adapter_p,
  D3D_DRIVER_TYPE_UNKNOWN,
  nullptr,
  D3D11_CREATE_DEVICE_FLAGS | D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
  featureLevels.data(), featureLevels.size(), D3D11_SDK_VERSION,
  &device, nullptr, &device_ctx
);
```

这个 `device` 是 `d3d_base_encode_device::device`，不是 `display_base_t::device`。NVENC 原生对象收到的是它：

```cpp
// display_vram.cpp:1252-1266
if (base.init(display, adapter_p, pix_fmt)) return false;
// ...
nvenc_d3d = factory->create_nvenc_d3d11_native(base.device.get());
```

`src/video.cpp` 也明确把 capture 和 encode 放在不同线程：`captureThread()`（`sunshine-video.cpp:1778`）与 `capture_async()` 中的 encoding thread（`:3205-3206`）。NVENC 有 `PARALLEL_ENCODING`（`:824-865`），入口 `:3280-3282` 选择异步采集路径。锁注释甚至直接写明「between the capture and encoding ID3D11Devices」（`display_base.cpp:320-324`）。

### 3.3 不是共享 DDA/WGC 原始 surface，而是共享私有纹理

```cpp
// display_vram.cpp:1968-1979, complete_img()
D3D11_TEXTURE2D_DESC t {};
t.Width = img->width; t.Height = img->height;
t.MipLevels = 1; t.ArraySize = 1; t.SampleDesc.Count = 1;
t.Usage = D3D11_USAGE_DEFAULT;
t.Format = img->format;
t.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
t.MiscFlags = D3D11_RESOURCE_MISC_SHARED_NTHANDLE |
              D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
auto status = device->CreateTexture2D(&t, nullptr, &img->capture_texture);
```

句柄只创建一次，随图像实例持有；析构用 `CloseHandle`（`:192-196`）：

```cpp
// display_vram.cpp:1991-2007
img->capture_texture->QueryInterface(__uuidof(IDXGIKeyedMutex),
                                     (void **) &img->capture_mutex);
img->capture_texture->QueryInterface(__uuidof(IDXGIResource1),
                                     (void **) &resource);
resource->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ,
                             nullptr, &img->encoder_texture_handle);
```

这符合 Microsoft 对 [CreateSharedHandle](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgiresource1-createsharedhandle) 的约束：NT handle + keyed mutex 时用 `CreateSharedHandle`，不能用 `GetSharedHandle`；句柄只需创建一次，后续要更多句柄用 `DuplicateHandle`。

---

## 4. GPU 纹理如何进 NVENC，CPU 到底碰了什么

### 4.1 DDA 与 WGC 都先 CopyResource 到私有 GPU 纹理

DDA 在没有光标时直接拷到 image；有光标时先保留无光标底图，再拷到输出 image：

```cpp
// display_vram.cpp:1629-1645
auto [d3d_img, lock] = get_locked_d3d_img(img);
device_ctx->CopyResource(d3d_img->capture_texture.get(), src.get());
last_frame_variant = img;

// :1728-1729，光标输出帧
device_ctx->CopyResource(d3d_img->capture_texture.get(), p_surface->get());
blend_cursor(*d3d_img);
```

`snapshot()` 返回后，通用 capture 循环才 `release_snapshot()`；DDA 最终 `ReleaseFrame`（`display_vram.cpp:1774-1775`）。所以 DDA 临时 surface 的访问位于 Acquire/Release 生命周期内，而编码持有的是私有纹理。

WGC 的 VRAM 版本同样只有 GPU 拷贝：

```cpp
// display_vram.cpp:1860-1910, display_wgc_vram_t::snapshot()
dup.set_cursor_visible(cursor_visible);
auto capture_status = dup.next_frame(timeout, &src, frame_qpc);
// ...
if (complete_img(d3d_img.get(), false) == 0) {
  texture_lock_helper lock_helper(d3d_img->capture_mutex.get());
  if (lock_helper.lock()) {
    device_ctx->CopyResource(d3d_img->capture_texture.get(), src.get());
  }
}
```

`display_vram.cpp` 没有 staging、`D3D11_MAP_READ` 或 `UpdateSubresource`。CPU 只为光标形状和黑色清屏小纹理提供初始化 bytes，**不是每帧屏幕读回**。

### 4.2 编码设备缓存 OpenSharedResource1，不是每帧打开

```cpp
// display_vram.cpp:985-1027
if (img_ctx.encoder_texture &&
    img.capture_texture.get() == img_ctx.capture_texture_p) {
  return 0;
}
img_ctx.reset();
// ...
device1->OpenSharedResource1(img.encoder_texture_handle,
  __uuidof(ID3D11Texture2D), (void **) &img_ctx.encoder_texture);
img_ctx.encoder_texture->QueryInterface(__uuidof(IDXGIKeyedMutex),
                                        (void **) &img_ctx.encoder_mutex);
device->CreateShaderResourceView(img_ctx.encoder_texture.get(),
                                 nullptr, &img_ctx.encoder_input_res);
```

缓存以 `img.id` 为 key，用 `capture_texture_p` 检测同一 image 是否换了底层纹理，用 `weak_ptr` 回收失效 image（`:494-502`, `:955-975`, `:1081-1085`）。这是 Sunshine 跨设备路径减少固定开销的关键，不能简化为「每帧 Open」。

### 4.3 keyed mutex 的两端与准确的锁区间

生产端在 CopyResource / blend 前 `AcquireSync(0, INFINITE)`，锁的 RAII 析构 `ReleaseSync(0)`（`:241-269`）。消费端：

```cpp
// display_vram.cpp:504-560
auto &img = (img_d3d_t &) img_base;
auto &img_ctx = img_ctx_map[img.id];
if (initialize_image_context(img, img_ctx)) return -1;
auto status = img_ctx.encoder_mutex->AcquireSync(0, INFINITE);
if (status != S_OK) return -1;
// PS draw Y / UV 到 NVENC input texture
// ...
img_ctx.encoder_mutex->ReleaseSync(0);
```

这里两侧都用 key `0`。源文件没有显式 `ID3D11DeviceContext::Flush()`；本文不额外声称「Flush 是正确性必要条件」。ReleaseSync 的跨设备同步语义是代码实际使用的机制。若 SoulLink 选择两个设备并发访问共享纹理，也必须保持生产/消费互斥，不能只 Open + Copy。

### 4.4 NVENC 注册的是输出 NV12，而不是共享采集纹理

```cpp
// nvenc_d3d11_native.cpp:37-68
if (!d3d_input_texture) {
  D3D11_TEXTURE2D_DESC desc = {};
  desc.Width = encoder_params.width;
  desc.Height = encoder_params.height;
  desc.MipLevels = 1; desc.ArraySize = 1;
  desc.Format = dxgi_format_from_nvenc_format(encoder_params.buffer_format);
  desc.SampleDesc.Count = 1;
  desc.Usage = D3D11_USAGE_DEFAULT;
  desc.BindFlags = D3D11_BIND_RENDER_TARGET;
  d3d_device->CreateTexture2D(&desc, nullptr, &d3d_input_texture);
}
NV_ENC_REGISTER_RESOURCE register_resource = {
  .version = NV_ENC_REGISTER_RESOURCE_VER
};
register_resource.resourceType = NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX;
register_resource.resourceToRegister = d3d_input_texture.GetInterfacePtr();
register_resource.bufferFormat = encoder_params.buffer_format;
register_resource.bufferUsage = NV_ENC_INPUT_IMAGE;
nvenc->nvEncRegisterResource(encoder, &register_resource);
```

`d3d_nvenc_encode_device_t::init_encoder()` 把这个固定纹理交给色彩转换器作为输出（`display_vram.cpp:1284-1295`），每帧只需 `convert()` + `encode_frame()`。实际调度见 `sunshine-video.cpp:571-575`、`:614-621`，以及 `encode_run()` 的 `:2783-2809`。

### 4.5 MapInputResource 不是 CPU Map；LockBitstream 才是输出复制

```cpp
// nvenc_base.cpp:715-776（删减日志，保留调用顺序）
if (!synchronize_input_buffer()) return {};
NV_ENC_MAP_INPUT_RESOURCE mapped_input_buffer = {
  .version = NV_ENC_MAP_INPUT_RESOURCE_VER
};
mapped_input_buffer.registeredResource = registered_input_buffer;
nvenc->nvEncMapInputResource(encoder, &mapped_input_buffer);

NV_ENC_PIC_PARAMS pic_params = {.version = NV_ENC_PIC_PARAMS_VER};
pic_params.inputWidth = encoder_params.width;
pic_params.inputHeight = encoder_params.height;
pic_params.encodePicFlags = force_idr ? NV_ENC_PIC_FLAG_FORCEIDR : 0;
pic_params.inputTimeStamp = frame_index;
pic_params.pictureStruct = NV_ENC_PIC_STRUCT_FRAME;
pic_params.inputBuffer = mapped_input_buffer.mappedResource;
pic_params.bufferFmt = mapped_input_buffer.mappedBufferFmt;
pic_params.outputBitstream = output_bitstream;
pic_params.completionEvent = async_event_handle;
nvenc->nvEncEncodePicture(encoder, &pic_params);

NV_ENC_LOCK_BITSTREAM lock_bitstream = {.version = NV_ENC_LOCK_BITSTREAM_VER};
lock_bitstream.outputBitstream = output_bitstream;
lock_bitstream.doNotWait = async_event_handle ? 1 : 0;
// async 时最多等 100ms；随后 LockBitstream
nvenc->nvEncLockBitstream(encoder, &lock_bitstream);
::nvenc::nvenc_encoded_frame encoded_frame {
  {data_pointer, data_pointer + lock_bitstream.bitstreamSizeInBytes},
  lock_bitstream.outputTimeStamp,
  lock_bitstream.pictureType == NV_ENC_PIC_TYPE_IDR,
  encoder_state.rfi_needs_confirmation,
};
```

`nvEncMapInputResource` 是把注册的 GPU resource 映射为 NVENC 可消费的 input，并非 D3D11 staging/CPU `Map`。但 `encoded_frame.data` 是 CPU `vector` 风格的 bytes，构造时会复制锁定的 bitstream；`sunshine-video.cpp:2164-2168` 再将这个 packet 入队。因此准确说法是：**画面像素无 CPU round-trip；编码输出 bitstream 有 CPU copy。**

### 4.6 不要把 RAM 后端混入此结论

```cpp
// display_wgc.cpp:281-340，仅 display_wgc_ram_t
// Create D3D11_USAGE_STAGING + D3D11_CPU_ACCESS_READ
// Copy from GPU to CPU
device_ctx->CopyResource(texture.get(), src.get());
device_ctx->Map(texture.get(), 0, D3D11_MAP_READ, 0, &img_info);
copy_mapped_image(img_info, *img, DXGI_MODE_ROTATION_IDENTITY);
device_ctx->Unmap(texture.get(), 0);
```

`display()` 按 `mem_type_e::dxgi` 选择 `display_wgc_vram_t`，`mem_type_e::system` 才选 `display_wgc_ram_t`（`display_base.cpp:1046-1058`）。Windows 独立 NVENC 的 platform formats 使用 `mem_type_e::dxgi`（`sunshine-video.cpp:824-832`），因此该 RAM 分支不是 NVENC 链路。

---

## 5. VideoProcessor 到底用没用：没有，PS 是实际转换器

四份目标文件及本地辅助的 D3D11 NVENC 文件中均无 `VideoProcessorBlt`。`d3d_base_encode_device` 的资源是 `vs_t/ps_t`、`shader_res_t`、`render_target_t`、sampler 和 blend state（`display_vram.cpp:1071-1107`）。

### 5.1 一帧转换是两个 draw，不是 VideoProcessorBlt

```cpp
// display_vram.cpp:520-553
auto draw = [&](auto &input, auto &y_or_yuv_viewports, auto &uv_viewport) {
  device_ctx->PSSetShaderResources(0, 1, &input);

  // Draw Y/YUV
  device_ctx->OMSetRenderTargets(1, &out_Y_or_YUV_rtv, nullptr);
  device_ctx->VSSetShader(convert_Y_or_YUV_vs.get(), nullptr, 0);
  device_ctx->PSSetShader(img.format == DXGI_FORMAT_R16G16B16A16_FLOAT
    ? convert_Y_or_YUV_fp16_ps.get() : convert_Y_or_YUV_ps.get(), nullptr, 0);
  auto viewport_count = (format == DXGI_FORMAT_R16_UINT) ? 3 : 1;
  device_ctx->RSSetViewports(viewport_count, y_or_yuv_viewports.data());
  device_ctx->Draw(3 * viewport_count, 0);

  // Draw UV if needed
  if (out_UV_rtv) {
    device_ctx->OMSetRenderTargets(1, &out_UV_rtv, nullptr);
    device_ctx->VSSetShader(convert_UV_vs.get(), nullptr, 0);
    device_ctx->PSSetShader(img.format == DXGI_FORMAT_R16G16B16A16_FLOAT
      ? convert_UV_fp16_ps.get() : convert_UV_ps.get(), nullptr, 0);
    device_ctx->RSSetViewports(1, &uv_viewport);
    device_ctx->Draw(3, 0);
  }
};
draw(img_ctx.encoder_input_res, out_Y_or_YUV_viewports, out_UV_viewport);
```

### 5.2 NV12 两个 plane 用不同类型 RTV 写同一纹理

```cpp
// display_vram.cpp:643-655（选 shader）与 :758-809（选 RTV）
case DXGI_FORMAT_NV12:
  create_vertex_shader_helper(convert_yuv420_planar_y_vs_hlsl, convert_Y_or_YUV_vs);
  create_pixel_shader_helper(convert_yuv420_planar_y_ps_hlsl, convert_Y_or_YUV_ps);
  create_vertex_shader_helper(convert_yuv420_packed_uv_type0_vs_hlsl, convert_UV_vs);
  create_pixel_shader_helper(convert_yuv420_packed_uv_type0_ps_hlsl, convert_UV_ps);
  break;
// NV12: Y = R8_UNORM，UV = R8G8_UNORM
auto create_rtv = [&](auto &rt, DXGI_FORMAT rt_format) {
  D3D11_RENDER_TARGET_VIEW_DESC rtv_desc = {};
  rtv_desc.Format = rt_format;
  rtv_desc.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
  return SUCCEEDED(device->CreateRenderTargetView(
    output_texture.get(), &rtv_desc, &rt));
};
```

D3D11 对 NV12 的 plane 通过格式化 view 区分；不是「NV12 不能做 RTV」，而是「需要正确的 plane RTV 与两次 draw」。UV viewport 是半宽半高（`:731-732`）。色彩矩阵通过 VS/PS constant buffer 注入（`:570-590`），缩放/旋转也由 viewport 和 shader 常量处理，无需 VideoProcessor。

`D3D11_CREATE_DEVICE_VIDEO_SUPPORT`（`:879`）只是 device 创建 flag；它**不是 VideoProcessor 调用证据**。

---

## 6. 光标：DDA 自绘，WGC 系统合成；cursor-only 帧不丢

### 6.1 DDA 元数据如何触发出帧

```cpp
// display_vram.cpp:1366-1390
auto capture_status = dup.next_frame(frame_info, timeout, &res_p);
resource_t res {res_p};
if (capture_status != capture_e::ok) return capture_status;
const bool mouse_update_flag =
  frame_info.LastMouseUpdateTime.QuadPart != 0 ||
  frame_info.PointerShapeBufferSize > 0;
const bool frame_update_flag = frame_info.LastPresentTime.QuadPart != 0;
const bool update_flag = mouse_update_flag || frame_update_flag;
if (!update_flag) return capture_e::timeout;
```

这意味着 `AcquireNextFrame` 成功返回但只有鼠标变化时，`src` 可以保持为空，仍继续生成带新光标的输出。时间戳取桌面/鼠标二者较新的 QPC（`:1386-1390`）。Microsoft 的 [Desktop Duplication API](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api) 同样说明：需要利用 pointer position/shape 把独立光标画到桌面图上，shape 只有变化时才重新获取。

### 6.2 形状读取、CPU 形状预处理与 GPU 上传

```cpp
// display_vram.cpp:1392-1417
if (frame_info.PointerShapeBufferSize > 0) {
  DXGI_OUTDUPL_POINTER_SHAPE_INFO shape_info {};
  util::buffer_t<std::uint8_t> img_data {frame_info.PointerShapeBufferSize};
  UINT dummy;
  dup.dup->GetFramePointerShape(img_data.size(), std::begin(img_data),
                                &dummy, &shape_info);
  auto alpha_cursor_img = make_cursor_alpha_image(img_data, shape_info);
  auto xor_cursor_img = make_cursor_xor_image(img_data, shape_info);
  set_cursor_texture(device.get(), cursor_alpha,
                     std::move(alpha_cursor_img), shape_info);
  set_cursor_texture(device.get(), cursor_xor,
                     std::move(xor_cursor_img), shape_info);
}
if (frame_info.LastMouseUpdateTime.QuadPart) {
  cursor_alpha.set_pos(frame_info.PointerPosition.Position.x,
    frame_info.PointerPosition.Position.y, width, height,
    display_rotation, frame_info.PointerPosition.Visible);
  cursor_xor.set_pos(/* 同位置与可见性 */);
}
```

`make_cursor_alpha_image` / `make_cursor_xor_image`（`:280-426`）处理三种 shape：

- COLOR：alpha 图原样使用，XOR 图为空；
- MASKED_COLOR：alpha=0 的像素进入 opaque alpha 图，alpha=255 的像素进入 XOR 图；
- MONOCHROME：把上下 AND/XOR mask 拆成 alpha 图的黑/白像素和 XOR 图的反色像素。

这只是很小的光标 shape buffer 的 CPU 处理，不是桌面帧回读。`set_cursor_texture()` 创建 IMMUTABLE B8G8R8A8 + SRV，并只在 shape 变化时调用（`:1322-1363`）。

### 6.3 真正的合成是两遍 GPU blend

```cpp
// display_vram.cpp:1663-1693
auto blend_cursor = [&](img_d3d_t &d3d_img) {
  device_ctx->VSSetShader(cursor_vs.get(), nullptr, 0);
  device_ctx->PSSetShader(cursor_ps.get(), nullptr, 0);
  device_ctx->OMSetRenderTargets(1, &d3d_img.capture_rt, nullptr);
  if (cursor_alpha.texture.get()) {
    device_ctx->OMSetBlendState(blend_alpha.get(), nullptr, 0xFFFFFFFFu);
    device_ctx->PSSetShaderResources(0, 1, &cursor_alpha.input_res);
    device_ctx->RSSetViewports(1, &cursor_alpha.cursor_view);
    device_ctx->Draw(3, 0);
  }
  if (cursor_xor.texture.get()) {
    device_ctx->OMSetBlendState(blend_invert.get(), nullptr, 0x00FFFFFFu);
    device_ctx->PSSetShaderResources(0, 1, &cursor_xor.input_res);
    device_ctx->RSSetViewports(1, &cursor_xor.cursor_view);
    device_ctx->Draw(3, 0);
  }
  device_ctx->OMSetBlendState(blend_disable.get(), nullptr, 0xFFFFFFFFu);
  // unbind RTV / SRV
};
```

`make_blend()` 的普通模式为 `SRC_ALPHA + INV_SRC_ALPHA`，invert 模式为 `INV_DEST_COLOR + INV_SRC_COLOR`，XOR 写掩码不含 alpha（`:95-127`, `:1677-1684`）。

为支持「只有鼠标动」，Sunshine 用 `last_frame_variant` 在「最近 image」和「无光标中间 surface」之间切换（`:1454-1521`）。无光标且新桌面帧到达时可直接转发最近 image，避免多一次拷贝（`:1487-1494`）；有光标时保存干净底图，鼠标变化时复制底图再画光标（`:1495-1506`, `:1707-1730`）。**不是重复发送上一张已经带旧光标的纹理。**

### 6.4 WGC 不做 cursor shader，调用开关即可

```cpp
// display_wgc.cpp:244-252
int wgc_capture_t::set_cursor_visible(bool x) {
  if (capture_session.IsCursorCaptureEnabled() != x) {
    capture_session.IsCursorCaptureEnabled(x);
  }
  return 0;
}
```

`display_wgc_vram_t::snapshot()` 每帧把通用 cursor 设置传给它（`display_vram.cpp:1863`）。这里的光标由 WGC/系统合成；不要在此路径上再画一份 DDA 光标。

SoulLink 当前 WGC 已设置 `IsCursorCaptureEnabled=true`（`WgcCapture.StartOnPumpThread()`，`WgcCapture.cs:200-205`）；DDA 的 `frameInfo` 已获取（`DxgiDdaCapture.cs:218` / `:306`）但未消费 `LastMouseUpdateTime`、`PointerPosition` 或 `PointerShapeBufferSize`。

---

## 7. SetMaximumFrameLatency：位置、对象、不能替代什么

```cpp
// display_base.cpp:724-737，display_base_t::init()
// Try to reduce latency
{
  dxgi::dxgi1_t dxgi {};
  status = device->QueryInterface(IID_IDXGIDevice, (void **) &dxgi);
  if (FAILED(status)) return -1;
  status = dxgi->SetMaximumFrameLatency(1);
  if (FAILED(status)) {
    BOOST_LOG(warning) << "Failed to set maximum frame latency ...";
  }
}
```

- 调用位置：创建采集 device 后，DDA/WGC backend 的 `init()` 之前；DDA 的 `display_ddup_vram_t::init()` 与 WGC 的 `display_wgc_vram_t::init()` 都先调用它（`display_vram.cpp:1778-1781` / `:1917-1920`）。
- 调用对象：上面的 `device` 是 `display_base_t::device`。`d3d_base_encode_device::init()` 里没有此调用。
- 【在线复核】Microsoft [SetMaximumFrameLatency](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgidevice1-setmaximumframelatency) 定义为「允许系统排队渲染的帧数」，默认 3、可设 1~16。
- **不保证** NVENC 只有一帧在飞，也不改变 DDA `AcquireNextFrame` 的 timeout，更不等于编码/传输 pacing。NVENC 低延迟另由 `zeroReorderDelay=1`、`enableLookahead=0`、`frameIntervalP=1`、`NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY` 等控制（`nvenc_base.cpp:258-281`, `:641-653`）。

同一初始化还会对采集 device 调 `SetGPUThreadPriority(7)`（`display_base.cpp:711-721`），对编码 device 也调一次（`display_vram.cpp:893-903`）。这两个调度参数同样不是 pacing。

---

## 8. 帧 pacing 与 capture 循环：精确结构，而非「200ms 轮询」

### 8.1 通用 capture 循环的 pacing group

```cpp
// display_base.cpp:250-252
DXGI_RATIONAL client_frame_rate_adjusted = adjust_client_frame_rate();
std::optional<std::chrono::steady_clock::time_point> frame_pacing_group_start;
uint32_t frame_pacing_group_frames = 0;
```

**组内**：目标时刻来自「组首源帧时间戳 + 第 N 拍」，不是「上次编码完成时间」：

```cpp
// display_base.cpp:276-303
if (frame_pacing_group_start) {
  const uint32_t seconds = (uint64_t) frame_pacing_group_frames *
    client_frame_rate_adjusted.Denominator / client_frame_rate_adjusted.Numerator;
  const uint32_t remainder = (uint64_t) frame_pacing_group_frames *
    client_frame_rate_adjusted.Denominator % client_frame_rate_adjusted.Numerator;
  const auto sleep_target = *frame_pacing_group_start +
    std::chrono::nanoseconds(1s) * seconds +
    std::chrono::nanoseconds(1s) * remainder / client_frame_rate_adjusted.Numerator;
  const auto sleep_period = sleep_target - std::chrono::steady_clock::now();
  if (sleep_period <= 0ns) {
    frame_pacing_group_start = std::nullopt;
    frame_pacing_group_frames = 0;
    status = capture_e::timeout;
  } else {
    timer->sleep_for(sleep_period);
    status = snapshot(pull_free_image_cb, img_out, 0ms, *cursor);
    if (status == capture_e::ok && img_out) {
      frame_pacing_group_frames += 1;
    } else {
      frame_pacing_group_start = std::nullopt;
      frame_pacing_group_frames = 0;
    }
  }
}
```

**组首/重启组**：

```cpp
// display_base.cpp:306-318
if (status == capture_e::timeout ||
    (status == capture_e::ok && !frame_pacing_group_start)) {
  status = snapshot(pull_free_image_cb, img_out, 200ms, *cursor);
  if (status == capture_e::ok && img_out) {
    frame_pacing_group_start = img_out->frame_timestamp;
    if (!frame_pacing_group_start)
      frame_pacing_group_start = std::chrono::steady_clock::now();
    frame_pacing_group_frames = 1;
  }
}
```

所以：

1. 组首最多等 200ms，新帧/鼠标更新到达即可返回；不是每 200ms 主动抓一次。
2. 组内先睡到绝对目标，再以 0ms 非阻塞取帧；这是 fps 上限，不是「桌面有变化就无限制编码」。
3. 到点没有新帧、错过拍点都会弃组，下一圈进入 200ms 等帧；不会用不断递增的 `nextAt` 在静止时空转。
4. DDA 时间戳来自 `LastPresentTime` / `LastMouseUpdateTime`，WGC 来自 `SystemRelativeTime`；它们被用于 pacing 与 packet 帧时间关联。

### 8.2 回调、DDA 释放与编码是分开的阶段

```cpp
// display_base.cpp:341-364
switch (status) {
  case capture_e::timeout:
    if (!push_captured_image_cb(std::move(img_out), false)) return capture_e::ok;
    break;
  case capture_e::ok:
    if (!push_captured_image_cb(std::move(img_out), true)) return capture_e::ok;
    break;
  // reinit/error/interrupted 直接返回
}
status = release_snapshot();
```

异步 NVENC 下，采集 callback 只把 image 放进 session 的 `images` event queue（`sunshine-video.cpp:1924-1934`），编码线程独立消费。采集 image pool 有 12 个槽、按 `use_count()` 复用，池满时 1ms 后重试（`:1820-1821`, `:1873-1914`）。这不是「一个私有纹理边写边读」。

编码线程 `images->pop(max_frametime)` 等图，`convert()` 后调用 `encode()`（`:2781-2809`）。`max_frametime` 默认为客户端 fps 的一半对应的帧间隔，也可配置（`:2740-2743`）。即使没有新图，它也会继续调用 encode：当前 `convert()` 对 `img.blank` 不做绘制，从而使用既有输出纹理（`display_vram.cpp:504-562`），这是 Sunshine 的最低 fps/静止质量控制，不应误写成「Sunshine 静止从不编码」。

### 8.3 DDA 200ms 超时后主动让锁 10ms

```cpp
// display_base.cpp:319-338
} else if (status == platf::capture_e::timeout) {
  // AcquireNextFrame() 运行时一直持 D3D11 unfair lock；
  // 静止时几乎总占着锁，会让编码线程初始化 shared state 饿死。
  std::this_thread::sleep_for(10ms);
}
```

这发生在 `AcquireNextFrame` 已返回、当前不持其内部等待锁之后。目的不是制造低帧率，而是让另一个线程获得 device。SoulLink 当前采集/编码同线程时没有同样的竞争；改成采集/编码分线程后才成为必需项。

### 8.4 WGC 还有生产者—消费者层，不等同于 DDA 的 Acquire

WGC 初始化基于 display 的同一个采集 device：

```cpp
// display_wgc.cpp:112-143
display->device->QueryInterface(IID_IDXGIDevice, (void **) &dxgi);
winrt::CreateDirect3D11DeviceFromDXGIDevice(*&dxgi, d3d_comhandle.put());
uwp_device = d3d_comhandle.as<winrt::IDirect3DDevice>();
// ...
frame_pool = winrt::Direct3D11CaptureFramePool::CreateFreeThreaded(
  uwp_device, static_cast<DirectXPixelFormat>(display->capture_format),
  2, item.Size());
capture_session = frame_pool.CreateCaptureSession(item);
frame_pool.FrameArrived({this, &wgc_capture_t::on_frame_arrived});
```

支持时设置 `MinUpdateInterval(4ms)`，注释写 250Hz（`:158-165`），然后 `StartCapture()`（`:167-172`）。这只是允许的更新间隔，不是 SoulLink 视频输出的 fps 调度器。

生产者回调一次只 `TryGetNextFrame()`，latest-wins 替换未消费帧：

```cpp
// display_wgc.cpp:181-198
frame = sender.TryGetNextFrame();
if (frame != nullptr) {
  AcquireSRWLockExclusive(&frame_lock);
  if (produced_frame) produced_frame.Close();
  produced_frame = frame;
  ReleaseSRWLockExclusive(&frame_lock);
  WakeConditionVariable(&frame_present_cv);
}
```

消费者在通用 capture 线程先释放上一次 `consumed_frame`，再等待/取走 `produced_frame`：

```cpp
// display_wgc.cpp:205-233
release_frame();
AcquireSRWLockExclusive(&frame_lock);
if (produced_frame == nullptr &&
    SleepConditionVariableSRW(&frame_present_cv, &frame_lock,
                              timeout.count(), 0) == 0) {
  ReleaseSRWLockExclusive(&frame_lock);
  return GetLastError() == ERROR_TIMEOUT ? capture_e::timeout
                                         : capture_e::capture_error;
}
if (produced_frame) {
  consumed_frame = produced_frame;
  produced_frame = nullptr;
}
ReleaseSRWLockExclusive(&frame_lock);
// frame.Surface -> IDirect3DDxgiInterfaceAccess -> ID3D11Texture2D
capture_access->GetInterface(IID_ID3D11Texture2D, (void **) out);
out_time = consumed_frame.SystemRelativeTime().count();
```

**关键**：WGC 帧对象 `consumed_frame` 在 `snapshot()` 的 `CopyResource` 完成后、下一次 `next_frame()` 开头才 Close；帧池原始 surface 不会被提前复用。生产端替换旧 `produced_frame` 会立即 Close，因此最多保留一份待消费最新帧，外加一份正在消费帧。

---

## 9. SoulLink 当前链路与 Sunshine 的逐函数对照

以下行号属于本次工作区，仅用于定位；函数名才是实施锚点。

| 环节 | SoulLink 当前事实 | 与 Sunshine 的差异 |
|---|---|---|
| DDA device | `DxgiDdaCapture.Start()` 自建 Hardware device，`DuplicateOutput(device)`（`DxgiDdaCapture.cs:117-155`） | 独立采集 device 本身没错；可继续跨设备，也可选择自有同-device 简化 |
| NVENC device | `NvencSdkEncoder.TryInitialize()` 另建 Hardware device，并传给 `OpenEncodeSessionEx`（`NvencSdkEncoder.cs:485-526`） | 与 Sunshine 拓扑相同 |
| DDA 私有纹理 | `DxgiDdaCapture.TryCaptureBgraTexture()` 建 `MiscFlags.Shared`、`BindFlags.None`，每次 `GetSharedHandle`（`:295-351`） | Sunshine 用 SHARED_NTHANDLE + KEYEDMUTEX + SRV/RTV，并缓存 NT handle |
| WGC 私有纹理 | `TryCaptureBgraTexture()` 只返回既有 `_gpuTexture`（`WgcCapture.cs:356-373`）；只有 `ConsumeBgra()` 调 `ProcessStashedFrame()`（`:395-426`） | **陈旧纹理根因**：GPU 分支不消费 stash。 Sunshine 每次成功 `next_frame` 都拷贝当前 source |
| 跨设备读取 | `GpuColorConverter.ConvertFromSharedHandle()` 每帧 `OpenSharedResource`、每帧新 `localBgra`、无 keyed mutex（`GpuColorConverter.cs:178-215`） | Sunshine 按 img 缓存 Open/SRV，锁内读取，且不复制到第二份 local BGRA |
| 色彩转换 | `GpuColorConverter.TryInitialize/Convert()` 使用 VideoProcessor（`:50-171`） | Sunshine 用 PS 两 pass；VideoProcessor 在 SoulLink RDP 是否可用仍【未实测】 |
| NVENC 输入 | `NvencSdkEncoder.CreateNv12Texture()` 已是 `BindFlags.RenderTarget`（`NvencSdkEncoder.cs:780-799`） | 可直接作为 PS 输出；`EncodeD3D11Texture()` 已跳过上传（`:717-775`），无需重写编码调用 |
| CPU 输出 | `EncodeD3D11Texture()` 锁定 bitstream 后 `Marshal.Copy`（`:753-766`） | 与 Sunshine 相同，不属「GPU 零拷贝失败」 |
| 帧节流 | `SessionRecorder.Loop()` 先 `WaitUntilNextSlot()`，成功后 `nextAt=now+1/fps`（`SessionRecorder.cs:690-777`, `:913-915`） | Sunshine 锚定源帧时间戳，错拍弃组；SoulLink 以编码完成时间重新起算 |
| 静止/失败分支 | GPU false 后同拍再 `CaptureBgraWithProbe()`；静止时 `IdleWait(5)`，不推进 `nextAt`（`:779-793`, `:827-831`, `:1022-1025`） | 同拍两次 Acquire/读回 + 静止 5ms 轮询；Sunshine 组首长等、超时让锁 |
| DDA 光标 | DDA 已拿到 `frameInfo`，只累计 `AccumulatedFrames` | 未处理鼠标时间与 shape；无 cursor-only 出帧 |
| WGC 光标 | `StartOnPumpThread()` 已启用 `IsCursorCaptureEnabled` | 已对齐系统合成，无需额外光标 shader |

另外，`SessionRecorder` 的零拷贝 PSNR 一次性诊断在 GPU 编码后又调用 `CaptureBgraWithProbe()`（`:742-759`），会再消费一次采集路径；它不是稳态热路径，但做逐帧测量/对比时应剔除这次诊断。

---

## 10. 可直接抄到 SoulLink 的改动清单

> 「抄」指抄设计和调用契约，不复制 GPL 源码。顺序刻意先修**正确性与每帧资源生命周期**，再换色彩转换器，最后调节拍。每条都列出函数、预期收益和验收重点；没有声称未经实测的固定毫秒数。

### P0：先让 GPU 路径正确且可观察

1. **`WgcCapture.TryCaptureBgraTexture()`：在返回句柄前消费 stash，而不是只读旧 `_gpuTexture`。**
   - 在 `_captureGate` 内原子取走 `_stashedFrame`，沿用 `ProcessStashedFrame(frame, generation)` 的代数/resize/CopyResource 逻辑，finally `Dispose` 帧对象；没有新帧时返回「无新帧」状态，不触发 CPU 抓屏。
   - 可把公共检出逻辑从 `ConsumeBgra()` 提取为私有辅助函数，两个入口共用，避免两份生命周期代码漂移。
   - 预期收益【推断】：消除 WGC 零拷贝冻结/重复，屏幕像素始终不必回读。
   - 验收：持续变化画面和纯光标变化均产生更新；帧对象 Dispose 次数平衡，无黑闪/旧帧复写。

2. **把 `IScreenCapture.TryCaptureBgraTexture()` 的 bool 改为区分「无新帧 / 失败 / 有纹理」的结果，并同步改 `DxgiDdaCapture.TryCaptureBgraTexture()`、`WgcCapture.TryCaptureBgraTexture()`、`BitBltCapture.TryCaptureBgraTexture()` 与 `SessionRecorder.Loop()`。**
   - 无新帧：正常等待下一拍，**不要**同拍 fall through CPU；失败：记录一次原因，再决定是否 CPU 降级；BitBlt：明确 unsupported。
   - DDA 成功取得帧后，只有成功交给编码分支才推进该 GPU 帧序号；无桌面 surface 但鼠标更新不应被当作失败。
   - 预期收益【推断】：去掉当前一次循环里的第二次 `AcquireNextFrame(16)`、WGC 全帧读回和静止重复工作，同时把「零拷贝为何没生效」从静默 false 变成可诊断状态。
   - 验收：静止时没有每秒数百次采集；DDA 失败只发生一次 Acquire；日志能区分 timeout/shared-open/convert/encode。

3. **`DxgiDdaCapture.TryCaptureBgraTexture()` 与 `WgcCapture.EnsureGpuTexture()`：改用 NT + keyed mutex 共享，句柄只创建一次。**
   - 纹理 flags：`SharedNtHandle | SharedKeyedMutex`（以 Vortice 实际枚举名为准），`BindFlags.ShaderResource | BindFlags.RenderTarget`；DDA 原始 texture 仍只用于锁内 CopyResource，绝不共享原始 DDA/WGC surface。
   - 用 `IDXGIResource1.CreateSharedHandle` 获取并缓存 NT HANDLE，不用 `IDXGIResource.SharedHandle`；纹理销毁时 `CloseHandle`。
   - `GpuColorConverter.ConvertFromSharedHandle()` 用 `ID3D11Device1.OpenSharedResource1`，按纹理身份缓存 opened texture/SRV/keyed mutex；`AcquireSync(0, INFINITE)` 保护读取，finally `ReleaseSync(0)`。
   - 不必每帧创建 `localBgra`：PS 可直接采样 opened shared texture。生产端 CopyResource 必须在对应 mutex 内；DDA 临时帧在 Copy 后、下一次 Acquire 前释放。
   - 预期收益【推断】：修复无互斥导致的旧帧/竞态，移除每帧 Open/CreateTexture/CreateView 的固定成本。
   - 备选：SoulLink 若主动选择同 device，可省共享和 mutex；这是简化设计，**不是 Sunshine 对齐的必要条件**。选择后仍需保留新帧消费、正确生命周期和 PS 转换。
   - 验收：资源创建日志只发生在纹理/尺寸变化；键锁获取失败有错误日志，不能无限静默等待；持续编码没有纹理陈旧和句柄增长。

4. **`SessionRecorder.Loop()`：只让「成功出 NAL」的路径写 `previousZeroSeq`；新观众/IDR 请求不要被无新帧吞掉。**
   - 当前代码在 `:760` 成功路径更新一次、`:765` 变化时又更新一次；失败时也会改基准，导致「转换失败后恢复」可能被误判为静止。应只在成功出 NAL 时更新，且用原子赋值替代两处分散更新；所有 early-continue 保留 `forceKeyFrame` 请求，或调用 `RequestKeyFrame()` 重新置位；GPU 超时与「未变化」应进入有界等待。
   - 预期收益【推断】：静止起播、热切、断流重同步不依赖下一次恰好变化的桌面帧。
   - 验收：静止桌面新订阅、WGC 无 FrameArrived 后起播、GPU→CPU 降级都能得到 IDR，且无首帧超时。

### P1：用可移植的 D3D11 PS 转换替代 VideoProcessor 热路径

5. **`GpuColorConverter.TryInitialize()` / `Convert()` / `ConvertFromSharedHandle()`：保留类边界，内部换成 BGRA→NV12 的两次 draw。**
   - 初始化：编译/加载 Y pass 与 UV pass VS+PS，创建线性 sampler、颜色矩阵 constant buffer；输出直接使用 `NvencSdkEncoder.Nv12InputTexture`。
   - Y pass：以 BGRA SRV 为输入，绑定 NV12 plane 0 的 `R8_UNorm` RTV，full-height viewport，Draw fullscreen triangle。
   - UV pass：绑定同一 NV12 的 plane 1 `R8G8_UNorm` RTV，半宽半高 viewport；根据 SoulLink 现有 `BgraToNv12()` 的既有 BT.601 系数、offset 及 2×2 平均语义实现/验证 shader；先确认实际 range，不能仅凭系数命名为 limited/full。
   - 缓存 SRV/RTV，不解绑前不要让同一纹理同时作为 SRV/RTV；每帧结束清理绑定。NV12 纹理已经 `RenderTarget`，`NvencSdkEncoder.EncodeD3D11Texture()` 可直接复用。
   - 保留 VideoProcessor 为可回退实现也可，但不要把它作为唯一 GPU 路径；本研究没有实测证明 RDP 不支持 VideoProcessor。
   - 预期收益【推断】：移除 `ID3D11VideoDevice` 可用性依赖与本地 BGRA 中转，降低 CPU 转换/上传和每帧分配；实际节省需分段 Stopwatch 或 GPU timestamp 测量。
   - 验收：与 `BgraToNv12()` 同帧比较 Y/UV（不只 Y），列 PSNR/最大误差；奇数尺寸裁边、旋转/窗口尺寸变化、全黑/暗场、HDR 不在支持范围时明确回退。

6. **`NvencSdkEncoder.TryInitialize()` / `EncodeD3D11Texture()`：保持现有 Register→Map→Encode→Lock 主链，只补生命周期和错误一致性。**
   - 不把共享 BGRA 注册给 NVENC；仍注册自有 NV12。确保 `LockBitstream` 成功后始终 `UnlockBitstream`，包括复制或日志路径异常；当前可改用一个集中式 encode-private helper 避免 `EncodeNv12()` 与 `EncodeD3D11Texture()` 漂移。
   - 维持 `enableEncodeAsync=0` 与 `doNotWait=0`，本任务不建议为了「像 Sunshine」同时引入异步事件状态机。
   - 预期收益【推断】：减少改动面；输出 CPU copy 是必要行为，不应误删。
   - 验收：CPU/GPU 输入都出 Annex-B H.264，首帧/强制帧含 SPS/PPS+IDR，输出帧序号一致，异常不遗留锁定资源。

### P1：按 Sunshine 的「组首阻塞 + 组内绝对拍点」修节拍，而非删除 fps 上限

7. **`SessionRecorder.WaitUntilNextSlot()` / `IdleWait()` / `Loop()`：改成 deadline-aware、有结果的等待。**
   - 新帧到达即编码，仅在需要限制输出 fps 时睡到下一绝对拍点；拍点无新帧则进入阻塞等帧，不走 5ms `IdleWait` 轮询。
   - 节拍基准优先使用 DDA `LastPresentTime`/鼠标时间或 WGC `SystemRelativeTime`；没有源时间戳时才用本地 monotonic clock。
   - 不要简单把 DDA 改成无条件 `AcquireNextFrame(200)`：那会绕过 fps 上限并取消 stop/IDR 响应。可用「到下一拍为止的 timeout」或完整 pacing group；取消事件要能打断等待。
   - 当前 `nextAt=编码完成+1/fps` 会把编码耗时叠加到帧周期；改用源时间锚点 + 错拍弃组，避免相位漂移。静止时推进状态机，不留在 `nextAt` 不变的 5ms 循环。
   - 预期收益【推断】：消除多余等待与双重抓屏，减少操作恢复后的首帧年龄；不承诺消除 RDP 自身低合成帧率。
   - 验收：分别测 capture wait、GPU convert、NVENC、queue age；活动画面 fps 不超上限，静止 CPU 下降，停止/参数热切/IDR 及时响应。

8. **`DxgiDdaCapture.CaptureBgra()` 与 `TryCaptureBgraTexture()`：消费完整 FRAME_INFO，先实现 cursor-only，再谈合成。**
   - 当 `LastMouseUpdateTime != 0` 或 `PointerShapeBufferSize > 0`，即使 `LastPresentTime == 0` 也视作有更新；shape 变化才 `GetFramePointerShape`，否则缓存 shape。
   - 新增 DDA cursor 状态（position/visible/shape 版本），在 GPU 私有纹理的 RTV 上画 alpha/XOR 光标；保留不带光标的底图，光标单独移动时复制底图后重绘，不能反复叠加。
   - 如果先走 CPU 路径，也应在同一 frameInfo 驱动下合成 CPU BGRA，但这只能作为功能回退，不是 GPU 零拷贝终态。
   - 预期收益【推断】：DDA 下光标立即可见且无需网页蓝球；静止桌面仅移动鼠标仍能显示反馈。
   - 验收【先测可用性】：目标 RDP 会话的 `PointerPosition.Visible`、shape buffer 是否有效；若系统已把光标烘进 DDA 图像或元数据无效，不得再叠一份。

### P2：低风险配套，不把它们包装成主延迟修复

9. **`DxgiDdaCapture.Start()` 与 `WgcCapture.CreateDevice()`：对采集 device 调 `SetMaximumFrameLatency(1)`，记录 HRESULT。**
   - 这是限制 GPU 渲染排队；不改 DDA timeout、不替代 NVENC 参数、不证明端到端收益。
   - 若改同-device 架构，在共享 device 初始化时只调一次。编码 device 是否需要另行设置应单独测量，不能声称 Sunshine 已这么做。
   - `SetGPUThreadPriority(7)` 需要权限且可能失败；默认不做全局优先级改动，先记录/评估，不能为串流程序破坏系统调度。
   - 预期收益【推断】：可能降低 GPU 命令排队抖动，数值未知。

10. **若未来把 `SessionRecorder.Loop()` 拆成采集与编码两个线程：同时引入 image 所有权池，并在 DDA 长等超时后让出 device。**
   - 可参照 Sunshine 的 image pool：纹理随 image 实例复用，生产者只写未在使用的槽，消费者持锁读取；不是简单把当前单 `_sharedBgraTexture` 给两个线程并发。
   - 200ms DDA 等待超时后，在 `AcquireNextFrame` 已返回的前提下 sleep/等待约 10ms，避免 unfair device lock 饿死编码初始化。
   - 预期收益【推断】：避免编码阻塞采集；同时新增同步复杂度，必须在 P0/P1 的单线程语义正确后再做。
   - 验收：纹理不重叠写读、销毁顺序正确、静止与设备移除不会死锁；观察句柄/显存至少 15 分钟。

---

## 11. 推荐最小落地顺序（防止把架构纠错变成大返工）

1. 先改结果三元状态和 WGC stash 消费，确保 DDA/WGC 的「新帧、静止、失败」可区分。
2. 在现有跨设备拓扑上补 NT handle + keyed mutex + 缓存，暂时保留 VideoProcessor，以最小变量验证 GPU 链路能持续出帧。
3. 再把转换器内部换成 PS 两 pass，并直接写 NVENC NV12；同一次不要改编码异步模式或网络协议。
4. 修 pacing 与 cursor-only，最后评估同-device 简化或双线程。
5. 真机只允许使用授权的 A2/A3 测试环境；本研究未执行这些测试，稳定版与 A1/A4 不在范围内。

**一句话给实现者**：SoulLink 不需要为了「像 Sunshine」而推翻双设备；需要先把 Sunshine 已经证明必要的四件事补齐——**每帧真正更新私有纹理、跨设备 keyed mutex、按纹理缓存资源、GPU PS 写 NV12**。帧节拍则按源时间和有新帧/无新帧状态管理，不能让「无 GPU 帧」自动等价于「再走一次 CPU 抓屏」。

---

## 12. 证据索引

### 本地 Sunshine 源码

- `artifacts/zero-copy-src/display_vram.cpp`：转换 `:494-563`；PS/RTV 初始化 `:601-826`；编码 device `:837-950`；共享打开缓存 `:955-1028`；NVENC 接线 `:1242-1310`；DDA cursor/frame `:1366-1775`；WGC VRAM `:1860-1915`；共享纹理 `:1925-2015`。
- `artifacts/zero-copy-src/display_wgc.cpp`：WGC 初始化 `:103-173`；生产/消费 `:176-242`；系统光标 `:244-252`；独立 RAM 路径 `:255-355`。
- `artifacts/zero-copy-src/display_base.cpp`：DDA `:64-219`；pacing `:225-368`；采集 device `:463-770`；SetMaximumFrameLatency `:724-737`；后端选择 `:1029-1064`。
- `artifacts/zero-copy-src/nvenc_base.cpp`：低延迟参数 `:252-299`, `:563-681`；编码调用 `:715-797`；销毁顺序 `:684-713`。
- `artifacts/zero-copy-src/nvenc_d3d11_native.cpp`：NV12 创建/注册 `:26-71`。
- `artifacts/zero-copy-src/nvenc_d3d11.cpp`：Windows 异步 event 与 100ms 等待使用的底层函数 `:13-51`。
- `artifacts/zero-copy-src/sunshine-video.cpp`（upstream `src/video.cpp`）：NVENC flags `:820-866`；采集 pool/队列 `:1778-2009`；NVENC session `:554-627`；编码循环 `:2707-2819`；线程入口 `:3175-3303`。

### 在线来源

- [Sunshine master commit 433c8760](https://github.com/SunshineStream/Sunshine/commit/433c876056c36f762428921a8b0a7760afe3911f)
- [固定 commit 的 display_base.cpp](https://github.com/SunshineStream/Sunshine/blob/433c876056c36f762428921a8b0a7760afe3911f/src/platform/windows/display_base.cpp)
- [固定 commit 的 display_vram.cpp](https://github.com/SunshineStream/Sunshine/blob/433c876056c36f762428921a8b0a7760afe3911f/src/platform/windows/display_vram.cpp)
- [固定 commit 的 display_wgc.cpp](https://github.com/SunshineStream/Sunshine/blob/433c876056c36f762428921a8b0a7760afe3911f/src/platform/windows/display_wgc.cpp)
- [固定 commit 的 nvenc_base.cpp](https://github.com/SunshineStream/Sunshine/blob/433c876056c36f762428921a8b0a7760afe3911f/src/nvenc/nvenc_base.cpp)
- [固定 commit 的 nvenc_d3d11_native.cpp](https://github.com/SunshineStream/Sunshine/blob/433c876056c36f762428921a8b0a7760afe3911f/src/nvenc/nvenc_d3d11_native.cpp)
- [Microsoft：CreateSharedHandle](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgiresource1-createsharedhandle)
- [Microsoft：Desktop Duplication API](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api)
- [Microsoft：SetMaximumFrameLatency](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgidevice1-setmaximumframelatency)
- [Microsoft：D3D11 Flush（用于说明 Sunshine 未显式调用及其一般语义）](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-flush)

### SoulLink 当前源码

- `Relay/SessionRecorder.cs`：`Loop()`、`WaitUntilNextSlot()`、`IdleWait()`、`BgraToNv12()`、`RequestKeyFrame()`。
- `Relay/DxgiDdaCapture.cs`：`Start()`、`CaptureBgra()`、`TryCaptureBgraTexture()`、`ReadBackTexture()`。
- `Relay/WgcCapture.cs`：`StartOnPumpThread()`、`OnFrameArrived()`、`TryCaptureBgraTexture()`、`ConsumeBgra()`、`ProcessStashedFrame()`、`EnsureGpuTexture()`。
- `Services/GpuColorConverter.cs`：`TryInitialize()`、`Convert()`、`ConvertFromSharedHandle()`。
- `Services/NvencSdkEncoder.cs`：`TryInitialize()`、`EncodeNv12()`、`EncodeD3D11Texture()`、`CreateNv12Texture()`。
