# CHANGELOG — WinKit

统一桌面助手：TodoList + Clipboard + 截图标注 + 本地 OCR 翻译。
格式参照 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)；版本号见 `WinKit.csproj` 的 `<Version>`。

## [Unreleased]

## [2.3.3] — 2026-10-29

### Fixed 修复
- **操作卡片上的按钮不显示**（2.3.2 引入）。`ToastWindow.xaml` 里按钮行 `ActionsRow` 默认 `Visibility="Collapsed"`，`RenderContent` 只在「推送」分支显式设回 `Collapsed` 后 return，**「拉取」分支仅往 `ButtonPanel` 里 `AddButton`，从未把 `ActionsRow` 改为 `Visible`**。结果是按钮对象全部创建、也挂进了控件树，但父容器保持折叠，第二行高度为 0——卡片看起来只有图标、标题和副标题。
  修法：`RenderContent` 末尾按 `ButtonPanel.Children.Count > 0` 判断后显式展开 `ActionsRow`。用「是否真的加到了按钮」而非无条件展开，避免将来某类内容不该有按钮时留出一条空白行。
- 记录一条经验：这类缺陷**编译期完全看不出来**（0 错误 0 警告），静态交叉核对控件名与事件签名也查不到，只有真正跑起来看界面才会暴露。后续新增卡片类型时，务必确认按钮之外的容器可见性也被打开。

### Changed 变更
- 版本号 2.3.2 → 2.3.3。

## [2.3.2] — 2026-10-29

### Added 新增
- **右下角操作卡片**：手机把文字 / 图片 / 文件传到电脑时，右下角弹出可交互卡片替代原来的系统托盘气泡。按钮按内容类型区分——文字给「复制 / 看历史」，图片给「复制 / 存到下载」，单个文件给「打开 / 所在文件夹 / 存到下载」，多文件组给「打开文件夹 / 存到下载」。
  - 「存到下载」读注册表 `{374DE290-123F-4565-9164-39C4925E467B}` 取真实「下载」路径而非拼 `UserProfile\Downloads`，兼容 OneDrive 接管过的情形；重名自动加 `(1)` 后缀，存完资源管理器定位选中。
  - 多条通知自底部向上堆叠，最多同屏 3 张，超出时挤掉最旧的一张。鼠标悬停暂停倒计时，移开续走。
  - 全屏独占（游戏、PPT 放映）、锁屏、勿扰时段由 `SHQueryUserNotificationState` 判定，非 `QUNS_ACCEPTS_NOTIFICATIONS` 一律静默跳过，内容照常写入剪贴板。
  - **只在「收到」时弹卡片**，推送成功仍走原托盘气泡——避免每复制一段文字就弹一张卡。
- **README 新增「📲 手机 ⇄ 电脑 互传指南」**：一次性配置、手机→电脑、电脑→手机、限制速查、排错表五段，逐条对齐代码实际行为（含「接收文件」默认关闭、图片默认不自动推送等易坑点）。

### Changed 变更
- `SyncEngine.SyncSucceeded` 事件签名由 `Action<string>` 改为 `Action<SyncToastPayload>`，携带类型、标题、副标题、文本、文件路径、缩略图与推拉方向。`SyncSkipped`（超上限 / 黑名单拦截提示）保持字符串走托盘气泡。
- 设置中心 → 同步 → 通知，新增「右下角操作卡片」开关（`SyncToastEnabled`，默认开）与「卡片停留时长」（`SyncToastSeconds`，默认 6 秒，范围 3–30）。关掉卡片即退回原托盘气泡，功能不丢、只是没有按钮。
- 版本号 2.3.1 → 2.3.2。

## [2.3.1] — 2026-10-29

### Added 新增
- **「启动时显示待办」开关**（设置中心 → 待办 → 窗口行为）：开启后程序启动即把待办面板摆到屏幕右上角；默认关闭，维持「仅驻留托盘」的原有行为。面板与「鼠标穿透」同时开启时可见但不可点击，该组合由用户自行取舍。

### Fixed 修复
- **剪贴板同步密码以明文落盘**。根因：`SettingsManager` 的 DPAPI 加密硬编码单字段——`SaveSettings` 里写死 `prop.Name == "OpenAIApiKey"`，`LoadSettings` 也只解这一个；同步模块是后加的，`SyncPassword` 没回填白名单，导致 `%APPDATA%\WinKit\settings.json` 中同步密码明文可读。
  改法：把加密字段收成 `SensitiveFields` 白名单（`OpenAIApiKey` + `SyncPassword`），保存与加载共用同一份清单；单字段解密逻辑抽为 `TryDecryptField`，对两字段各执行一次，任一处判定为旧版明文即触发后台整体重加密。**旧配置无需手动处理**——启动时自动迁移，密码值不变。以后新增敏感配置只需改白名单一处，不会再出现「存时加密、读时漏了」的错位。

### Changed 变更
- 版本号 2.3.0 → 2.3.1。

## [2.3.0] — 2026-10-29

### Added 新增（剪贴板同步模块，兼容 SyncClipboard 官方协议）
- **同步引擎**（`Clipboard/Services/SyncEngine.cs`，约 500 行）：对接 [SyncClipboard](https://github.com/Jeric-X/SyncClipboard) 官方协议（`GET/PUT SyncClipboard.json` + `file/*` 端点 + HTTP Basic Auth），服务器端实测兼容 [SyncClipboardCfServer](https://github.com/Leexunhuan743/SyncClipboardCfServer)（Cloudflare Workers 复刻，D1 + R2 + Durable Objects）。文本 / 图片 / 文件三类推拉：
  - **协议逐字对齐官方源码**（`ProfileDto.cs` + `ImageProfile/FileProfile` + `docs/Hash.md` 三方交叉验证）：文本 hash = `SHA256(UTF8)→大写hex`；**图片/文件的 `hash` 不是内容哈希**，而是 `SHA256("文件名|" + 大写hex(内容SHA256))`，内容哈希单独放 `transferDataHash`；图片远端命名对齐官方 `Image_时间戳.png` 格式——手机端官方客户端可直接混用。
  - **两步上传**：先 `PUT file/xxx` 传数据，再 `PUT SyncClipboard.json` 换指针，中断不产生悬空引用。文件走流式上传（大文件不整读进内存），下载后校验 `transferDataHash`，损坏即弃。
  - **防回环双保险**：`_lastPushedHash`/`_lastPulledHash` 双标记（拉取后两者同步更新）；图片因 `SetImage` 重编码后字节必变、hash 守不住，改用**历史条目 Id** 守门。
  - **轮询 + Nudge**：默认 5 秒轮询（2 台设备约 3.5 万请求/天，CF 免费额度 10 万/天内）；`Nudge()` 允许显式推送后立即触发一轮，不等轮询。
  - 全部网络异常静默跳过、下轮重试；UI 线程操作（`Clipboard.SetText/SetImage`、历史入库）经 `Dispatcher.InvokeAsync` 切回 STA。
- **推送时机分级（用户决策：复制即推只保留文本）**：文本复制即推（默认开）；**图片/文件复制仅入历史不上传**（`SyncAutoPushImage` / `SyncAutoPushFile` 默认关，设置页可开回）。显式推送（右键）无视一切开关与黑名单，仅大小上限仍拦——用户主动点的就是要传。
- **资源管理器右键菜单**（`Common/ShellMenuHelper.cs`）：`HKCU\...\Directory\shell\WinKitSync` 注册表方案（无需管理员、零 shell DLL），菜单名「同步到手机（WinKit）」。点击后 `WinKit.exe /clipsend` 临时进程把文件路径写入 `clipsend.txt` 临时文件 + 唤醒主实例即退出；主实例复用单实例唤醒事件通道，在 `WatchShowEvent` 中区分「唤醒」与「传文件」（后者不弹待办窗）。**启动时自动刷新注册表中的 exe 绝对路径**，exe 挪位置不失效。
- **显式推送通道**：`SyncEngine.PushFileNow()` 创建**推送专用临时 SyncEngine**（不依赖总开关——「剪贴板同步」关着也能右键推）；多选文件一期只推第一个并气泡提示（官方 Group/zip 属二期）；右键图片文件（jpg/png 等）按 Image 类型推，手机端直接进剪贴板显示而非当文件保存。
- **剪贴板文件支持**：`ClipboardItem` +`File` 类型（`FilePath` 字段，`DisplayText` 显示 `📄 文件名`，历史窗口数据模板绑定自动适配零改动）；`ClipboardManager.AddFileItem()` 入库；`ClipboardService` +`FileChanged` 事件（`FileDropList` 监听，与文本/图片互斥判定同轮询环）；`AppPaths` +`Sync` 下载目录（`%APPDATA%/WinKit/sync/`）。
- **设置中心新增「同步」分区**（6 分区 → 7 分区，插在「剪贴板」后）：连接（服务器/用户名/密码/轮询间隔，**连接参数变更热重启引擎**，不再手改 json）、同步内容（同步文本 / 接收图片 / 接收文件——后两个含义收窄为**仅拉取方向**）、推送时机（图片/文件自动推送，默认全关）、集成（右键菜单开关）、限制（图片 20MB / 文件 48MB 上限——对齐 CF Worker 48MiB，文件扩展名黑名单）、通知（推送/拉取气泡各自开关，走 `NotifyIcon.ShowBalloonTip` 2 秒自动消失）。
- 托盘菜单新增顶级「剪贴板同步」勾选开关（总开关：关 = 完全不连服务器，右键推送也不可用）。

### Changed 变更
- **推送语义修正**：自动推送只看 `Items[0]`（当前剪贴板最新项），废弃旧实现「翻历史找最近文本」（会误推旧内容），与官方单槽语义一致。
- `Common/AppSettings.cs`：+14 个 `Sync*` 配置字段（总开关 / 连接三件套 / 轮询间隔 / 类型开关 / 自动推送 / 上限 / 黑名单 / 通知）。
- `Common/SettingsWindow.xaml(.cs)`：导航与分区索引整体后移；同步页走现有 `RowLabel`/`RowHint` 分组样式。
- `App.xaml.cs`：`/clipsend` 参数解析（必须在单实例判定**之前**读出参数再决定信号语义）、`StartSyncEngine`/`ToggleSyncFeature`/`RestartSyncEngine`、OnExit 释放两处引擎（常驻 + 临时）。

### 开发期踩坑（记录进 AGENTS.md）
- WPF + WinForms 混合下 `Application`/`Clipboard` 命名空间歧义（CS0104），且项目自身命名空间 `WinKit.Clipboard` 会**遮蔽** `System.Windows.Clipboard`（CS0234）——同步引擎内一律用完全限定名。
- 构建报 MSB3027 文件锁（WinKit 正在运行）→ 结束进程重建，数据全落盘无损失。

### Added 新增（同步二期 + ROADMAP 清尾，2026-10-29）
- **Group 多文件同步**（对齐官方 `GroupProfile.cs` 逐字节实现）：右键多选文件自动打包 `File_时间戳.zip` 推送（条目清单 hash = UTF-8 字节序排序后 `D|名` / `F|名|长度|内容hash\0` 逐条 SHA256，对齐官方 `CalculateEntriesHashAsync`）；远端 Group 拉取自动解包、逐条校验 hash 后写回剪贴板 `FileDropList`，文件落 `sync/<hash>/` 目录。
- **拉取图片质量优化**：新增 `ClipboardManager.AddImageItem(byte[], ext)` 原始字节入库（保留 jpg/png 原格式，消除 BitmapSource→PNG 二次编码损失与字节漂移）；拉取图片写回剪贴板改为**双格式**（位图 + FileDropList 原文件，可粘贴出原始文件）。
- **同步状态可视化**：设置页「同步」页顶部状态行（运行中/已停用/连续失败 N 次），含上次成功时间与最近错误；`SyncEngine` 暴露 `LastSuccessAt`/`LastError`/`ConsecutiveFailures`。
- **失败退避重试**：连续失败按 2^N 拉长轮询间隔（上限 8 倍基础间隔），成功后立即回落；`Nudge()` 不受退避影响。
- **软件自动更新**（`Common/UpdateService.cs`）：版本探测走 **Gitee API + GitHub 302 重定向**两路并发（1.5s 超时），**取版本最高的成功源**；下载前再对 **Gitee / GitHub / CNB 三镜像**发 Range 请求测 TTFB，**选最快可用的镜像下载**。「自动检查更新」（默认开，每日一次）与「自动下载更新包」（默认关）双开关分离；下载完成后气泡提供「打开」跳转资源管理器定位安装包。设置页「通用」分区新增「软件更新」卡片（当前版本 / 上次检查时间 / 立即检查）。
- **回收站收尾**：设置「待办」分区暴露「回收站保留天数」（1-365）；回收站窗口支持 Ctrl/Shift 多选批量还原 / 删除（按钮带选中计数）。
- **图片缓存可视化**：设置「剪贴板」分区新增「图片缓存」卡片——保留天数 / 容量上限（10-1024 MB）可配，当前占用实时统计 + 「打开文件夹」入口。
- README 新增「剪贴板同步」章节并关联 [SyncClipboardCfServer](https://github.com/oishijie/SyncClipboardCfServer) 服务器仓库。
- 仓库根新增 `latest.json`（CNB 更新清单，发版时随版本更新）。
- 版本号 2.2.0 → 2.3.0。

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
