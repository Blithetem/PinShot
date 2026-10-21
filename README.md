# PinShot

Windows 截图 / 贴图 / 标注 / 长截图 / 录屏工具，用 C# + .NET 8 + WPF 实现，目标是日常替代 PixPin 的核心功能。

当前状态：**截图、标注、贴图、长截图、GIF、录屏、OCR、命令行**均已可用。

## 功能

### 截图

- 全局热键 `Ctrl+1` 或 `Ctrl+Shift+S`（被占用时自动降级，实际组合见托盘提示）
- 全屏遮罩选区、8 个手柄调整、`空格 + 拖动`整体移动
- 方向键按像素微调选区，`Shift` 固定 1:1，`Ctrl+A` 全选屏幕
- 鼠标悬停高亮窗口，单击直接选中整窗
- 十字辅助线 + 放大镜取色（默认 RGB，按住 `Shift` 显示 HEX），`C` 复制颜色
- `R` / `Shift+R` 载入历史选区（保留最近 10 条）
- 点击尺寸框可手动输入宽高；`~` 截图包含鼠标指针
- 圆角截图（选项条按钮或 `Ctrl+R` 循环半径）、输出可加阴影
- 截图时自动隐藏所有贴图窗口，不会把贴图截进图里

### 标注（14 种工具）

矩形、椭圆、直线、**折线**、箭头、画笔、荧光笔、文字、序号、马赛克、橡皮擦、**聚光灯**、**放大镜（2/4/8×）**、**水印**

- 二次编辑：点击标注选中（虚线框 + 8 手柄），拖动移动、手柄缩放、`Delete` 删除
- 修改颜色 / 线宽会直接作用到选中的标注
- 滚轮调线宽，`Ctrl+滚轮` 调透明度
- `Shift+1`~`Shift+0` 快速切换工具
- 文字支持字号 12/16/20/28 与背景色（无 / 白 / 黄 / 黑）
- 水印支持时间变量 `$yyyy-MM-dd HH:mm:ss$`
- 撤销 / 重做：`Ctrl+Z` / `Ctrl+Y`（或 `Ctrl+Shift+Z`）

### 贴图

- 拖动移动、方向键微调、滚轮缩放（保持中心）、`Ctrl+滚轮` 调透明度、中键重置
- `L` 锁定、`T` 置顶、`Space` 标注、`F2` 标题、`R` 缩略图、`Ctrl+W` / `Esc` / 双击关闭、`Ctrl+D` 销毁
- `Ctrl+拖拽` 把贴图拖到其他应用
- 图像处理：左右旋转、水平/垂直翻转、灰度、反色、亮度 ±10%、透明度 ±10%、重置
- 关闭的贴图进历史（保留 10 条），`Ctrl+3` 恢复；销毁不入历史
- 分组：`Ctrl+~` 切换分组视角
- 支持图像 / 文本 / 颜色 / 文件四种贴图，`Ctrl+2` 从剪贴板贴图
- 托盘菜单可批量关闭 / 销毁 / 把屏幕外的贴图拉回来

### 长截图

- 截图工具条点「滚动」或托盘菜单进入
- 选区后自动检测滚动并拼接，保留选区边框，悬浮条显示已捕获尺寸
- 完成后在独立编辑窗口中查看、标注、保存

### 录屏 / GIF

- 托盘菜单或 `Ctrl+Shift+R` → 拖选区域 → 悬浮控制条 → 停止 → 保存
- 视频为 AVI（MJPEG），GIF 10fps 自动缩放

### OCR

- 使用系统自带 Windows OCR（`Windows.Media.Ocr`），离线、无模型下载
- 截图工具条点「识别」，或编辑态按 `O`
- 可选在线 OCR：在本机 `%LOCALAPPDATA%\PinShot\ocr.json` 里填写 Endpoint / ApiKey / Model 并打开 `UseOnline`（仓库不含任何服务地址与凭据）

## 构建

需要 .NET 8 SDK（含 Windows Desktop 工作负载）。

```powershell
dotnet build PinShot.sln -c Release
```

产物：`src\PinShot.App\bin\Release\net8.0-windows10.0.19041.0\PinShot.exe`

也可以直接运行 `run.cmd`（自动找 Release 产物并启动）。

## 快捷键

| 操作 | 快捷键 |
|---|---|
| 截图 | `Ctrl+1` / `Ctrl+Shift+S` |
| 贴图（剪贴板） | `Ctrl+2` |
| 恢复上次关闭的贴图 | `Ctrl+3` |
| 录屏开始 / 停止 | `Ctrl+Shift+R` |
| 切换贴图分组视角 | `Ctrl+~` |
| 复制截图 | `Enter` / `Ctrl+C` |
| 保存截图 | `Ctrl+S` |
| 贴到屏幕 | `P` |
| 识别文字 | `O` |
| 全选屏幕 | `Ctrl+A` |
| 历史选区 | `R` / `Shift+R` |
| 取色并复制 | `C` |
| 圆角循环 | `Ctrl+R` |
| 截图带光标 | `~` |
| 退出标注 / 取消 | `Esc`（分级：先取消选中，再退出标注，最后取消截图） |

## 命令行

命令行模式不创建托盘与全局热键，执行完自动退出，适合脚本调用。

| 命令 | 说明 |
|---|---|
| `PinShot.exe --capture-full [--output <文件>] [--clipboard] [--delay <毫秒>]` | 截取整个虚拟屏幕 |
| `PinShot.exe --capture-region [--output <文件>] [--clipboard] [--delay <毫秒>]` | 交互式框选 |
| `PinShot.exe --capture-region --region <x,y,w,h> [--output <文件>]` | 直接截取指定矩形，不弹界面 |
| `PinShot.exe --pin <图片路径>` | 把图片贴到屏幕上 |
| `PinShot.exe --help` | 帮助 |

退出码：`0` 成功，`1` 参数或执行失败，`2` 用户取消。

> 截图类命令（`--capture-full`、`--capture-region --region`、`--help`）走不启动 WPF 的快速路径：全屏截图约 **150 ms**，`--help` 约 **65 ms**。
> `--pin` 和交互式框选需要窗口，仍会正常启动 WPF（约 1.2 s）。

示例：

```powershell
$exe = "src\PinShot.App\bin\Release\net8.0-windows10.0.19041.0\PinShot.exe"

# 全屏截图
& $exe --capture-full --output "$env:TEMP\shot.png"

# 延迟 2 秒截全屏并复制到剪贴板
& $exe --capture-full --clipboard --delay 2000

# 直接截取一块区域（不弹选区）
& $exe --capture-region --region 100,100,800,600 --output "$env:TEMP\region.png"

# 把图片贴到屏幕上
& $exe --pin "$env:TEMP\shot.png"
```

## 自检与回归

程序内置自检入口，可用一条命令跑完整回归（不依赖模拟鼠标键盘）：

```powershell
pwsh -NoProfile -File tools\verify-core.ps1
```

覆盖：CLI 全屏/选区截图、截图装饰（圆角/阴影/光标）、贴图管理（26 项）、长图编辑器、长截图拼接、GIF、录屏、OCR。

单项自检：

```powershell
PinShot.exe --capture-test       # 进入截图流程（交互）
PinShot.exe --pin-test           # 贴图链路
PinShot.exe --pin-manager-test   # 贴图历史/分组/图像处理（26 项断言）
PinShot.exe --decorate-test      # 圆角/阴影/光标输出
PinShot.exe --editor-test        # 长图编辑器工具栏布局
PinShot.exe --longshot-test      # 长截图拼接算法（合成帧）
PinShot.exe --gif-test           # GIF 录制 2 秒
PinShot.exe --record-test        # 录屏 3 秒
PinShot.exe --ocr-test           # OCR 引擎
PinShot.exe --help --diag-startup  # 输出启动各阶段耗时到 %TEMP%\pinshot-startup.txt
```

日志：`%LOCALAPPDATA%\PinShot\logs\`（`error.log` 异常、`info.log` 启动与热键绑定）。

## 已知限制

- 截图使用 GDI 快照，截图界面打开期间新弹出的窗口不会更新
- 录屏只有 AVI/MJPEG，无音轨、无暂停；GIF 最长约 18 秒
- 热键固定（`Ctrl+1` / `Ctrl+2` / `Ctrl+3` / `Ctrl+Shift+S` / `Ctrl+Shift+R`），暂无可视化设置界面
- 长截图上限 12000px，超出静默截断
- 贴图分组无重命名 UI，位置不持久化
- OCR 仅中文/系统语言，表格识别、公式识别、翻译、二维码尚未实现

## 目录结构

```
src/PinShot.App/
  App.xaml(.cs)      入口：单实例、托盘、热键、命令分发、自检入口
  Interop/           Win32 P/Invoke、全局热键消息窗口
  Capture/           屏幕捕获、选区遮罩、窗口吸附、历史选区
  Editor/            标注引擎、14 种标注、工具栏、长图编辑窗口
  Pin/               贴图窗口、贴图管理/历史/分组
  LongShot/          滚动长截图
  Recorder/          录屏与 GIF
  Ocr/               Windows OCR 与可选在线 OCR
  Cli/               命令行
  Services/          托盘等
tools/verify-core.ps1  命令行回归
```

## 许可

代码采用 [MIT](LICENSE)。
