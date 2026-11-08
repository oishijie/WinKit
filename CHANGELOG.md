# CHANGELOG — WinKit

统一桌面助手：TodoList + Clipboard + 截图标注 + 本地 OCR 翻译。
格式参照 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)；版本号见 `WinKit.csproj` 的 `<Version>`。

## [2.4.2] — 2026-11-08

### Removed 移除
- **晾衣架（Rack）整体移除**：删除 `Rack/` 全部源码与素材、托盘入口、截图编辑器「挂衣架」按钮、设置页「晾衣架」分区及 14 项设置、`AppPaths` / `AppSettings` 中的相关字段与 `csproj` 的 `Content` 规则。历史条目保留在下方 2.4.1 段落中，仅作记录，功能已不存在；重做计划与失败复盘已记入 `ROADMAP.md` 中期段。设置中心由 8 个分区回到 7 个，产物去掉约 1.83MB 主题素材。

## [2.4.1] — 2026-11-07

### Fixed 修复
- **【协议】剪贴板同步对齐官方 3.3.0，修掉一处会损坏数据的缺口（2026-11-08 追加）**：服务端升级到 3.3.0-beta1 后回查 `SyncClipboard-master/src/SyncClipboard.Shared/Profiles/TextProfile.cs` 与 `SyncClipboardCfServer-master/src/{hash,profile,profileWrite}.ts`，确认 `SyncClipboardProfile` 7 字段、Text / File / Image / Group 四套 hash 公式、`transferDataHash × hasData` 组合、Basic Auth、拉取侧内容校验**本来就是对的**，升级不需要改这些；真正的问题是两处一直存在、被新版严格校验暴露出来的协议缺口：
  ① **大文本会被静默截断，并可能污染其它设备**：`TextProfile.cs:10,25-30` 对 >10240 字符的文本把 `text` 截成前 10240 字符前缀、全文放外置 `.txt`（`hasData=true`），而 `PullTextAsync` 只读 `profile.Text`、完全忽略 `HasData/DataName` → 残缺前缀进剪贴板；更糟的是防回环标记用截断文本算 hash、`_lastRemoteHash` 记的却是全文 hash，两者永远对不上，既不自愈，还可能把残缺文本推回去覆盖全网。现改为：`HasData=true` 时 `GET file/{dataName}` 取全文，校验 `SHA256(内容字节)` 同时匹配 `transferDataHash` 与 `hash`（官方二者都等于全文 hash），再用全文 hash 统一防回环；推送侧按同一 10240 阈值拆外置 `Text_{时间戳}.txt`，推拉不再不对称，也避开长文本全塞 JSON 的 48MiB 请求体上限。
  ② **右键选文件夹推不动，放行后又会 100% 被服务端拒**：四处 `File.Exists`（`SyncEngine.PushGroupNow`、`App.ParseClipSendArgs` / `TakeClipSendFiles` / `ProcessClipSendFiles`）把目录静默丢弃；即便放行，`BuildGroupZipAsync` 只写一条顶层 `dirName/` 目录条目，而官方 `GroupProfile.cs:513-520` 与服务端 `hash.ts:252-259` 都会**逐级推导每一层父目录**计入 entries hash → 含子目录时两端条目集不同，`PUT /SyncClipboard.json` 必返 400 `Hash is not match data.`。现改为逐级写显式目录条目、目录大小按递归文件累计（旧写法 `FileInfo(目录).Length` 直接抛异常导致整次推送静默失败）、单个目录不降级成 File 类型。
  - 同批修掉的既有 bug：`File.OpenRead(sub)` 从不 Dispose（大文件夹漏句柄）、`PullGroupAsync` 把 zip **条目名**当磁盘路径去做 `File.Exists`（拉下来的文件一个都不入历史）、null 字段被序列化成 `"transferDataHash":null`（wire 形状与官方 `ProfileDto.cs:14-17` 不一致，现加 `WhenWritingNull`）。
- **【协议】401/403 不再用 5 秒轮询硬撞新版服务端的 IP 封锁**：服务端 `rateLimit.ts` 在 15 分钟窗口内累计 10 次认证失败会锁 IP 15 分钟，密码 / URL 配错时旧轮询几十秒就能撞满。现在任一步撞到 401/403 就登记 `_authBlockedUntil`，主循环静默等到窗口过去（状态栏给出原因），用户显式 `Nudge()` 或改配置触发 `RestartSyncEngine()` 可提前解除。另在 `PUT SyncClipboard.json` **失败时** `DELETE /file/{dataName}` 清理本次上传的暂存对象 —— 服务端只在成功分支 `deleteTemp`（`profileWrite.ts:193`），「PUT 400」(:187-200) 会留孤儿；**成功时绝不删**：CF 服务端该路由只删暂存（`webdav.ts:205-213`），但官方 Server / WebDAV 的 `/file/{name}` 可能已指向持久文件，成功后删会把刚推上去的数据一起删掉。宁可留孤儿，不可破坏数据。
- **构建：本地参考源码副本不再卷进 WPF 编译**：放进项目根目录的 `SyncClipboard-master/` 含官方 WinUI3 `App.xaml` / `Views/*.xaml`，WPF SDK 默认把 `**/*.xaml` 收成 `Page`，构建直接刷 39 个 `MC3074` 失败。`WinKit.csproj` 用 `DefaultItemExcludes` 一次性排除 4 个 `*-master` 参考目录（同时补进 `.gitignore`）；显式 `Content Include` 的 `Rack/Assets` 不受影响。
- **晾衣架「文本一条一条挂起来」是复刻方向错误（用户实测反馈后回查源码）**：`CM-idea/SnowAir-Rack` 里 `Plugin.cpp:90` 只有 `OnCapture(path)`（截图）自动上绳，文本**没有**任何自动上绳路径；`ClipboardBubble.cpp:329–393 LoadEntries()` 按 `EntryContentKey` 把复制过的文本**合并去重**成一个气泡列表，用户点「挂到绳子上」才生成卡片（`ClipboardBubble.cpp:1103` / `Plugin.cpp:225`）；信封 / 篮子还各带条数角标。旧实现反其道而行：`RackModule.LoadFromSources()` 在首次打开时把未完成待办 + 最近剪贴板文本 / 图片逐条挂满绳子，点信封又直接把当前剪贴板挂上去。修法：删除 `LoadFromSources()`，首次打开只恢复 `rack_items.json`；信封改为 `RackListPanel` 的剪贴板合并池（按内容指纹去重、重复显示 ×N、点「上绳」/双击才挂）；篮子改为待办合并池（按标题合并，只读 `todos.md`）；同正文重复挂载只晃原卡；设置拆成「截图完成后自动挂载」与「复制文本自动上绳」（**默认关**）两项。**本条后续又按用户反馈做了二次纠错（篮子语义 + 视觉复刻），见上方 Added 段。**
- **【严重】「一编辑就闪退」**：用户实测连崩三次，Windows 事件日志（`.NET Runtime` Id=1026）给出同一堆栈 —— `System.ArgumentException: 指定的 Visual 已经是另一个 Visual 的子级或者已经是 CompositionTarget 的根`，`AnnotationController.AddCommitted` (line 90) ← `EditorWindow.Canvas_MouseLeftButtonUp` (line 663)。根因：`Canvas_MouseLeftButtonDown` 已经把预览元素 `Children.Add` 进 `OverlayCanvas`，而矩形 / 箭头 / 画笔 / 椭圆的 `Visual` 与 `CreatePreview()` 返回的是**同一个实例**，松手提交时 `AddCommitted` 又 Add 一次，WPF 不允许 Visual 有第二个父级。马赛克宿主也会自行 Add，同样命中。修法：`AddCommitted` 先 `Children.Contains(visual)` 判重，只有文字标注这类「松手后重建 Visual」的才真正挂载，顺带加 null 防守。该重复 Add 路径在轮 1 的工具条换皮中没有被触碰，属既有代码结构缺陷；叠加项目零冒烟测试 + 零异常兜底，一直表现为「一画标注就无声退出」。
- **补齐全局异常兜底**：`App.OnStartup` 订阅 `DispatcherUnhandledException`，未处理的 UI 线程异常先写 `crash.log`（程序目录，含完整堆栈），再弹一次提示，`e.Handled = true` 保持托盘进程存活。此前项目**没有任何异常处理**，所以任何 UI 线程异常都直接击穿进程，排查只能靠翻事件日志（AGENTS.md 里「GUI 崩溃只表现为打不开」同源）。
- **编辑器「大黑边」**：用户截图 705×97 实测，除左右各约 14px 桌面外，中间 82px 高几乎全是 `#1E1E1E/#202020` 深色，工具条图标只占中间约 16px。三处叠加：`RootBorder` 的不透明 `#FF202020` 底板 + `Margin=10` + `WinShadow` 投影 + 半透明白边；`ScrollViewer` / `ImageHost` 的 `#FF141414` 背景（小截图时窗口被 ~700px 胶囊条撑宽，右侧一整片露出黑底）；铺满整行带分隔线的状态条。现改为窗口整体透明、图片层透明、状态文字收成紧贴文字的小标签，`BaseImage` 仍铺满 `ImageHost`，`ComposeImage` 渲染逻辑一行未动 —— **只保留编辑条**。
- **晾衣架（Rack）「点了没反应」** —— 这是一个从没真正跑通过的实验模块（CHANGELOG / ROADMAP / AGENTS 均无记录，且带有编译错误）。四条叠加根因：
  ① `RackModule.LoadFromSources()` **全项目零调用点**，托盘点「晾衣架」只 `Show()` 了一个高 8px、全透明的空窗口，绳子画在 Y=20 被裁掉 —— 视觉上等于什么都没发生；
  ② 从截图编辑器点「挂衣架」确实挂上了卡片，但 `DispatcherTimer` 每 50ms 轮询判定鼠标不在屏幕顶部，1.2s 后 `CollapseToTop()` 把窗口压回 8px，卡片一闪就没；
  ③ `Control.MousePosition` 是**物理像素**，却与 WPF 的 DIP 坐标 `Left/Width/Height` 直接比较，125% / 150% 缩放下顶部热区永远命中不了，悬停展开也不会触发；
  ④ 自动收起后窗口仍以 8px 热区 `IsVisible=true`，托盘 `Toggle()` 据此判定会先 Hide，用户要点两次才有反应。
  修法：托盘首次打开时调用一次 `LoadFromSources()`（未完成待办 + 最近剪贴板文本 / 图片），所有入口统一走 `RackWindow.Reveal()`；自动收起加 `_autoHideArmed` 武装开关（只有鼠标真正进过展开窗口，离开后才允许收起，程序挂入的卡片保持展开）；鼠标坐标经 `PresentationSource.CompositionTarget.TransformFromDevice` 换算成 DIP；显隐状态改看 `IsExpanded` / `RackModule.IsActive`，收起走 `Dismiss()`。
- **晾衣架透明窗口吞掉屏幕顶部点击**：`Background` 恒为 `null`（展开态空白区域穿透），`RopePath` 设 `IsHitTestVisible="False"`；热区判定本就靠 Win32 坐标轮询，不依赖 WPF 命中测试。收起态把绳子压成贴顶细线，保证「它还在」有可见反馈。
- **三处编译阻断**（本机构建时才暴露，此前任何发布版都不可能含此模块）：`RectAnnotation` / `EllipseAnnotation` 的 `FillAlpha` 由 `double` 改为 `byte`（`Color.FromArgb` 需 byte）；`RackWindow.DrawRope` 的 `new BezierSegment(start, ctrl1, ctrl2, end, true)` 改为 4 参（起点由 `PathFigure.StartPoint` 提供）。
- `AGENTS.md` 里 `VerifyLocalOcrRuntime` 更正为 csproj 实际的目标名 `VerifyLocalOcrModels`。

### Added 新增
- 晾衣架空态提示（无卡片时也能看出「已打开」）、托盘菜单按状态显示「晾衣架 / 收起晾衣架」、`SizeChanged` 后按卡片实际宽度重排、窗口 `Closed` 时停掉 50ms 轮询计时器。
- **本地编译门禁**：`tools/preflight.ps1` 自动检查 SDK / restore / build 0 错误 0 警告 / 核心 7 件套 / `models/` 7 文件 / 无 `runtimes/` / 产物体积 / 版本一致性共 10 项，退出码非 0 不许提交；`docs/local-preflight-checklist.md` 给出 13 项人工冒烟自检与发版四件套 + 三镜像资产清单。目的是把「编译错误进仓库」从偶发事故变成提交前必被拦下的事件 —— 纯本机按需执行，不接入任何远程自动化服务。
- `README.md` 快捷键表补齐 `Win + V`（剪贴板面板，含 Win 键走低级钩子）与 `Alt + T`（待办窗口）两条可配置热键，并修正「所有快捷键都在翻译与 OCR 分区」的旧描述。
- **截图选区层对齐 SnowAir（复刻轮 1）**：`#34C759` 1px 绿框、8 个白色方形夹点（四角 + 四边中点）、深灰 75% 信息条显示 `X / Y / W×H`。坐标与尺寸统一报**物理像素**，高 DPI 或负坐标副屏下不再出现「显示尺寸 ≠ 实际位图尺寸」。夹点层 `IsHitTestVisible="False"`，框选与 `Complete()` 主流程未动。
- **标注工具条换成黑色胶囊 17 键图标条（复刻轮 1）**：新增 `Capture/CaptureIcons.xaml`，30 个 24×24 `StreamGeometry` —— 18 个从 `SnowAir-main/images/icon/*.svg` 提取，12 个按同网格手绘补齐。主条槽位按 `show-cursor / rect / arrow / pen / text / number / mosaic / eraser / undo / more / pin / ocr / translate / long / save / cancel / confirm` 排列；未实现的能力保留槽位但**禁用 + 高亮提示「后续版本」**，不做点了没反应的死按钮。
- **「更多」浮动子工具条**：颜色 5 色、线宽 3 档、椭圆、重做、挂衣架，外加直线 / 聚焦两个占位；浮在图像上不占布局高度，子条内显示当前工具名，避免选中态落在子条里时主条看不出状态。
- 胶囊条拖动只响应左侧把手与空白处：`IsInsideButton` 沿可视树回溯 `ButtonBase`，避免 `DragMove()` 吞掉图标按钮的 Click。
- **文字标注（复刻轮 2 第 1 项）**：胶囊条第 5 键从禁用占位改为可用工具。点选后在图上拖出 `#34C759` 1px 虚线文本框，松手自动聚焦就地输入；框上方浮一条样式条（5 色 + 字号 −/+ + 确定 / 取消）。`Enter` 或 ✓ = 提交，`Esc` 或 ✕ = 取消，点击编辑区外即提交，空文本直接丢弃且不进撤销栈。提交后成为普通标注：虚线选中框 + 8 个白色控制点，可拖动、改色、`Delete` 删除、`Ctrl+Z` 撤销、横向缩放（文字自动换行、字号不变）。
  - 新增 `Capture/Annotations/TextAnnotation.cs`：`IAnnotation` 的第 7 个实现，与 `MosaicAnnotation` 同属「松手后由宿主接管」的两阶段标注。`SupportsRotation=false`，`Thickness` 只存不用（字号由样式条单独给，避免线宽滑块意外改字号）。`ToolKind` 加 `Text` 并注册工厂。
  - 输入框与样式条只在编辑期间挂在 `OverlayCanvas` 上，提交 / 取消时立即移除；`ComposeImage()` 的 `RenderTargetBitmap` 渲染逻辑一行未改，只在入口加「收尾编辑 + 去选中态」两行兜底，因此**光标、样式条、控制点都不会被截进导出图片**。
  - 键盘冲突：编辑期间 `Window_KeyDown` 整体让路（`Esc` 只取消编辑，`Delete` / `Ctrl+Z` / `Ctrl+Y` 交给 `TextBox` 自带行为），编辑结束归还。焦点离开输入框会延迟一帧提交，并用 `TextStyleBar.IsMouseOver` 放行样式条按钮，避免「颜色还没点下去编辑就被收掉」。
- **轮 2 剩余 5 项一次落地**（草图经批准后批量实现，中途不逐项停）：
  - **序号标注**：胶囊第 6 键解禁。点一下放一个 28px 圆徽标，自动 1→2→3，可连点；删除 / 撤销 / 重做**不重排已有编号**。走 `MouseDown` 直接 `AddCommitted` 的捷径，不进「预览 → 拖拽 → 提交」链路，避免徽标先在 `(0,0)` 闪一帧。新增 `NumberAnnotation`，固定尺寸、不支持旋转，选中显示亮绿虚线外框。
  - **直线**：子工具条第 2 键解禁。新增 `LineAnnotation`，复用 `ArrowAnnotation` 的点到线段命中 / 平移 / 旋转 / 快照，几何只少了末端两撇箭头；描边色与线宽跟随当前调色板和三档线宽。
  - **聚焦高亮**：子工具条第 5 键解禁。拖矩形后框内原样、框外压暗 `#8C000000`（55%）。实现为覆盖整张画布的 `Geometry.Combine(全画布矩形, 高亮矩形, Exclude)`，因此遮罩天然随 `ComposeImage()` 进导出图，无需额外合成。`HitTest` 只认边框附近 6px 的带 —— 否则整块画布都能点中它，用户就再也无法在框内画新标注。支持 8 控点缩放 / 拖动 / 撤销，新增 `HighlightAnnotation`。
  - **OCR**：胶囊第 12 键解禁。对「底图 + 已有标注」的 `ComposeImage()` 结果做离线识别，结果弹进**现有 `ResultWindow`**（复制 / 翻译 / 搜索 / 钉图 / 置信度状态栏全部白拿），编辑器保持打开。
  - **翻译**：胶囊第 13 键解禁。先 OCR 再走 `ITranslator`，沿用设置里的 Provider / Key / 源目标语言，结果同样进 `ResultWindow`；Key 型引擎未配 Key 时只在编辑器状态栏提示，不弹空白结果窗。
  - 接线：`TranslateModule` 新增 `RecognizeEditorImageAsync` / `TranslateEditorImageAsync` / `RecognizeBitmapAsync` / `IsTranslatorConfigured`，**复用主实例的 `_ocr` 与 `_translator`**，不新建第二个 200~300MB 引擎；`BitmapSource → PNG → GDI+ Bitmap` 交给 `IOcrProvider.RecognizeAsync`，识别历史照常落库。结果窗与编辑器同为置顶，弹窗前编辑器临时 `Topmost=false`，点回编辑器时由 `Activated` 恢复。
  - 仍为禁用占位：第 1 键「显示鼠标指针」、第 14 键「长截图」（属轮 3，需滚动拼接与新依赖）。
- **晾衣架一比一复刻（SnowAir-Rack 形态）**：施工草图经批准后一次落地。参照链为 `ScreenCapture-main`（祖宗）→ `SnowAir-main`（图标 / 工具条）→ `CM-idea/SnowAir-Rack`（卡片与主题源码）→ `%APPDATA%\SnowAir\plugin\rack\`（已装插件的素材与数据）。
  - **形态**：下垂 24px 的贝塞尔吊绳换成**近水平微垂横杆（Sag 6px）+ 左信封（back/front 叠层）+ 右篮子**，与 `theme_0..3.png` 一致。收起态仍把杆压成贴顶 8px 细线。
  - **4 套主题**：石墨 / 纸白 / 赤陶 / 深海，横杆色、卡片底、外圈、文字、夹子、信封、篮子整套联动；设置页直接显示官方 `theme_N.png` 预览。
  - **两套卡片模板**：照片卡（白边 + 圆角 + 投影 + 顶部夹子，按图片 `aspect` 调宽）与类型方卡（20% 外圈 + 白底 + 4px 类型色头）。10 种 CardType 枚举与 7 条官方类型色全部保留；WinKit 有数据源的 7 种（图片 / 文本 / 便签 / 链接 / 颜色 / 文件 / 待办）都有入口 —— 左信封按剪贴板内容自动判型（URL→链接、#RRGGBB→颜色、存在的路径→文件、多行→便签），SVG / 录屏 / 视频 / 音乐保留枚举但不产生空卡。
  - **持久化**：新增 `RackItemStore`，数据落 `%APPDATA%/WinKit/rack_items.json`（`version + items[]`，每条 20 字段，对齐插件）；位图按内容 SHA256 落 `rack_images/<hash>.png`；`hash` 去重、`offsetX` 管重排、`locked` 管固定、上限淘汰最旧的未锁定卡。一次补齐 ROADMAP 的「重启清空 / 不能重排」两个待办。
  - **交互**：横向拖动改顺序并落盘、纵向拖出到其它应用（一律 COPY）、点击复制、右键锁定 / 解锁 / 移除；两个合并池里点「上绳」才生成卡片。
  - **设置页新增「晾衣架」分区**（导航倒数第二，前 6 项索引不变）：外观 5 项（主题 / 卡片尺寸 / 横杆 / 信封篮子 / 夹子）、交互 5 项（悬停展开 / 自动收起 / 展开延时 / 收起延时 / 拖动重排）、数据 3 项（持久化 / 数量上限 / 截图后自动挂载），共 **13 项**，另有主题预览图与「清空晾衣架」按钮。改一项存一项，经 `SettingsChanged` 实时推给窗口，无需重启。
  - **五条铁律全部保留**：8px 热区、600ms 悬停展开、1200ms 离开收起、50ms Win32 坐标轮询、DPI 换算，以及 `_autoHideArmed`（程序挂入的卡片不会被自动收起带走）。
  - **素材与许可**：`Rack/Assets/` 22 张 PNG 共 1.8MB + `iconfont.ttf` 34KB，以 `Content` 形式随输出目录分发（不嵌进 dll）。插件本体 Apache-2.0；`basket*` / `envelope*` 是 PicRope 的 MIT 衍生品，`Rack/Assets/NOTICE.txt` 已附 MIT 全文与来源说明。
  - **文本合并语义补做（对齐插件后追加）**：新增 `Rack/RackListPanel.xaml(.cs)`（两个合并池共用一个气泡控件）、`RackItem.FromClipboardText()` 判型工厂、`RackCard.Shake()` 重复反馈、信封 / 篮子条数角标；`RackItemStore.ClearAll()` 改为「只清卡片 + 按引用清理孤儿 PNG」。设置页由 13 项扩到 **14 项**。五条铁律与 8px 热区 / 600ms / 1200ms / 50ms 轮询 / DPI 换算全部保留。
  - **二次纠错：两个合并池 + 一比一视觉复刻**（用户指出篮子做错成「历史」、样式全靠自绘方块太丑）：篮子改成**待办合并池**（只读 `todos.md`、按标题合并、未完成 / 已完成筛选），`RackHistoryStore.cs` / `rack_history.json` / `RackPanelMode.History` 全部删除；信封补齐图片 / 文件条目并按内容指纹去重。`RackListPanel` 推倒重做为官方 380×550 气泡（顶部 9px 箭头、搜索框、6 个分类 chip、卡片行、20×20 图标按钮、深浅主题），图标改补拷进项目的 `iconfont.ttf`（family name `font_family`，加载失败退回汉字）；`RackCard` 文本 / 便签 / 待办卡改用 `letter.png` 信纸；绳子加金属夹子，信封 / 篮子按素材原比例摆放，删掉自创的深灰空态条与系统默认按钮，角标改主题色小圆。面板高度 250 → 660px（按工作区高度钳制），`SetAnchor()` 让篮子气泡贴右展开。待办池**只读不回写**，避免覆盖待办窗口未保存的改动。五条铁律未动。
  - 轮 3 大件（长截图 / 录屏 / 二维码 / 水印）按用户指示**暂缓**。

### Changed 变更
- 版本号 2.4.0 → **2.4.1**；`WinKit.csproj` / `CHANGELOG` / `latest.json` 三处已对齐，README 快捷键表同步。**三镜像资产待上传**，`latest.json` 的 `note` 保留「待发布」标记，上传完成后删除。
- `EditorWindow` 按钮重命名与参数槽位调整：`UndoBtn/RedoBtn → BtnUndo/BtnRedo`、`ToolBar_MouseLeftButtonDown → CapBar_MouseLeftButtonDown`；`Tag` 改存 `StreamGeometry`，工具名与线宽数值改走 `CommandParameter`，`Tool_Checked` / `Width_Checked` 同步改读它。
- `tools/preflight.ps1`：进程内固定 `DOTNET_CLI_UI_LANGUAGE=en`，改用英文摘要 `N Error(s) / N Warning(s)` 精确解析。旧实现用「任意非零数字 + 一个词」的宽松匹配，会把失败构建里的「1 个错误」误判成 1 条警告（本次复刻首次构建实测触发）；解析不到摘要时回退到诊断文本匹配，不再产生假红字。

## [2.4.0] — 2026-10-29

### Fixed 修复
- **【严重】所有设置从未成功落盘过**（用户报告：透明度滑块「没生效」）。这一条的实际影响远不止透明度——**任何**设置项改完都只在本次运行内有效，退出即回默认值。根因在 `SettingsManager.SaveSettings` 的 DPAPI 加密环节：重新序列化时对非敏感字段调用了 `JsonElement.WriteTo(writer)`，而该方法**只写值、不写属性名**；`WriteStartObject()` 之后 writer 正停在「等待属性名」状态，收到一个 `StartObject` 值即抛 `InvalidOperationException: Cannot write a JSON value within an object without a property name`。`AppSettings` 的 60 个属性里只有 2 个敏感字段（`OpenAIApiKey` / `SyncPassword`）走带 name 的 `WriteString` 重载，其余 58 个全部命中这条错误路径 → **每次保存必然失败**。已补 `writer.WritePropertyName(prop.Name)`。
  - 为什么长期未被发现：旧代码把 `SettingsChanged?.Invoke` 写在 `try` 内部、异常分支只有一句 `Debug.WriteLine`（**Release 构建下整行被编译器剔除**）。于是「内存里已改、界面毫无反应、磁盘零痕迹、连错误日志都没有」四件事同时成立。现已将事件通知移出 `try`（配置生效不再取决于磁盘写成功）、写盘改为 `.tmp` + `File.Move` 原子替换、失败落 `%APPDATA%/WinKit/settings-error.log`。**这次正是靠这份新日志拿到了完整堆栈才定位到行号。**
  - 副作用修复：因该文件自 DPAPI 上线后从未被成功写过，其中 `SyncPassword` 一直是 **12 字符明文**。升级后首次保存会自动迁移为 DPAPI 密文。
- **识别中切到别的窗口，结果「凭空消失」**（用户报告：一换页面识别就像被打断了）。根因是三层叠加，**没有一层是取消识别**：
  ① `ResultWindow.Window_Deactivated` 不区分状态，失焦一律 `Hide()` —— 识别跑到一半用户切走，窗口当场消失；
  ② 窗口隐藏触发 `IsVisibleChanged` → `RequestSlimNow()`，把推理实例一并卸载了；
  ③ `RapidOcrProvider.CancelSlim` 的注释写着「即将开始一次识别时调用」，但**全项目从未在识别开始时调用过它**（唯一调用点在「关闭瘦身开关」那条分支里），闲置计时器无人取消。
  实际识别一直跑到了最后，结果只是写进了一个隐藏窗口，下次热键还要冷启动 3~5 秒 —— 于是看起来像「被打断」。因为 `Deactivated` 对任何失焦都触发，切软件内页面与切到别的程序是同一条路径，一处修完两种场景一起解决。
- **「MKL-DNN 加速」是个彻底的假开关**（Paddle → ONNX 迁移遗留）。设置页有开关、文案写着「Intel CPU 上显著提速」，`OcrEngineOptions.SameEngineConfig` 还把它算进重建判据，但 `RapidOcrProvider.BuildEngine` **从头到尾没读过这个值**——用户拨动它只会白等 1~3 秒引擎重建，然后什么都不会发生。已连同字段、设置项、事件处理器一并删除。
- **4K 整屏被压到 960px，小字直接不可读**。`MaxSideLen = 960` 会对最长边 >3000px 的图生效，3840×2160 的 4K 整屏被缩到 960×540（0.25 倍），12px 正文变成约 4px 再送进识别头。RapidOCR 官方默认 `max_side_len` 是 2000，本项目取了不到一半。已提到 **2048**（32 的整数倍，便于张量对齐）。常规 1080p / 2K 选区走不到这个分支，不受影响。
- **小字放大判据看错了对象**。旧逻辑用**整图高度** < 48px 判断要不要放大，但一个 900px 高的代码块里正文只有 12px——整图够高就永远不放大，小字原样喂给按 48px 行高训练的识别头。已改为按**检出框的中位高度**判断（见新增的「小字增强」）。
- **置信度被整段丢弃**。引擎回传了逐字符置信度（`TextBlock.CharScores`），`ToBlocks` 只取了文本和坐标，等于把「哪一行不可信」这个信号扔了。现在每块都带 `Confidence`，并一路传到版面层——顺带修掉一个隐蔽缺陷：`OcrTextLayout` 会重建 `OcrTextBlock`，若不同步搬运置信度，它会在版面重排时被悄悄重置为 1.0。

### Added 新增
- **小字增强（两遍制）**：首轮识别后若检出框中位高度（换算到引擎实际尺度）低于 32px，按目标 44px 行高整体放大再识别一遍，两轮之间按「字符总数 + 平均置信度」择优采用——字符变少或置信明显下降就保留首轮，避免放大反而把检测搞崩。放大倍率钳在 1.4~2.5，且受长边 2600px 硬上限约束（再大也会被引擎缩回去，白跑）。开关 `OcrSmallTextBoost`，默认开；状态栏显示「小字增强」。
- **置信度过滤**：低于阈值（`OcrTextScoreThreshold`，默认 0.5，与 RapidOCR 官方 `text_score` 一致）的块直接丢弃，滤掉图标、边框、页面纹理被误检成"文字"的垃圾块。为能统计「滤掉了多少」，引擎侧 `TextScore` 置 0 全量回传，过滤改在本层做。`OcrResult` 新增 `AverageConfidence` / `SmallTextBoosted` / `FilteredBlocks`。
- **状态栏显示置信度**：`RapidOCR · PP-OCRv6 Small · 14 块 · 420 ms · 置信 92%`。这是「这次识别有多可信」的单一量化指标，比让用户对着乱码猜有用。
- **结果窗布局重排**：`StatusText` 从「夹在复制与翻译/搜索之间」改为独占第二行，`置信 92% · 小字增强 · 已反色` 不再被两侧按钮挤压截断；标题行改为「标题 ＋ `[复制][翻译][搜索]` 成组 ＋ `📌 ✕` 最右」，三个操作钮统一 `MinWidth = 42`（旧版复制固定 56 宽、另两个按 padding 自适应，宽度参差）。三行结构由 `26 / 36 / *` 调整为 `36 / 22 / *`，头部反而省下 4px。
- **识别可真正中断**：改用 `DetectAsync`，`CancellationToken` 一路传进 ONNX `RunOptions`，每个 kernel 检查一次 terminate 标志——连 detection 这段最长的也能被打断。旧实现是同步 `Detect` 包在 `Task.Run` 里，取消只在「排队等信号量」那一段生效，识别一旦跑起来只能等它跑完再丢结果，白烧 CPU。
- **`Common/WindowBackdrop.cs`**：窗口背板着色收敛成单一实现（`FromPercent` / `Apply(Window)` / `Apply(Border)`，钳位 20~100%）。此前「透明度」设置只作用于 Todo 与剪贴板两个窗口，且着色代码硬编码在 `TrayHelper` 里；ResultWindow 等其余窗口各自硬编码 `#CCFFFFFF`，滑块拖到 40% 它纹丝不动——这才是用户看到「没生效」的直接原因。
- **识别结果窗接入透明度设置**：`ResultWindow.SetBackdropOpacity()`，由 `TranslateModule` 在窗口创建时与每次 `SettingsChanged` 时推送。
- **设置保存错误日志**：`%APPDATA%/WinKit/settings-error.log`，仅在异常时追加（含类型、消息、完整堆栈），超过 64KB 自动重开一份。

### Changed 变更
- **深色反色改为 3×3 网格投票**。旧实现只看整图外圈平均亮度，「深色窗口里嵌浅色对话框」这类深浅混排截图会整图判错极性，一次反色把正常的那一半毁掉。现在缩到 48px 长边切 9 格逐格判定，只有 ≥2/3 的格子偏暗才整图反色；混排时保守按浅色处理——宁可让深色那一半掉字，也不能让浅色那一半变乱码。缩略图读取也从逐点 `GetPixel` 换成了 `LockBits` 批量拷贝。
- **文字方向分类默认改为开启**。分类模型仅 1MB、开销占全流程 3~5%，关掉的代价是倒置文字（翻转截图、拍屏照片）直接出乱码——典型的省小时间丢大片正确率。已有配置不受影响，仍按用户存下的值走。
- **`IOcrProvider` 接口补齐**：新增 `ReconfigureAsync`、`RequestSlimNow`、`InitializationError`。此前这三者只长在 `RapidOcrProvider` 上，调用方得靠 `_ocr is RapidOcrProvider paddle` 做类型转换才能用——抽象层等于形同虚设，换引擎就得连调用方一起改。
- **结果窗在识别期间不再因失焦隐藏**：`SetLoading` → `SetResult` 之间 `_busy` 为真，`Deactivated` 直接返回，切软件内页面或切到别的程序都能看到进度与最终结果。识别完成时若窗口已失焦，再停 3.5 秒才收起（期间 `Activated` 会取消该定时器）；另有 30 秒兜底复位，防止链路被「静默 OCR」顶掉后窗口永久卡在「识别中…」且永不自动隐藏。
- **窗口隐藏不再连带卸载引擎**：`TranslateModule.OnResultWindowVisibilityChanged` 增加 `&& !w.IsBusy` 判断；`RapidOcrProvider.RecognizeAsync` 开头补上 `CancelSlim()`，取消分支与 `finally` 都恢复 `ScheduleSlim()`（原本取消分支会直接 return，计时器永久停在取消态、引擎从此不再瘦身）。
- 设置页「翻译与 OCR」分区：移除「MKL-DNN 加速」，原位置替换为「小字增强」（同一行布局，未新增区块）。
- **建立全局视觉规范单一来源**（`App.xaml` 新增 7 个键），删掉三份窗口私有副本（`ResultWindow.ResultActionBtn`、`ToastWindow.ToastButton/ToastButtonPrimary`、`EditorWindow.ActionBtn`）：`Radius12` / `Radius8`（浮窗 / 窗内卡片）、`WinShadow`（16/2/0.16，`x:Shared="False"` 保证每窗独立实例，避免共享 `DropShadowEffect` 在多层窗口同时渲染时互相干扰）、`WinBtn` / `WinBtnPrimary`（浅色浮窗）、`WinBtnDark` / `WinBtnDarkPrimary`（深色工具界面，同尺寸同交互）。此前阴影有 4 套互不相同的参数、5 个窗口完全没有阴影，圆角则是 8 个窗口 10 + 通知卡 12。
  - 顺带消除一个隐患：`EditorWindow` 曾自定义 `ActionBtn`，与全局同名——窗口级资源会**静默覆盖**全局定义，改全局样式对它无效。
- **结果窗按「卡片化」重排**：标题行只留标题与 `📌 ✕`（按钮常态 0.5 透明、鼠标进标题栏提到 1，钉住时保持常亮，不再强制常显）；原文 / 结果各自成为 `#0A000000` + 圆角 8 的卡片块，并有 8px 间距，取代原来那条 1px 分隔线；`[复制]` 采用 Primary 变体（深边 + Medium）区分主操作、`[翻译] [搜索]` 为次级，三钮统一 `MinWidth = 42`；底部补 `ResizeGrip`——此前 `ResizeMode="CanResize"` 却没有任何抓手，用户不知道窗口能拖大。窗口由 440×340 调整为 460×360，`RootBorder` 留 `Margin=10` 给投影让位（`AllowsTransparency` 窗口的绘制会被裁到窗口矩形，不留边则外扩阴影全被切掉）。
  - 纯识别场景（`Alt+Shift+S`）不再显示「识别结果」小标签：窗口标题已写明「文字识别」，块内重复一次是冗余信息。翻译场景仍保留「原文 / 译文」——那时确实需要区分。
- **图片编辑器工具条规范化（保持深色底）**：工具条由「工具｜撤销重做｜复制 另存为 关闭」挤在一行，改为三段分组（工具 / 历史 / 导出），`✕` 从导出组拆出单独靠最右；选中态由纯亮蓝 `#0A6CFF` 实心块改为淡蓝底 `#261A8CFF` + 蓝字 `#7FB8FF`（实心块亮度太高，抢画面判断）；状态文字从工具条最右移到底部独立一条，不再与按钮争宽度。**底色的深浅不参与「统一」**——看图与标注需要中性暗底才能准确判断颜色与对比度，统一的是圆角体系、按钮尺寸/字号、主次分级与 hover 强度。
- **框选提示条**：圆角 6→8 对齐规范，「ESC 取消」降为半透明次级信息，「拖拽选择识别区域」保持主信息。
- 其余 6 个窗口（Todo / 剪贴板 / 历史 / 编辑对话框 / 关于 / 设置页）只替换圆角与阴影来源，布局未动。

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
