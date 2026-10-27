# CHANGELOG — WinKit

统一桌面助手：TodoList + Clipboard + 截图标注 + 本地 OCR 翻译。
格式参照 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)；版本号见 `WinKit.csproj` 的 `<Version>`。

## [Unreleased]

## [2.2.0] — 2026-10-27

### Added 新增（自上游 li5bo5/WinKit v2.11 移植）
- **FastClipboard**（`Clipboard/Services/FastClipboard.cs`）：Win32 API 直写剪贴板，绕过 WPF `Clipboard.SetText` 的阻塞重试。文本回填失败自动回退 WPF 通道；图片复制改为独立 STA 线程写入，不阻塞 UI；回填写图失败时中止回填，避免把旧剪贴板内容粘出去。
- **待办回收站**：删除的待办先进回收站，托盘菜单新增「待办历史」入口（`Todo/HistoryWindow`）。支持还原（保持原 ID 与创建时间，还原到列表顶部）、彻底删除、清空全部；窗口失焦自动隐藏、Esc 关闭。存储为 `%APPDATA%/WinKit/recyclebin.jsonl`（JSON Lines，原子写 + 损坏行容错，引擎 `Common/JsonLinesStorage.cs` 自上游移植）；保留天数 `RecycleBinRetentionDays` 默认 60 天自动过期。接入点：待办双击删除、✕ 按钮删除两条路径均先入回收站。
- **图片缓存生命周期清理**（`Clipboard/Services/ImageCleanupService.cs`，自上游适配）：三重触发（启动 10 秒后 / 每 12 小时轮询 / 新增图片后）+ 三道工序（保留天数清理 / 容量上限折半淘汰 / 孤立文件清理）。与原有 `PasteMaxItems` 条数上限清理互补。配置：`ClipboardImageRetentionDays`（默认 15 天）、`ClipboardImageMaxStorageMB`（默认 100MB）。适配说明：本仓库 `ClipboardItem` 无 Pinned / 缩略图字段，时间戳为 `Timestamp`，孤立清理仅扫 `images/` 目录。

### Fixed 修复（截图 / 标注）
- **框选窗口"一闪而过" / Esc 失灵**：`ScreenshotWindow` 原先 `Deactivated → Cancel()` 无条件生效，任何焦点竞争（通知弹窗、输入法浮窗、他窗弹窗）都会瞬间取消选区。现加 `_activatedOnce` 门闩，仅成功激活过一次后才允许失焦取消；`Loaded` 末尾补 `Activate()+Focus()`；Esc 由 `KeyDown` 改为 `PreviewKeyDown`。
- **截图热键永久失效（只能重启）**：底图捕获抛异常（锁屏 / 安全桌面 / 显卡切换瞬间）时 `_tcs` 永不完成 → `CaptureModule._busy` 永久 true → Alt+A 死锁。`OnLoaded` 整体 try-catch 失败即取消完成 Task；`Show()/Activate()` 亦包兜底。
- **多屏 / 负坐标副屏截图错位**：物理原点原为 `VirtualScreenLeft(DIP) × 主屏 DPI` 推算，混合 DPI 下算错。改用 `GetSystemMetrics(SM_X/CX/Y/CY_VIRTUALSCREEN)` 物理像素真值，全屏底图尺寸同步修正。
- **大区域截图时编辑器溢出屏幕**：`EditorWindow` 窗口钳制到所在屏工作区（`MaxWidth/MaxHeight`），图像区包 `ScrollViewer` 滚动查看，工具栏始终可达；`ComposeImage` 渲染完整 Visual，保存/复制的分辨率不受滚动视口影响。
- **标注误画**：编辑器画布支持右键取消当前正在进行的绘制（无需 Ctrl+Z）。

### Changed 变更
- `Clipboard/MainWindow.xaml.cs`：三处剪贴板写入接入 FastClipboard；移除失去引用的 `LoadBitmap`。
- `Common/AppPaths.cs`：新增 `RecycleBin` 路径。
- `Common/AppSettings.cs`：新增 `RecycleBinRetentionDays`、`ClipboardImageRetentionDays`、`ClipboardImageMaxStorageMB`。
- `Common/TrayHelper.cs`：菜单新增「待办历史」项 + `TodoHistoryRequested` 回调（App 装配，窗口由 App 惰性创建复用单实例）。

## [2.1.3] — 2025-09-27

- **截图独立模块**（`Capture/`）：`Alt+A` 或托盘「截图」→ 框选 → 自动复制原图到剪贴板 → 打开标注编辑器（矩形 / 箭头 / 画笔 / 马赛克 + 撤销重做 + 复制 / 另存为 PNG）；托盘菜单精简为 5 项。
- **快捷键自定义链路修复**：录制时挂起全局热键；冲突检测覆盖全部热键；注册失败提示；键帽重复标红；键名显示修正（D0 → 数字等）。
- **OCR 质量改进**：深色截图自动反色（`OcrAutoInvertDark`）；`OcrResult.Blocks` 保留原图坐标与版面行号；前后处理参数变更不再触发引擎重建。
- **快捷键全面可自定义**：剪贴板唤出键（默认 Win+V）纳入设置中心；`KeyboardHookService` 重写为通用「修饰键 + 按键」匹配，修掉钩子超时失灵缺陷。
- **OCR 并发改为新触发取消旧触发**（CancellationToken 贯穿排队与界面写入）。
- **新增识别历史**：入库 `clipboard.db` 的 `ocr_history` 表，设置中心可查可复制可清空，上限 `OcrHistoryMaxItems`。
- **快捷键支持清空**：录制中按 Delete，键帽灰色「未设置」；剪贴板唤出键清空后 Win+V 归还系统。
- **待办窗口纳入可配置热键**（默认 Alt+T）。
- **OCR 引擎由 Paddle Inference 换成 ONNX Runtime**（`RapidOcrNet` 4.2.0 + PP-OCRv6 模型随仓库）：**解压后 413MB → 69MB、分发包 168.8MB → 44.3MB**，识别效果不变。csproj 锁 RID `win-x64`，模型目录 `models/`。
- 已发布 GitHub Release v2.1.3。

## [2.1.2] — 2025-09

- 修复国内翻译引擎可达性（默认预填 DeepSeek 端点）；翻译配置异步热更新。
- 单实例保护强化（Mutex + 命名事件唤醒已有实例）。
- 剪贴板图片内容级去重（PNG 像素哈希 `ContentHash`）。
- Todo 完成态持久化（`- [x]` 任务列表格式 + 双击已完成删除）；启动不再自动弹出待办窗口。

## [2.1.1] — 2025-09

- 修复 TodoList 置顶时 OCR 截图闪退：`ScreenshotWindow` 加 `_completing` 防重入标志，调整窗口恢复顺序（先关选区窗再恢复被隐藏窗口）。
- TodoList 穿透定时器加 `IsVisible` 检查，隐藏期间跳过 Win32 调用。

## [2.1.0] — 2025-09

- 快捷键 UI 可配置；剪贴板图片支持 + 内置截图；OCR 模型热切换；API Key DPAPI 加密存储；Pin 钉住；SQLite 常驻连接与 WAL 优化。

## [2.1.0 之前]

- 基于 [li5bo5/WinKit](https://github.com/li5bo5/WinKit) 二次开发（TodoList + 剪贴板历史），沿用 **AGPL-3.0** 开源。维护仓库 [oishijie/WinKit](https://github.com/oishijie/WinKit)。
