# 交互体验：连续缩放 / 平移 / 画面适配 / 坐标变换（源码级）

> 成员：verifier（源码级实证；RustDesk + Moonlight + scrcpy + 用户向日葵录屏 3 帧）
> 日期：2026-11-25
> 背景：用户批评「双指缩放应是自由缩放不是档位」「双指滑动是调整位置，不是锁死」「铺满/等比没生效」。
> 方法：RustDesk flutter 源码（canvas/手势/输入消费）+ Moonlight Game.java（t32 已落盘）+ scrcpy screen.c（已落盘）+ 向日葵 3 帧（v0032/v0134/v0142）。

---

## 〇、一句话结论

**连续缩放+平移的正确实现 = 一个三元组状态 (scale, panX, panY) + 双指 updateScale(比例, 焦点) 一步到位 + pan 用增量累加 + 边界 clamp**。
RustDesk 全部源码确认：CanvasModel 就是三个 double 字段 _x/_y/_scale（model.dart:2241-2245），updateScale(v, focalPoint) 一次同时改 scale 和 pan（model.dart:2737-2756），双指手势里 scale 与 pan 同帧都做、不互斥（remote_input.dart:491-494）。

---

## 一、连续缩放的实现（源码确认）

### 1.1 状态模型（RustDesk CanvasModel）

flutter/lib/models/model.dart:2239-2247：

    class CanvasModel with ChangeNotifier {
      double _x = 0;      // image offset of canvas（平移 X，单位=屏幕像素）
      double _y = 0;      // image offset of canvas（平移 Y）
      double _scale = 1.0;
      double _devicePixelRatio = 1.0;
      Size _size = Size.zero;   // 客户端视口大小

- 没有「档位」——两个 double 偏移 + 一个 double 缩放，全部连续。
- 我们的 1x/2x/3x/4x 按钮档位是错的；应改成一个连续 double（可保留 2x 快捷钮作「跳到该值」，但捏合必须连续）。

### 1.2 缩放范围限制（源码确认 model.dart:2737-2744）

    updateScale(double v, Offset focalPoint) {
      final s = _scale;
      _scale *= v;                                   // 增量缩放（乘法）
      final maxs = parent.target?.imageModel.maxScale ?? 1;
      final mins = parent.target?.imageModel.minScale ?? 1;
      if (_scale > maxs) _scale = maxs;
      if (_scale < mins) _scale = mins;

- 乘法增量（_scale *= v），每帧传入 d.scale/_scale（两指距离比值）。
- clamp 的 min/max 来自 imageModel（minScale 默认 1），不是写死 1x~4x。

### 1.3 缩放中心点 = 双指焦点 focalPoint，不是屏幕中心（源码确认 model.dart:2745-2751）

    // (focalPoint.dx - _x_1) / s1 + displayOriginX = (focalPoint.dx - _x_2) / s2 + displayOriginX
    // _x_2 = focalPoint.dx - (focalPoint.dx - _x_1) / s1 * s2
    _x = focalPoint.dx - (focalPoint.dx - _x) / s * _scale;
    final adjust = getAdjustY();
    _y = focalPoint.dy - adjust - (focalPoint.dy - _y - adjust) / s * _scale;

- focalPoint = ScaleUpdateDetails.focalPoint = 双指中点（Flutter Scale 手势原生提供）。
- 含义：缩放前后，双指中点指着的远端内容保持不动——「捏着哪里就放大哪里」，这就是自由缩放的手感核心。

### 1.4 边界约束：不能拖出黑边（源码确认 model.dart:2451-2457 与 2833-2858）

    // _resetCanvasOffset：居中
    _x = (size.width - displayWidth * _scale) / 2;
    _y = (size.height - displayHeight * _scale) / 2;

    // _moveToCenterCursor 的 clamp（:2841-2857）
    final minX = _size.width + (imageRect.left - imageRect.right) * _scale;   // = width - imageW*scale
    final minY = _size.height + (imageRect.top - imageRect.bottom) * _scale;
    if (minX < 0) { _x = min(max(offsetToCenter.dx, minX), maxX=0); }
    // 注释原文：_size.width > imageW*scale 时（缩太小了）不改 _x（保持居中）

- 边界规则：图像缩放后比视口大 → pan 限制在 [width - imageW*scale, 0]；
  图像缩放后比视口小 → 固定居中（无平移余量）。
- 我们「锁死位置」的根因：scale>1 后没有 panX/panY 状态，也没有任何 clamp 逻辑。

---

## 二、双指手势区分（捏合缩放 vs 平移 vs 滚动）

### 2.1 RustDesk：状态机 + 双指同帧同时做 scale 和 pan（源码确认）

flutter/lib/common/widgets/gestures.dart:6-11 状态机：

    enum GestureState { none, oneFingerPan, twoFingerScale, threeFingerVerticalDrag }

- CustomTouchGestureRecognizer extends ScaleGestureRecognizer（gestures.dart:13），按 d.pointerCount 切状态：
  1 指→oneFingerPan（带 debounce :125-142 防误触发），2 指→twoFingerScale，3 指→threeFingerVerticalDrag（:48-61）。
- 官方 ScaleUpdateDetails 本来就同时给 scale（两指距离比值）和 focalPoint/focalPointDelta（双指中点位移）——
  与 Android 官方 detectTransformGestures 同款语义：一个回调同时给 zoom 和 pan，不需要自己区分。

### 2.2 消费侧：scale 与 pan 同帧都做（remote_input.dart:461-495）

    onTwoFingerScaleUpdate(ScaleUpdateDetails d) async {
      // ... special drag 分支略
    } else {
      // mobile
      ffi.canvasModel.updateScale(d.scale / _scale, d.focalPoint);   // ① 连续缩放（焦点=双指中点）
      _scale = d.scale;
      ffi.canvasModel.panX(d.focalPointDelta.dx);                    // ② 双指中点位移 = 平移画布
      ffi.canvasModel.panY(d.focalPointDelta.dy);
    }

- 双指捏合（距离变化）→ 缩放；双指整体拖动（中点位移）→ 平移；同时发生（捏着移）→ 同时应用。
- 回答 Lead「二选一还是同时支持」：RustDesk 是同时支持——scale 和 pan 是同一帧里的两个正交分量，不该互斥。

### 2.3 双指「捏合」vs「同向滑动（滚动）」的区分

- RustDesk 移动端双指不做滚动：滚轮走三指垂直拖（gestures.dart:10 threeFingerVerticalDrag，:53-61）；
- 我们当前「距离变化 vs 同向滑动」思路方向对但多余：Flutter Scale 的 scale 分量只在两指距离变化时非 1，
  同向平移时 scale 约 1 而 focalPointDelta 大——数学上天然正交，不需要自己加阈值判断；
- 若要防「纯平移时轻微误缩放」，可加死区：if ((d.scale - 1).abs() < 0.02) d.scale = 1.0; [推测]（官方未见此死区）。

---

## 三、画面适配：等比 vs 铺满（含 SurfaceView 的坑）

### 3.1 「没生效」的最可能原因：onMeasure 结果被父布局/生命周期吞掉（源码确认）

Moonlight ui/StreamView.java:30-60（t32 已落盘全文）的 onMeasure：

    protected void onMeasure(int widthMeasureSpec, int heightMeasureSpec) {
      if (desiredAspectRatio == 0) { super.onMeasure(...); return; }
      int widthSize = MeasureSpec.getSize(widthMeasureSpec);
      int heightSize = MeasureSpec.getSize(heightMeasureSpec);
      if (widthSize > heightSize * desiredAspectRatio) {
        measuredHeight = heightSize;
        measuredWidth = (int)(measuredHeight * desiredAspectRatio);
      } else {
        measuredWidth = widthSize;
        measuredHeight = (int)(measuredWidth / desiredAspectRatio);
      }
      setMeasuredDimension(measuredWidth, measuredHeight);
    }

- Moonlight 能生效的前提：activity_game.xml 根布局是 merge + StreamView layout_gravity=center（t32 已落盘）——
  FrameLayout 会尊重子 View 的 onMeasure 收缩并按 gravity 居中。
- 我们的静默失效点（两个，源码确认）：
  1. 若视频 View 被放在强制 match_parent 的容器，onMeasure 收缩结果被父布局覆盖回去；
  2. Game.java:953-959 的二选一：stretchVideo=true 时走 Holder.setFixedSize(w,h)——Surface 的 buffer 尺寸钉死而显示尺寸仍由布局决定，
     两者不一致时表现为「铺满拉伸」，等比开关自然没生效。

### 3.2 铺满 = 拉伸还是裁剪？（源码确认：官方「铺满」全是拉伸）

- Moonlight stretchVideo=true → setFixedSize + 布局铺满 = 拉伸变形；
- scrcpy SC_RENDER_FIT_STRETCHED（options.h:254，screen.c:212-217 rect.w=window.w/h=window.h）= 拉伸；
- 真正的「裁剪铺满 cover（不失真）」官方三家都没做——默认全是 letterbox 保持比例。
  [推测] 原因：远控裁剪会丢画面边缘内容（任务栏/聊天窗被裁掉不可接受）。
- 结论：用户嫌黑边，正解 =「等比放大到 cover + 连续 pan 回去看被裁部分」——依赖第一节的连续缩放/平移。RustDesk zoom>minScale 后正是这个能力。

### 3.3 SurfaceView vs TextureView（源码级判断）

| 能力 | SurfaceView | TextureView |
|---|---|---|
| 等比 letterbox | 靠 onMeasure（受父布局配合） | setTransform(matrix) 自由缩放/平移/旋转，必生效 |
| 裁剪铺满 cover | 做不干净 | matrix 一行 |
| 连续 zoom+pan | 需重算 onMeasure，平移靠 margin，别扭 | matrix.postScale/postTranslate 直接表达 |
| 延迟 | 更低（合成器直通） | 略高（多一层离屏缓冲） |

- Moonlight 选 SurfaceView 为延迟（游戏串流），letterbox 只需居中+等比，onMeasure 够用；
- 我们要做 zoom+pan+cover，TextureView 的 matrix 与 CanvasModel(_x,_y,_scale) 一一对应，最短路径；
- [推测] 坚持 SurfaceView 也可用外层 Compose Box graphicsLayer scale/translate 包住 AspectSurfaceView（ui-fixer 第七批已用 scaleX/scaleY 走此路），但 pan 也要走 graphicsLayer，不改 View layout。

---

## 四、坐标变换公式（可直接抄）

### 4.1 正向：远端坐标 = f(屏幕坐标, scale, pan)（RustDesk 源码确认 model.dart:3328-3334）

    Offset _getNewPos(double x, double y, double adjust) {
      final xoffset = canvasModel.x;    // = panX
      final yoffset = canvasModel.y;    // = panY
      final newX = (x - xoffset) / scale + _displayOriginX;
      final newY = (y - yoffset - adjust) / scale + _displayOriginY;
      return Offset(newX, newY);
    }

完整公式（adjust=0）：远端坐标 = (屏幕坐标 - pan) / scale + displayOrigin。
- pan = (panX, panY) = 图像在屏幕上的偏移（居中时 = (viewW - videoW*scale)/2）；
- displayOrigin = 远端显示原点（我们是 0~1 归一化，此项为 0）。

### 4.2 落到我们（0~1 归一化 + videoRect + zoom）

ui-fixer 第七批的逆变换缺 pan（letterbox 偏移与平移）。合并 RustDesk 公式后：

    // videoRect = 等比模式下视频在屏幕内的真实矩形（letterbox 后）
    // zoom >= 1；panX/panY = 当前平移量（屏幕像素，clamp 后）
    // sx, sy = 手指屏幕坐标
    inVideoX = sx - videoRect.left;
    inVideoY = sy - videoRect.top;
    nx = ((inVideoX - videoRect.width/2)  / zoom + videoRect.width/2  + panX) / videoRect.width;
    ny = ((inVideoY - videoRect.height/2) / zoom + videoRect.height/2 + panY) / videoRect.height;
    nx = nx.coerceIn(0,1); ny = ny.coerceIn(0,1);

（zoom 围绕视口中心时逆变换=先减中心、除 zoom、加中心、加 pan；若 zoom 围绕焦点（RustDesk 式），pan 已含焦点补偿，直接 (s - pan)/scale。）

### 4.3 校验与黑边处理（RustDesk getVisibleRect model.dart:3240-3248）

    Rect getVisibleRect() {
      final x0 = _displayOriginX - xoffset / scale;
      final y0 = _displayOriginY - yoffset / scale;
      return Rect.fromLTWH(x0, y0, size.width / scale, size.height / scale);
    }

- 可见远端区域 = 左上 -pan/scale、大小 size/scale——同一公式的正向校验（isInRemoteRect :3316-3325）。
- 黑边触摸：getRemotePosInRect 点不在可见矩形时返回 null（:3316-3325）→ RustDesk 黑边事件被丢弃，不是 clamp [源码确认]。
  与 ui-fixer 第七批「clamp 到边缘」不同——两种都可，但黑边 tap 呼出工具栏应走 UI 层判断，不进输入。

---

## 五、追加：向日葵录屏 3 帧的实现还原

### v0032（等比居中 + 图标列在黑边区）

- 视频（横屏桌面）在竖屏上等比居中，上下黑边；右侧图标列浮在黑边区，不遮画面。
- 实现还原：视频 View 尺寸按比例收缩居中（letterbox），图标列是 Compose 层兄弟节点 align CenterEnd，
  不是塞进 videoRect——黑边区天生是它的容身之处。
- 层级：SurfaceView 的 Surface 在窗口 Z 序最底（合成器直通），Compose 层天然盖在其上；TextureView 按 View 树顺序（放手势层之下即可）。
- 我们的差距：ui-fixer 第七批图标列已 align CenterEnd；等比生效后黑边自然出现，图标列天然落在黑边。

### v0142（按键分布在画面外黑边 + 画面边缘）

- 实测：Esc/数字/字母键一半落在上方黑边，右列键（Ctrl/Enter）横跨画面右缘与黑边——按键层坐标系是全屏，不是 videoRect。
- 结论：向日葵虚拟按键层锚定全屏坐标系，靠用户拖动自由摆放，不感知 videoRect；画面外按键照样发键（全屏 touch 层消费）。
- 对我们：ControlsLayout 归一化 anchor 按全屏算则与向日葵一致；旋转/比例变化后按键可能落到画面上——向日葵没处理（用户自己拖），我们也先不处理，交给编辑模式。
- v0134：游戏键盘入口 = 旋转 90 度的横版面板卡片（四标签+搜索），即键盘选择页是独立面板——与我们 BottomSheet 方案等价。

---

## 六、我们该怎么改（具体到 API）

### 6.1 状态模型（照抄 RustDesk 三元组）

    class VideoTransform {
      var scale = 1f          // clamp [minScale=1, maxScale=4]
      var panX = 0f; var panY = 0f
      fun applyGesture(scaleRatio: Float, focal: Offset, focalDelta: Offset, viewW: Float, viewH: Float, videoW: Float, videoH: Float) {
          scale = (scale * scaleRatio).coerceIn(minScale(), maxScale)
          // 焦点补偿（RustDesk model.dart:2747 同款）
          panX = focal.x - (focal.x - panX) / scaleRatio * scale
          panY = focal.y - (focal.y - panY) / scaleRatio * scale
          panX += focalDelta.x; panY += focalDelta.y   // 双指整体拖动
          clamp(viewW, viewH, videoW, videoH)
      }
      fun clamp(...) {
          val maxX = 0f; val minX = viewW - videoW * scale
          panX = if (minX < 0f) panX.coerceIn(minX, maxX) else 0f;  // 缩太小→锁居中
          // 同 Y
      }
    }

### 6.2 双指手势（对齐 gestures.dart 状态机）

- Compose awaitEachGesture + awaitFirstDown 后数手指：1 指→桌面拖动（现有），2 指→twoFinger（同帧读 PointerEvent.calculateZoom()/calculateCentroid()，与 Flutter Scale 等价），3 指→滚轮（已有）；
- 不要再做「距离变化 vs 同向滑动」判断——scale 与 centroid 位移是正交分量，同帧一起用；
- [推测] 可加 (zoom-1).abs()<0.02 → 1 死区防手抖。

### 6.3 坐标换算（替换现有 nxOf/nyOf）

- 按 4.2 公式重写：先 videoRect 原点、再 zoom 逆变换、加 pan、除 videoRect 尺寸、clamp；
- 光标绘制做同款正向变换（远端 0~1 → 屏幕）；
- 黑边触摸：选「丢弃」对齐 RustDesk（工具栏/呼出走 UI 层，不进输入）。

### 6.4 缩放应用（View 层）

- 沿用 ui-fixer 第七批 AspectSurfaceView.scaleX/scaleY + graphicsLayer translationX/Y（pan）——不改 onMeasure（那是 letterbox 用的）；
- zoom=1 时 pan 强制 0（RustDesk 同款：缩比视口小则锁居中）。

### 6.5 双击复位

- RustDesk resetOffset()（model.dart:2719-2726）→ _resetCanvasOffset（居中+scale 回 viewStyle）；
- 我们已有 zoom>1 双击=复位，语义一致，保留。

---

## 七、诚实标注

- RustDesk model.dart 159KB 关键段（2239-2935/3240-3360）、gestures.dart 23.8KB 全文、remote_input.dart 21.9KB（445-510）、remote_page.dart 57KB（560-740）——行号均来自本轮实取源码。
- Moonlight/scrcpy 结论复用 t32 落盘源码（StreamView/Game/screen.c），行号一致。
- canvas_model.dart 独立文件在 RustDesk master 不存在（404），内容在 model.dart 内——如实记录。
- 向日葵内部实现无源码，v0032/v0134/v0142 三帧为行为反推，标 [推测]（3 处）。
- 延迟数字无出处不写。未改代码、未装/连手机。

