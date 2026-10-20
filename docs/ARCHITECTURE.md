# PinShot 架构

## 设计原则

1. **物理像素优先**：捕获、选区、贴图坐标全部以物理像素计算，只在 WPF 呈现层转 DIP；否则多屏不同缩放必然错位。
2. **捕获与呈现解耦**：`ICaptureSource` 是最小接口，先实现 GDI，后续替换 `Windows.Graphics.Capture` / DXGI 不影响上层。
3. **无主窗口**：托盘 + 全局热键驱动，避免窗口生命周期干扰截图。
4. **动作即结果**：截图结束返回 `CaptureResult(Image, Action)`，由 App 统一分发，方便后续接入脚本/历史/上传。

## 分层

```
┌──────────────────────────────────────────┐
│ App（组合根）                             │
│  单实例 / 托盘 / 热键 / 结果分发            │
├──────────────────────────────────────────┤
│ Capture     选区遮罩、裁剪                 │
│ Editor      标注（P2）                     │
│ Pin         贴图窗口                       │
│ LongShot   长截图（P3）                    │
│ Recorder   录屏（P4）                      │
│ Ocr        识别（P5）                      │
├──────────────────────────────────────────┤
│ Services   配置 / 历史 / 热键 / 托盘        │
│ Interop    Win32 P/Invoke                  │
└──────────────────────────────────────────┘
```

## 当前实现

| 文件 | 职责 |
|---|---|
| `App.xaml.cs` | 单实例 Mutex、托盘、热键注册、截图流程编排、结果分发 |
| `Interop/Native.cs` | `RegisterHotKey` / `SetWindowPos` / `DeleteObject` 等 P/Invoke |
| `Interop/HotkeyWindow.cs` | `HwndSource` 隐藏消息窗口，接收 `WM_HOTKEY` |
| `Capture/ScreenCapture.cs` | 虚拟屏幕整体捕获 → `BitmapSource` |
| `Capture/CaptureOverlay.xaml(.cs)` | 全屏遮罩、拖动选区、EvenOdd 挖洞、键盘动作 |
| `Capture/CaptureResult.cs` | `CaptureAction`（Copy/Save/Pin）与结果记录 |
| `Pin/PinWindow.xaml(.cs)` | 置顶贴图窗口：拖动、缩放、右键菜单 |
| `Services/TrayService.cs` | `NotifyIcon` 托盘与菜单 |

## 关键实现细节

- **窗口覆盖**：`OnLoaded` 里用 `SetWindowPos` 按物理像素铺满虚拟屏幕，而不是依赖 WPF 的 `Left/Width`（DIP），避免 DPI 缩放导致遮罩偏移。
- **选区遮罩**：`GeometryGroup{FillRule=EvenOdd}` = 全屏矩形 + 选区矩形，一次绘制挖洞，比四个矩形拼更快且无缝隙。
- **坐标映射**：`ScaleX = snapshot.PixelWidth / BgImage.ActualWidth`，鼠标 DIP → 图像像素；绘制时反向转换。
- **裁剪**：`CroppedBitmap(image, Int32Rect)`，直接零拷贝视图。

## 与 PixPin 的模块对应（逆向参考，不复制代码/资源）

| 能力 | PixPin 模块 | 本项目计划 |
|---|---|---|
| 屏幕枚举/抓取 | `PixScreenManager` + `PixWinCapture` + `PixWin32CaptureCore` | `ScreenCapture` → 后续 `GraphicsCaptureSource` |
| 双引擎兜底 | DXGI 优先、Qt 兜底 | Graphics Capture 优先、GDI 兜底 |
| UI 吸附 | `UiRegionDetector`（UIA + 模型） | `UiaRegionDetector`（只做 UIA，模型后续再说） |
| 选区 | `TrigerGraphicRectSelectItem` | `CaptureOverlay` 的选区逻辑 |
| 标注 | `EditItem*` 家族 | `Editor/`（P2） |
| 贴图 | `PinWindow*` 9 类 | `PinWindow` → 后续拆分 Text/File/ROI/Dynamic |
| 长截图 | `LongShotWidget` + `PixStitching` | `LongShot/`（P3） |
| 录屏 | `PixMovie` 多轨 + `PixAVCodec` | `Recorder/`（P4） |
| OCR | `PixOCR2` + `PixModelRunner` | `Ocr/`（P5） |
| 脚本 | `QJSEngine` + `pixpin.*` | 可选：Jint（P6 之后） |

## 技术选型与许可

当前 NuGet 依赖只有 **SharpAvi 3.0.1（MIT）**；WPF / WinForms / System.Drawing 来自 .NET 8；系统能力通过 `user32.dll` / `gdi32.dll` 的 P/Invoke 调用。

后续各期计划引入：

| 用途 | 选型 | 许可 | 备注 |
|---|---|---|---|
| 录屏编码（当前） | SharpAvi + MJPEG（WPF JpegBitmapEncoder） | MIT（SharpAvi） | 不依赖系统编码器，输出 AVI |
| 录屏编码（备选） | Media Foundation H.264 / OpenH264 | 系统 / BSD + Cisco 专利 | 本机 MF H.264 编码链不可用（`MF_E_TOPO_CODEC_NOT_FOUND`），暂未启用 |
| 捕获（升级） | `Windows.Graphics.Capture` | 系统 | 后续替换 GDI |
| 图像处理（可选） | `SixLabors.ImageSharp` | **Six Labors Split License** | 开源免费，商业闭源需注意 |
| OCR 推理 | `Microsoft.ML.OnnxRuntime` | MIT | |
| OCR 模型 | PaddleOCR PP-OCRv4 | Apache-2.0 | 按需下载 |
| 二维码 | `ZXing.Net` | Apache-2.0 | |
| 长图匹配 | `OpenCvSharp4` | Apache-2.0 | |
| 脚本引擎（可选） | `Jint` | BSD-2-Clause | |

**避开**：GPL 版 FFmpeg、来源不明的图标与文案、直接照搬 PixPin 的配置键名与 UI 文案。


