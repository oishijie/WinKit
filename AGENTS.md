# AGENTS.md — WinKit

统一桌面助手：TodoList + Clipboard + 截图标注 + 本地 OCR 翻译（常驻系统托盘）。

## 怎么跑起来
- 还原并构建：`dotnet restore` → `dotnet build -c Release`（输出 `bin/Release/net8.0-windows/`）。
- 本地 OCR 依赖 `RapidOcrNet`（ONNX Runtime）；模型随仓库放在 `models/`（PP-OCRv6，约 37MB），**必须先 `dotnet restore`**，否则 `VerifyLocalOcrRuntime` 构建目标报错。
- 无头跑 `dotnet WinKit.dll` 可抓启动期托管异常（GUI 崩溃在桌面上只表现为「打不开」）。

## 技术栈
- net8.0-windows、x64（**ONNX Runtime 原生库仅提供 x64，进程锁定 x64**）、WPF + WinForms 混合。
- 本地 OCR：`RapidOcrNet` 4.2.0（ONNX Runtime 1.29 + SkiaSharp，**已替代原 Paddle Inference / PaddleOCRSharp**），CPU 推理，不联网。
- 翻译：默认**国内大模型（DeepSeek 等 OpenAI 兼容）**，预填 `https://api.deepseek.com/v1` 国内可达端点；可在设置切到通用 OpenAI 兼容 `/chat/completions`、或 Google（需代理）。

## 目录与约定
- `Common/`：`App.xaml(.cs)` 入口、`SettingsManager`（配置中枢，单例持有 `AppSettings` + `SettingsChanged` 事件）、`SettingsWindow`（7 分区设置中心：通用 / 待办 / 剪贴板 / **同步** / 截图 / 翻译与 OCR / 关于，改一项存一项、即时生效）、`TrayHelper`（托盘 + 焦点宿主）、`AppSettings`（持久化模型，普通属性 JSON 序列化）、`AutoStartHelper`、`ShellMenuHelper`（资源管理器右键菜单 HKCU 注册表安装器）、`HotkeyService`（通用全局热键封装，各模块各持一份实例）。
- `Todo/`、`Clipboard/`、`Capture/`、`Translate/`：四大功能模块。
- `Capture/CaptureModule.cs`：截图编排 + 截图热键 `Alt+A`。链路为「框选 →（按 `CaptureAutoCopy` 配置）自动复制原图到系统剪贴板 →（按 `CaptureOpenEditor` 配置）打开标注编辑器」，独立于剪贴板与翻译模块启停。配套 `ScreenshotWindow`（全屏选区，多屏/DPI/隐藏自身窗口）、`EditorWindow`（标注编辑器）、`ScreenshotService`（按矩形抓屏）。Translate 做 OCR 时复用同一个 `ScreenshotWindow`。
- `Translate/TranslateModule.cs`：编排 OCR→翻译四条链路，注册 `Alt+S / Alt+D / Alt+Shift+S / Alt+Shift+F` 热键（`Alt+A` 已移交 Capture）；持 `RapidOcrProvider`、`ITranslator`（Google / OpenAI 兼容实现，按 `TranslateProvider` 分派）。
- 配置项增删：在 `AppSettings` 加属性即可自动持久化；需在设置窗 `LoadAll` 回填 + 对应 handler 写回 + `Save()`。
- 敏感信息（OpenAI API Key）使用 Windows DPAPI 加密存储，同机同用户自动解密，`SettingsManager` 保存时加密、加载时解密 + 明文自动迁移。
- 快捷键支持 UI 录制/保存/热重载：`SettingsWindow` 快捷键列表可点击录制新组合，保存后 `TranslateModule` / `CaptureModule` 各自注销旧键、注册新键。
- **热键元数据单一数据源 `Common/HotkeyCatalog.cs`**：全部可配置热键（显示名 / 归属分区 / 配置读写器）只在这里定义一张表，设置窗渲染、冲突检测、录制、模块注册都从它取。新增一条热键只需往 `All` 数组追加一项（并同步模块的动作字典），不再需要改四处数组 + 两个 switch。
- **录制期间挂起全局热键**：`SettingsWindow.HotkeyRecordingChanged` → `App` 调 `TranslateModule/CaptureModule.SuspendHotkeys()` 注销全部热键，录完 `ResumeHotkeys()` 按最新配置装回。不做这一步的话，用户按下「当前已生效的组合」（如 Alt+S）时按键会被 Windows 直接发给注册者，设置窗收不到键，弹出的截图/翻译窗还会把设置窗顶得失焦、连带取消录制。
- **热键注册失败会提示**：`HotkeyService.Register` 的返回值不再被忽略，`TranslateModule/CaptureModule` 抛出 `HotkeyRegistrationFailed`，`App` 汇总后在录制结束时弹一次提示；设置窗里与其它项重复的键帽也会直接标红。旧版静默失效，用户会以为「改了没用」。
- OCR 模型支持热切换：设置窗口切换模型后 `RapidOcrProvider.ReconfigureAsync` 销毁旧引擎并后台预热新引擎，无需重启应用。前后处理类参数（瘦身秒数、深色反色）走 `SameEngineConfig` 判定，只就地更新、不触发重建。
- **OCR 结果携带原图坐标**：`OcrResult.Blocks`（元素类型 `OcrTextBlock`）给出每个文本框的文字 + **在原图坐标系中的像素包围盒** + 版面行号 `LineIndex`（同一行的碎片共享该值）。之所以需要换算：`RapidOcrProvider.Prepare` 会因小图放大 / 超大图缩小而产生缩放系数，引擎回传的坐标位于缩放后的空间，`OcrTextLayout.Compose` 按系数除回去并收敛到边界内。下游拿到的即是可以直接在截图上作画的坐标 —— 这是「原文译文逐行对照高亮 / 把译文叠回截图 / 按块单独复制 / 带坐标导出」的共同地基。
- **深色截图自动反色**：`RapidOcrProvider.IsDarkBackground` 把图缩到长边 32 的缩略图后**只取外圈**采样（中央是内容、四周边带才代表底色），平均亮度低于阈值即判定深色，整图反色后再送识别 —— PP-OCR 识别头按「白底黑字」训练，浅字深底会明显掉字。开关 `OcrAutoInvertDark`，识别成功时状态栏追加「已反色」。
- **热键有两种注册机制，别混用**（`HotkeyMechanism`）：普通组合（Alt+S 等）走 `HotkeyService.RegisterHotKey`；**含 Win 键的组合走低级键盘钩子** `Clipboard/Services/KeyboardHookService`（默认的剪贴板唤出键 Win+V 就是这类）。含 Win 的组合被系统保留，`RegisterHotKey` 注册必然失败；钩子还能顺带**吞掉按键**，从而阻止系统自带剪贴板面板弹出。改键时两种机制都要照顾到。
- **热键按归属分三类，新增时先想清楚归哪类**（`HotkeyCatalog` 是唯一数据源，新增一条只需改 catalog —— UI、冲突检测、录制、注册全部自动跟上）：
  ① **模块热键**（`HotkeyOwner.Translate` / `Capture`）—— 各模块持有自己的 `HotkeyService`，按 `descriptor.Owner` 过滤后注册；
  ② **钩子热键**（`HotkeyOwner.Clipboard`）—— `App.ApplyClipboardHotkey` 走 `KeyboardHookService`；
  ③ **顶层热键**（`HotkeyOwner.Todo` 等没有独立模块类的）—— `App.ApplyTopLevelHotkeys` 的兜底套路：凡「未被模块认领且走 `RegisterHotKey`」的条目都由它接管，所以**不会出现「加了热键但没人注册」的漏洞**。新增顶层热键 = catalog 加一项 + `App.ResolveTopLevelAction` 接一个动作。它用**配置签名字符串**判断是否需要重装，且因 `HotkeyService` 没有单条注销 API，每次重装都换新实例。
  三条路径**都必须**在录制期间注销（否则按键被系统直接发给注册者，弹出的窗口会顶掉设置窗焦点、当场中断录制），录完按最新配置装回。
- **快捷键可以留空，且留空是一等状态**：VK == 0 表示「用户主动清空」，全链路都已适配 —— `HotkeyService.Register` 对 VK=0 直接返回 true（否则上层的 `HotkeyRegistrationFailed` 会把「用户留空」误报成注册失败）、`FindDuplicate` 跳过空值、`KeyName(0)` 显示「未设置」、`KeyboardHookService.SetBindings` 过滤 VK=0 且空集合自动卸钩（清空剪贴板唤出键 = Win+V 归还系统）。录制时按 **Delete / Backspace** 清空，键帽转灰色「未设置」；注意 `HotkeyDescriptor.Get` 对 null 会回落成 VK=0 而不是默认值，所以**新增热键必须给属性初始化器写默认组合**，否则用户升级后会拿到一个空热键。
- **剪贴板同步模块（Unreleased）**：`Clipboard/Services/SyncEngine.cs`（约 500 行）+ `Common/ShellMenuHelper.cs`，兼容 [SyncClipboard](https://github.com/Jeric-X/SyncClipboard) 官方协议，服务器端用 [SyncClipboardCfServer](https://github.com/Leexunhuan743/SyncClipboardCfServer)（CF Workers + D1/R2/DO，48MiB 上限）。改同步相关代码前必读以下铁律：
  - **协议 hash 是两套算法，别混**：文本 = `SHA256(UTF8)→大写hex`（`Convert.ToHexString`）；**图片/文件的 `hash` 不是内容哈希**，是 `SHA256("文件名|" + 大写hex(内容SHA256))`，内容哈希放 `transferDataHash` 字段。整段照官方 `ProfileDto.cs` + `docs/Hash.md` 逐字对齐——算错任何一步，手机端官方客户端直接解析失败。图片远端命名必须 `Image_时间戳.png`。
  - **上传必须两步**：先 `PUT file/{dataName}` 传数据，再 `PUT SyncClipboard.json` 换指针。反过来或只传指针会产生悬空引用。
  - **防回环双保险，缺一不可**：① `_lastPushedHash`/`_lastPulledHash` 双标记，拉取后两者同步更新；② 图片拉取写回剪贴板后经 `SetImage` 重新编码，字节必变、hash 守不住，**必须用历史条目 Id 守门**（拉取入库拿到 Id 后记下，推送循环里跳过该条目）。
  - **推送时机分级（用户拍板）**：只有文本是「复制即推」；图片/文件默认**仅入历史不上传**（`SyncAutoPushImage/SyncAutoPushFile` 默认 false）。显式推送（右键 `/clipsend`）优先级最高：无视总开关、类型开关、黑名单，仅大小上限仍拦——用户主动点的就是要传。
  - **显式推送用独立临时 `SyncEngine`**（`App.PushFileNow` 路径）：托盘「剪贴板同步」总开关关着也必须能右键推，因此不复用常驻引擎实例；OnExit 两处都要 Dispose。
  - **`/clipsend` 参数通道顺序**：`OnStartup` 里必须在**单实例 Mutex 判定之前**解析 `args`——后启动的临时进程拿不到 Mutex，只写 `clipsend.txt` 临时文件 + `SignalExistingInstance()` 唤醒主实例即退出；主实例 `WatchShowEvent` 里读临时文件区分「纯唤醒（弹待办窗）」和「传文件（入历史 + PushFileNow，不弹窗）」两种信号语义。
  - **右键菜单注册表走 HKCU**（`ShellMenuHelper`）：无需管理员、零 shell DLL；`App.OnStartup` 每次自动刷新 exe 绝对路径（exe 挪位置不失效）。菜单挂在 `Directory\shell`（单文件）；多选合并走临时文件。
  - **轮询环线程模型**：`HttpClient` 全程 `ConfigureAwait(false)` 后台线程；碰 UI（`Clipboard.SetText/SetImage`、`ClipboardManager` 增删）必须 `Dispatcher.InvokeAsync` 切回 STA。网络异常一律静默吞掉下轮重试，不弹窗不崩。
  - **连接参数热重启**：设置页改 URL/账号/间隔后调 `App.RestartSyncEngine()`（Dispose 旧的 + `Start()` 新的），不要求重启应用。改 `AppSettings` 的 `Sync*` 字段记得同步在 `SettingsWindow.LoadAll` 回填 + handler 写回。
  - **设置分区索引**：SettingsWindow 现在 7 分区，「同步」插在索引 3（剪贴板之后、截图之前）——`ShowSection` 的 Visibility 映射、`NavList.SelectedIndex` switch、`RefreshOcrHistory`/`RefreshOcrStatus` 的触发索引都已整体后移，加新分区时从这。
  - **Group 协议（多文件组）同样逐字对齐官方 `GroupProfile.cs`**：打包 = zip 根目录平铺条目（`_archive/` 前缀目录排除）；条目清单 hash = 条目按**文件名字节序**（`ByteArrayComparer`，不是字符串比较序）排序后，`D|名\0` / `F|名|长度|内容hash大写hex\0` 逐行 `IncrementalHash(SHA256)`；远端命名必须 `File_时间戳.zip`；解包先整体校验清单 hash 再写盘，文件落 `sync/<ziphash>/`。右键多选入口在 `App.ProcessClipSendFiles`：1 个文件走 `PushFileNow`，≥2 个走 `PushGroupNow`。
  - **发版三件套约定**：发新版本时 ① GitHub/Gitee 同步发 Release 并传 `WinKit-v版本-win-x64.zip`；② 更新仓库根 `latest.json`（CNB 更新清单，raw 匿名可读——CNB release API 匿名 401，故用它做版本探测，`downloadUrl` 指向 Gitee 国内最快）；③ Gitee Release 别落后（自动更新选源**取版本最高的成功源**，某源落后不会误判「已是最新」，但用户会绕开慢源）。
- **低级键盘钩子的三条铁律**（`KeyboardHookService` 类注释里有完整说明，这里列要点）：① 回调必须极快返回 —— Windows 的 `LowLevelHooksTimeout`（默认 300ms）超时会**静默移除钩子且不通知**，此后热键彻底失效直到重装，因此回调内只做「判定 + 投递」，窗口操作一律 `Dispatcher.BeginInvoke` 异步做；② 回调内异常绝不能逃逸（异常穿过 P/Invoke 边界同样会移除钩子）；③ 修饰键状态由钩子自维护，不在回调里现查 `GetAsyncKeyState`。装钩/卸钩会写日志到 `%APPDATA%/WinKit/hotkey.log`。
- **钩子型热键的录制通道**：含 Win 的组合到不了 WPF 键盘事件，只有钩子看得见。因此录制期间**钩子不挂起**（与 `HotkeyService` 那批相反），而是由 `App.OnHookHotkey` 判断 `SettingsWindow.IsRecordingHotkey`，把 `(VK, Modifiers)` 转给 `SettingsWindow.FeedHotkeyInput` 提交。若把钩子也挂起，按 Win+V 会落到系统手里、弹出系统剪贴板并顶掉设置窗焦点。
- **OCR 并发语义：新触发取消旧触发**（不是忽略）：`TranslateModule.RunAsync` 持一个 `CancellationTokenSource`，新请求进来先 `Cancel` 旧的，token 一路传到 `OcrAsync` → `IOcrProvider.RecognizeAsync`。注意原生推理不可中断，「取消」的生效点在于**排队等信号量那一段**，以及**每一步界面写入前的 token 检查**（长识别跑完后结果会被丢弃，不会覆盖界面）。`CaptureModule` 保持「忽略并发」—— 那边是要避免开出多个全屏选区窗口。
- **识别历史**：`Translate/Services/OcrHistoryStore.cs` 复用剪贴板同一个库（`%APPDATA%/WinKit/clipboard.db`）的 `ocr_history` 表，另开一条自己的连接并设 `busy_timeout`（WAL 下与 `ClipboardManager` 的常驻连接并存）。`TranslateModule.OcrAsync` 在识别成功后统一记账，三条链路（截图翻译 / 文字识别 / 静默 OCR）自动全覆盖。上限由 `OcrHistoryMaxItems` 控制，UI 在设置中心「翻译与 OCR → 识别历史」。写库失败只记日志，不影响识别主流程。
- **⛔ 发布体积约束（v2.1.3 起，动依赖前先读这段）**：OCR 引擎已由 Paddle Inference 换成 **ONNX Runtime**，解压后 **413MB → 69MB**、分发包 zip **168.8MB → 44.3MB**。四条不能破的规则：
  ① **必须锁 RID**：`<RuntimeIdentifier>win-x64</RuntimeIdentifier>` + `<AppendRuntimeIdentifierToOutputPath>false</AppendRuntimeIdentifierToOutputPath>`。不加时 NuGet 会把 OnnxRuntime / SkiaSharp 的**全平台**原生资产平铺进 `runtimes\`（Android `.aar`、iOS `.xcframework.zip`、二十余个 Linux 的 `.so`）—— **实测 378MB**；加 RID 后该目录整个消失、原生库平铺到根目录。⚠️ 改 RID 后必须清 `bin/Release` + `obj/Release`，否则旧的全平台产物不会被自动删除。
  ② **模型放在 `models/`**（随仓库提交，由 csproj 复制规则带进输出），档位只有 **Small / Tiny** 两档 —— PP-OCRv6 单模型即多语言（简繁中文 / 英文 / 日文 + 46 种拉丁语系），**不要再拆「中英文 / 英文专用」**。
  ③ **`PaddleOCRSharp` 与 `Paddle.Runtime.win_x64` 已彻底移除**：那套 `libs/` 的五个大 DLL 互相咬死（`mklml` 88M ← `paddle_inference`、`paddle_yt_phi` 44M ← `paddle_inference`…），**在 Paddle 框架内最多只能压到 350MB**，别回头再试。
  ④ **`OcrTextLayout` 不感知具体引擎**：它只吃项目自有矩形块（`OcrTextBlock`），已解除对 `PaddleOCRSharp.TextBlock` 的依赖 —— 换引擎只需改 `Translate/Services/` 下的 Provider，版面层与下游一律不动。
  引擎特有坑（`RapidOcrProvider`）：`RapidOcrOptions.Default` **不能配 v6**（其 1024 长边上限 + 50px 白边是为 v5 调的，会让 v6 检测器分辨率不足），必须用 `PPOCRv6` 预设；`TextBlocks` / `BoxPoints` 是**数组**，用 `.Length`（写 `.Count` 会被解析成 LINQ 扩展方法，报 `CS0019`）；`RapidOcr` 只接受 `SKBitmap`，`Bitmap → SKBitmap` 走逐行 `Marshal.Copy`，因此 `Prepare` 输出为 **32bpp**（`Bgra8888` + `Opaque` 是唯一无需二次转换的匹配格式）。

## 当前状态与下一步
- v2.1.0：快捷键 UI 可配置、剪贴板图片支持 + 截图功能、OCR 模型热切换、DPAPI 加密、Pin 钉住、SQLite 连接优化、**单实例保护（Mutex + 唤醒已有实例）**、**剪贴板图片内容级去重（像素哈希）**、**Todo 完成态持久化（`- [x]` 任务列表格式 + 双击已完成删除）**。
- v2.1.3：**截图抽成独立的 `Capture/` 模块** —— 热键 `Alt+A` 或托盘「截图」框选后，自动把原图复制到系统剪贴板并打开标注编辑器（矩形 / 箭头 / 画笔 / 马赛克 + 撤销重做 + 复制 / 另存为 PNG），截图不再受剪贴板或翻译模块启停影响；**托盘菜单精简为 5 项**（截图 / 显示待办 / 剪贴板历史 / 设置 / 退出），低频开关全部收敛到设置中心；剪贴板窗口标题栏的截图按钮已移除；**快捷键自定义链路修复**（录制时挂起全局热键、冲突检测覆盖全部热键、注册失败提示、键帽重复标红、键名显示修正）；**OCR 质量改进第一轮** —— 深色截图自动反色（可开关）、`OcrResult.Blocks` 保留原图坐标与版面行号、前后处理参数变更不再触发引擎重建；**快捷键全面可自定义** —— 剪贴板的唤出键（默认 Win+V）纳入设置中心可改，`KeyboardHookService` 重写为通用「修饰键 + 按键」匹配并修掉「有概率失灵」的钩子超时缺陷；**OCR 并发改为新触发取消旧触发**；**新增识别历史**（入库 + 设置中心可查可复制可清空）；**快捷键支持清空**（录制中按 Delete，键帽转为灰色「未设置」，该功能不再占用全局组合，剪贴板唤出键清空后 Win+V 归还系统）；**待办窗口纳入可配置热键**（默认 Alt+T，与托盘「显示待办」共用同一实现）；**OCR 引擎由 Paddle Inference 换成 ONNX Runtime**（`RapidOcrNet`，模型 PP-OCRv6 随仓库放 `models/`，档位精简为 Small / Tiny 两档），**解压后 413MB → 69MB、分发包 168.8MB → 44.3MB**，识别效果不变。**已发布 GitHub Release v2.1.3**（非 draft）。
- 维护仓库 oishijie/WinKit；原项目 li5bo5/WinKit。
- 待办：DeepL / Azure 翻译接入（接口已预留，按需实现）。
- v2.2.0（2026-10-27，已构建验证，**已发布 GitHub Release v2.2.0**——非 draft，资产 `WinKit-v2.2.0-win-x64.zip` 44.3MB；github remote 已切 SSH over 443，HTTPS 走本地代理对 github.com 返回 502）：**自上游 v2.11 移植三件** —— ① `FastClipboard`（Win32 直写剪贴板，文本回填失败回退 WPF，图片走独立 STA 线程，回填写图失败即中止）；② **待办回收站**（`Todo/HistoryWindow` + `RecycleBinService`，存储 `%APPDATA%/WinKit/recyclebin.jsonl` 走 `JsonLinesStorage`，托盘菜单新增「待办历史」项由 `TrayHelper.TodoHistoryRequested` 回调、App 惰性创建复用单实例；**`MarkdownStorage` 保持不动**，回收站独立存储）；③ **图片缓存生命周期清理**（`ImageCleanupService`：启动 10s / 12h 轮询 / 新增图片三重触发，保留天数 + 容量折半 + 孤立文件清理，配置 `ClipboardImageRetentionDays=15` / `ClipboardImageMaxStorageMB=100`；移除回调 `RemoveItemsBatch` 必须经 `Dispatcher.Invoke` 回 UI 线程再动 `ObservableCollection`）。**截图标注四修** —— ① `ScreenshotWindow` 加 `_activatedOnce` 门闩（未激活成功前失焦不取消，否则通知/输入法浮窗抢焦点会让框选"一闪而过"），Esc 改 `PreviewKeyDown`；② `OnLoaded` 底图捕获与 `Show/Activate` 全部 try-catch 兜底完成 `_tcs`（否则异常 → `CaptureModule._busy` 永久 true → Alt+A 死到重启）；③ 虚拟屏物理原点/尺寸改用 `GetSystemMetrics(SM_X/CX/Y/CY_VIRTUALSCREEN)`（原 `VirtualScreenLeft(DIP)×主屏DPI` 在负坐标副屏/混合 DPI 下算错）；④ `EditorWindow` 窗口钳制工作区 + 图像区包 `ScrollViewer`（`ComposeImage` 渲染完整 Visual，输出分辨率不受滚动影响），画布支持右键取消当前绘制。**上游移植铁律**：上游 `TodoService/JsonLinesStorage 的 Todo 存储`、`上游 KeyboardHookService 整体`、`上游 HotkeySettingsWindow 整体` 三样**不许整体引入**（会毁掉 MarkdownStorage 多端同步、0xFF 防开始菜单机制、与现有 SettingsWindow 冲突）；`JsonLinesStorage` 只作通用存储引擎复用。
- Unreleased（2026-10-29，已构建验证 0 警告 0 错误，未发版）：**剪贴板同步模块** —— 兼容 SyncClipboard 官方协议（服务器端实测 SyncClipboardCfServer / CF Workers）。文本/图片/文件推拉（`SyncEngine.cs` 约 500 行，协议 hash 与两步上传逐字对齐官方源码）；**推送时机分级**（文本复制即推；图片/文件默认仅入历史，显式通道才传）；**资源管理器右键「同步到手机」**（HKCU 注册表 + `/clipsend` 临时进程参数通道，推送专用临时引擎不受总开关限制）；设置中心 6 → 7 分区新增「同步」（连接/同步内容/推送时机/集成/限制/通知，连接参数热重启）。开发期坑：WPF+WinForms 混合下 `Application`/`Clipboard` 歧义（CS0104）+ 项目命名空间 `WinKit.Clipboard` 遮蔽 `System.Windows.Clipboard`（CS0234），同步引擎内一律完全限定名。详细铁律见上文「剪贴板同步模块」段。

## 文档维护约定（⭐ 每次实质性改动后必做）
- **`CHANGELOG.md`**：按 Keep a Changelog 格式记录；未发布改动写 `[Unreleased]` 段，发版时改为版本号并同步 `csproj` 的 `<Version>`。新增 Added / Fixed / Changed，附根因与关键适配说明（「为什么这么改」比「改了什么」重要）。
- **`ROADMAP.md`**：路线图与优先级；做完的挪进 CHANGELOG，新计划写进对应时段，决策变更（尤其「不做」清单）当场改写。
- **`AGENTS.md`（本文件）**：架构约定与踩坑铁律的唯一权威来源；新增机制、新坑、新约束，先写这里再写代码注释。
- 三份文档 + 代码同一次提交交付；改了行为不改文档 = 改动未完成。
