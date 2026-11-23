# DDA 纹理跨设备共享句柄可行性研究（服务 DDA 零拷贝）

> 日期：2026-11-24
> 任务：task_mwtiz0fe6u6yit（纯研究，未改任何产品代码）
> 结论级别：MSDN 官方文档原文 + Sunshine/OBS 源码 file:line 直取；未实测，未臆造。
> 关联：GPU-ZERO-COPY-20261123.md（WGC 零拷贝路线）、GPU-ZERO-COPY-IMPL-PLAN-20261124.md（WGC 实现方案）。

---

## 0. 结论先行

**核心答案：DDA 的 AcquireNextFrame 返回的 IDXGIResource 纹理不能可靠地直接 GetSharedHandle 跨设备共享——正确做法是「复制到自己创建的可共享纹理」再共享（Sunshine/OBS 均如此），或者更简单：直接在「编码设备的同一个 D3D11 device」上建 DDA duplication，从根本上避免跨设备。**

四点直接回答验收问题：

1. **能否拿 SharedHandle 跨设备共享？** —— DDA surface 是桌面合成器内部管理的只读纹理，不是应用用 D3D11_RESOURCE_MISC_SHARED 创建的；OpenSharedResource 文档明确「The only resources that can be shared are 2D non-mipmapped textures」且须「created with D3D11_RESOURCE_MISC_SHARED flag」。DDA surface 不满足，GetSharedHandle 会 DXGI_ERROR_INVALID_CALL（非共享资源）。因此不能直接拿 DDA surface 的 SharedHandle 在另一设备 OpenSharedResource。
2. **帧所有权时序冲突如何解？** —— 不是「先 Open 再 Release」或「先 Release 再 Open」二选一，而是压根不在 DDA surface 上共享：先 CopyResource 把 DDA surface 拷贝到自建的可共享纹理（SHARED_NTHANDLE|SHARED_KEYEDMUTEX），然后即可 ReleaseFrame，之后编码设备再 OpenSharedResource1 + IDXGIKeyedMutex 同步读取。复制发生在 AcquireNextFrame 之后、ReleaseFrame 之前；共享句柄的 Open 发生在 Release 之后也安全（因为数据已落到自己的纹理里）。
3. **Sunshine/OBS 的做法** —— Sunshine DDA 分支（display_ddup_vram_t）根本不做跨设备：DuplicateOutput((IUnknown*)device) 直接把 duplication 建在编码设备上（display_base.cpp:116），抓屏和编码同一 device，零拷贝且无共享句柄。Sunshine WGC 分支、OBS 才用 CreateSharedHandle + OpenSharedResource1 + IDXGIKeyedMutex，且都是「复制到自有纹理再共享」，从不共享 DDA/WGC 的原始 surface。
4. **DDA 零拷贝可行吗？** —— 可行，且比 WGC 更简单：正确做法是「DDA duplication 建在 NVENC 的 D3D11 device 上」→ 同一 device 内 CopyResource（DDA surface → 自有 BGRA 纹理，仍是 GPU 内部拷贝、不落 CPU）→ GPU 色彩转换 BGRA→NV12 → NVENC 直通。不需要跨设备共享句柄。若因架构约束必须分离设备，才需要「CopyResource 到 SHARED 纹理 + CreateSharedHandle + OpenSharedResource1 + KeyedMutex」。

---

## 1. MSDN 官方文档证据

### 1.1 IDXGIResource::GetSharedHandle —— 只对「创建为 shared」的资源有效

原文（learn.microsoft.com dxgi/nf-dxgi-idxgiresource-getsharedhandle，2021-10-13 更新）：

- "Gets the handle to a shared resource."
- "GetSharedHandle returns a handle for the resource that you created as shared (that is, you set the D3D11_RESOURCE_MISC_SHARED with or without the D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX flag)."
- "GetSharedHandle fails if the resource to which it wants to get a handle is not shared."
- "This handle is not an NT handle. Therefore, do not use the handle with CloseHandle, DuplicateHandle, and so on."
- 推荐用 IDXGIResource1::CreateSharedHandle（NT handle，可 CloseHandle/DuplicateHandle）。

**含义**：DDA surface 不是应用创建的 shared 纹理 → GetSharedHandle 失败。

### 1.2 ID3D11Device::OpenSharedResource —— 只接受「shared 创建」的资源

原文（d3d11/nf-d3d11-id3d11device-opensharedresource，2024-02-22 更新）：

- "Give a device access to a shared resource created on a different device."
- "To share a resource between two Direct3D 11 devices the resource must have been created with the D3D11_RESOURCE_MISC_SHARED flag, if it was created using the ID3D11Device interface. If it was created using a DXGI device interface, then the resource is always shared."
- "The only resources that can be shared are 2D non-mipmapped textures."
- 共享纹理更新后须 ID3D11DeviceContext::Flush。

**关键句**：『若资源是通过 DXGI device 接口创建的，则总是共享的』——这句话看似支持「DDA surface 可共享」，但 DDA surface 是桌面合成器（DWM）内部创建的，不是应用调用 IDXGIDevice/ID3D11Device 创建的，且受 DesiredAccess=READ_ONLY 与 duplication 帧所有权约束，不属于该条覆盖的应用场景。Sunshine/OBS 的实际工程实践（§2）也证明它们不共享原始 surface。

### 1.3 Desktop Duplication 概述 —— DDA 是「帧所有权」模型

原文（direct3ddxgi/desktop-dup-api，2021-01-06 更新）：

- "Apps can use the desktop duplication API to access frame-by-frame updates to the desktop."
- "The format of the desktop image is always DXGI_FORMAT_B8G8R8A8_UNORM."
- 示例代码 DUPLICATIONMANAGER::GetFrame：AcquireNextFrame → QueryInterface(ID3D11Texture2D) → 处理 → DoneWithFrame 调 ReleaseFrame 后才 Release。
- 帧所有权：AcquireNextFrame 拿到帧后必须 ReleaseFrame 才能拿下一帧（DXGI_ERROR_INVALID_CALL 表示已释放或非法）。

**含义**：DDA 的 surface 生命周期被 duplication 会话严格管理，AcquireNextFrame 之后、ReleaseFrame 之前是「持有帧」状态。跨设备共享会破坏这个所有权模型（另一个 device 引用同一个 surface，Release 后数据可能被复用/回收）。

### 1.4 IDXGIResource1::CreateSharedHandle —— 正确的共享句柄产生方式

原文（dxgi1_2/nf-dxgi1_2-idxgiresource1-createsharedhandle）：

- 参数 dwAccess 支持 DXGI_SHARED_RESOURCE_READ (0x80000000L) 与 DXGI_SHARED_RESOURCE_WRITE (1)，可 OR 组合。
- 返回的是 NT HANDLE，可跨进程 DuplicateHandle。
- 只对「创建为 shared 的纹理」有效。

这是 Sunshine/OBS 跨设备共享用的 API（非 GetSharedHandle）。

---

## 2. Sunshine / OBS 源码证据（本地 artifacts/zero-copy-src/，file:line 可回查）

### 2.1 Sunshine DDA 分支：抓屏和编码用同一个 device，零拷贝且无跨设备共享

display_base.cpp:116（duplication_t::init）：

    status = output1->DuplicateOutput((IUnknown *) display->device.get(), &dup);

DuplicateOutput 的 pDevice 参数直接传 display->device —— 这个 device 就是后续编码用的 device（display_base.cpp:390 device_t device，display_vram.cpp:1125 d3d11_device = base.device.get()）。即：DDA 帧从一开始就落在编码设备的 GPU 上，CopyResource 在同一 device 内完成，无需共享句柄。

display_base.cpp:88 的 DuplicateOutput1 同样传 display->device.get()。

### 2.2 Sunshine DDA 帧处理：CopyResource 到自有 capture_texture

display_vram.cpp:1629-1645（copy_src_to_img）：

    device_ctx->CopyResource(d3d_img->capture_texture.get(), src.get());

src = AcquireNextFrame 返回的 DDA surface；capture_texture = 自有纹理（见 2.4）。先 CopyResource，再（在调用方）ReleaseFrame。

### 2.3 Sunshine WGC 分支：跨设备共享才用 CreateSharedHandle + OpenSharedResource1

display_vram.cpp:2006（生产侧，WGC 帧已 CopyResource 到 capture_texture 后）：

    status = resource->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ, nullptr, &img->encoder_texture_handle);

display_vram.cpp:1003（消费侧，编码设备）：

    status = device1->OpenSharedResource1(img.encoder_texture_handle, __uuidof(ID3D11Texture2D), (void **) &img_ctx.encoder_texture);

display_vram.cpp:1010：

    status = img_ctx.encoder_texture->QueryInterface(__uuidof(IDXGIKeyedMutex), (void **) &img_ctx.encoder_mutex);

注意：这里共享的是 capture_texture（自有、SHARED_KEYEDMUTEX 标志），不是 DDA/WGC 的原始 surface。共享用 IDXGIKeyedMutex 做跨设备同步（AcquireSync/ReleaseSync）。

### 2.4 Sunshine capture_texture 的创建标志（为什么能共享）

display_vram.cpp:1968-1979：

    D3D11_TEXTURE2D_DESC t {};
    t.Width = img->width; t.Height = img->height;
    t.MipLevels = 1; t.ArraySize = 1; t.SampleDesc.Count = 1;
    t.Usage = D3D11_USAGE_DEFAULT;
    t.Format = img->format;
    t.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
    t.MiscFlags = D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
    device->CreateTexture2D(&t, nullptr, &img->capture_texture);

关键：SHARED_NTHANDLE | SHARED_KEYEDMUTEX —— 这才是「能 CreateSharedHandle + OpenSharedResource1」的前提。DDA surface 没有这些标志。

### 2.5 OBS：OpenSharedResource + KeyedMutex + 先 CopyResource

obs_nvenc-d3d11.c:188：
    hr = device->lpVtbl->OpenSharedResource(device, (HANDLE)(uintptr_t)handle, &IID_ID3D11Texture2D, &input_tex);

obs_nvenc-d3d11.c:194：
    hr = input_tex->lpVtbl->QueryInterface(input_tex, &IID_IDXGIKeyedMutex, &km);

obs_nvenc-d3d11.c:243-247：
    km->lpVtbl->AcquireSync(km, lock_key, INFINITE);
    context->lpVtbl->CopyResource(context, (ID3D11Resource *)output_tex, (ID3D11Resource *)input_tex);
    km->lpVtbl->ReleaseSync(km, *next_key);

OBS 同样是：OpenSharedResource 拿共享句柄 → AcquireSync 上 KeyedMutex 锁 → CopyResource 到自己的 output texture → ReleaseSync → 之后 NVENC 用 output texture。共享的是「捕获端已共享化的纹理」，不是 DDA 原始 surface。

---

## 3. DDA 帧所有权 vs 跨设备共享：时序冲突的正确解法

任务里问「先 Open 再 Release 还是先 Release 再 Open」——两者都不对，正确模型是：

1. AcquireNextFrame 拿帧（进入「持有帧」状态）；
2. 在同一 device 上 CopyResource(自有纹理, DDA surface) —— 把像素复制到自己的可共享纹理；
3. ReleaseFrame（释放 DDA 帧所有权，允许下一帧）；
4. （若需跨设备）CreateSharedHandle(READ) 产生句柄 → 另一 device OpenSharedResource1 + AcquireSync/ReleaseSync。

为什么不能在 DDA surface 上直接 OpenSharedResource：

- DDA surface 受 duplication 帧所有权约束，ReleaseFrame 后内容可能被 DWM 复用/回收，另一个 device 持有引用会导致读脏数据或 DXGI_ERROR_INVALID_CALL；
- DDA surface 非 SHARED 创建，GetSharedHandle/CreateSharedHandle 会失败；
- 共享语义要求「创建者不得在所有人打开前销毁资源」，与 DDA 的「ReleaseFrame 即归还」冲突。

结论：『先 CopyResource 到自有共享纹理，再 ReleaseFrame，之后跨设备 Open』——复制是解开帧所有权与共享冲突的唯一正确手段。

---

## 4. 结论：DDA 零拷贝是否可行 + 正确时序 + 坑清单

### 4.1 可行性：可行，两条路线

| 路线 | 做法 | 零拷贝 | 复杂度 | 结论 |
|---|---|---|---|---|
| A（推荐，Sunshine DDA 同款） | DDA DuplicateOutput 建在 NVENC 的 D3D11 device 上 → 同 device CopyResource（DDA→BGRA 纹理）→ GPU 转换 BGRA→NV12 → NVENC 直通 | 是（全程 GPU） | 低（复用现有 NvencSdkEncoder + GPU 转换器） | 首选，无需跨设备共享句柄 |
| B（架构被迫分离设备时） | DDA 在设备 X 抓 → CopyResource 到 SHARED_NTHANDLE+SHARED_KEYEDMUTEX 纹理 → CreateSharedHandle(READ) → 设备 Y OpenSharedResource1 + KeyedMutex → 编码 | 是（多一次 GPU 内 CopyResource + 同步） | 中（跨设备同步） | 兜底 |

### 4.2 正确时序（路线 A）

    AcquireNextFrame → 同 device CopyResource(DDA surface → 自有 BGRA 纹理) → ReleaseFrame
    → GPU 转换 BGRA→NV12（VideoProcessor/shader）
    → NvEncRegisterResource(NV12 纹理) → Map → Encode → LockBitstream

### 4.3 坑清单

1. DDA surface 不可直接共享：GetSharedHandle/CreateSharedHandle 对 DDA 原始 surface 失败（非 SHARED 创建）；必须 CopyResource 到自有 SHARED_NTHANDLE 纹理。
2. 帧所有权：AcquireNextFrame 后必须 ReleaseFrame 才能拿下一帧；CopyResource 必须在 ReleaseFrame 之前完成，否则读到脏数据。
3. 同 device 是关键：DuplicateOutput(pDevice) 的 pDevice 必须与编码 device 相同，否则又回到跨设备问题。Sunshine 就是传 display->device（= 编码 device）。
4. 共享纹理更新后 Flush：跨设备共享时，写入方须 ID3D11DeviceContext::Flush（OpenSharedResource 文档明确要求），否则另一设备读到旧数据。
5. KeyedMutex 必须：跨设备共享（路线 B）必须 SHARED_KEYEDMUTEX + AcquireSync/ReleaseSync，否则竞态；Sunshine/OBS 都这么做。
6. 格式固定 B8G8R8A8：DDA 桌面图格式恒为 DXGI_FORMAT_B8G8R8A8_UNORM（GetDesc 文档）；NVENC 只收 NV12，所以仍需要 GPU 色彩转换这一步（同 WGC 零拷贝的缺口②）。
7. DDA 帧所有权与共享资源生命周期冲突：共享资源要求「创建者不得在所有人打开前销毁」，与 DDA「ReleaseFrame 即归还」冲突——所以不能共享 DDA 原始 surface，必须复制。
8. ROI 判断：当前 SoulLink 的 DDA 后端是「AcquireNextFrame → QueryInterface(ID3D11Texture2D) → staging+Map 读回 CPU」（DxgiDdaCapture.ReadBackTexture），零拷贝收益 = 砍掉 staging+Map 读回（约3-6ms）+ CPU BgraToNv12（约3-6ms）。但前提是 DDA 的 duplication 要改建在 NVENC device 上（当前 DxgiDdaCapture 自建 D3D11 device，与 NVENC device 不同），改动量大于 WGC 零拷贝（WGC 已把帧留在 GPU，只差转换器）。

---

## 5. 证据边界

- 本文结论基于：MSDN 官方文档原文（GetSharedHandle / OpenSharedResource / CreateSharedHandle / Desktop Duplication / GetDesc）+ 本地抓取的 Sunshine/OBS 源码（artifacts/zero-copy-src/，file:line 可回查）。
- 未实测：未在本机做「DDA surface 直接 GetSharedHandle」的运行时验证（只读任务约束，不跑探针）。「DDA surface 非 SHARED 创建、GetSharedHandle 失败」是基于官方文档语义的强推断，标注为【官方文档语义推断】而非【实测】。
- Sunshine/OBS 源码为 GPL-3.0，只读参考，不可抄。
- 结论「路线 A（同 device）优于路线 B（跨设备）」与 GPU-ZERO-COPY-IMPL-PLAN-20261124.md（WGC 路线）一致：都是「让抓屏与编码同 device + GPU 色彩转换」这个统一范式。
