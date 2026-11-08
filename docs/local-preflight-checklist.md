# 本地自检与发版清单

> 建立背景：`CHANGELOG` 里多次出现「编译期 0 错误 0 警告、跑起来才暴露」的严重缺陷——最典型的是 v2.4.0「所有设置从未成功落盘过」、v2.3.3「卡片按钮不显示」等编译期 0 错误 0 警告的缺陷。
> 本清单把「提交前必过」固化成一条本机命令 + 一份人工冒烟表。**全程只在本机按需执行，不涉及任何远程自动化服务。**

---

## 一、什么时候跑

| 时机 | 动作 |
| :--- | :--- |
| 改完任意 `.cs` / `.xaml` / `.csproj` | 跑一次门禁脚本 |
| 提交前 | 再跑一次，必须全绿 |
| 发版打包前 | 跑门禁 + 走完第三节人工冒烟 + 核对第四节四件套 |
| 换了 .NET SDK / 换了构建机器 | 必须重跑（含 `-NoRestore` 之外的完整还原） |

---

## 二、门禁脚本（自动检查）

```powershell
# 完整跑（推荐，含 NuGet 还原）
powershell -ExecutionPolicy Bypass -File tools\preflight.ps1

# 已还原过、只想快速复验编译
powershell -ExecutionPolicy Bypass -File tools\preflight.ps1 -NoRestore
```

退出码 `0` = 全绿，`1` = 有检查失败。**失败时不要提交。**

自动检查 10 项：

| # | 检查 | 拦截什么 |
| :-- | :--- | :--- |
| 1 | SDK 可用（`dotnet --version`） | PATH 上只有运行时、没有 SDK 的假环境 |
| 2 | `dotnet restore` 成功 | 缺包 / 镜像源不可达 / `Directory.Build.props` ref pack 版本对不上 |
| 3 | `dotnet build -c Release` 退出码 + 无 `error CS/MSB/NETSDK` | **编译错误进仓库** |
| 4 | 0 警告 | 静默劣化（未用字段、异步漏洞等） |
| 5 | 核心 7 个二进制齐全 | 发布包缺 exe / 缺 ONNX 原生库 |
| 6 | `models/` 7 个文件齐全 | `VerifyLocalOcrModels` 只校验 3 个，另外 4 个漏了照样能构建 |
| 7 | 输出目录无 `runtimes/` | RID 锁丢失 → 全平台原生资产平铺、体积暴涨到约 378MB |
| 8 | 产物体积 40–120MB | 正常约 69MB；偏大偏小都说明依赖被改坏 |
| 9 | `csproj <Version>` == `latest.json version` | 版本号漂移 |
| 10 | `CHANGELOG.md` 有对应版本标题 | 忘了把 `[Unreleased]` 转正 |

脚本最后打印 PASS / FAIL 汇总表，任一红字即退出码 1。

> 改了 `TargetFramework`、`RuntimeIdentifier`、模型清单或版本号时，同步更新 `tools/preflight.ps1` 里的常量，别让门禁自己过期。
>
> 项目根目录下的 `ScreenCapture-main/`、`SnowAir-main/`、`SyncClipboard-master/`、`SyncClipboardCfServer-master/` 是第三方参考源码副本，已由 `WinKit.csproj` 的 `DefaultItemExcludes` 排除编译、`.gitignore` 排除提交。**以后再新增参考目录要同步改这两处** —— 官方 SyncClipboard 客户端里带 WinUI3 的 `App.xaml` / `Views/*.xaml`，不排除会被 WPF 默认当成 `Page` 卷进构建，一次刷出 39 个 `MC3074` 直接打断发布。

---

## 三、最小冒烟自检（人工，约 3 分钟）

编译通过只能证明「能生成 IL」，下面这些是历史上真实翻过车的点。**由你在本机启动程序逐个点，助手不代为启动常驻进程。**

| # | 步骤 | 期望 |
| :-- | :--- | :--- |
| 1 | 运行 `bin\Release\net8.0-windows\WinKit.exe` | 托盘出现图标，无启动期异常弹窗 |
| 2 | 托盘右键 → 设置 → 随便改一项（如不透明度）→ 关设置 → 重启程序 | 设置**重启后仍在**（v2.4.0 曾整段失效）；异常时查 `%APPDATA%\WinKit\settings-error.log` |
| 3 | `Alt + A` 框选任意区域 | 选区是 **1px 亮绿框 + 8 个白色方形夹点**，深灰信息条显示 `X / Y / W×H` 且数值等于实际位图像素；标注编辑器打开后是**居中黑色胶囊 17 键图标条**，未实现的键为灰禁用态（有「后续版本」提示）；点「更多」能弹出颜色 / 线宽 / 椭圆 / 重做；点图标按钮正常响应，拖左侧把手或胶囊空白处才能移动窗口；**窗口除黑色胶囊与一行状态小字外全部透明**，图片之外的区域直接透出桌面，不能有一圈深色底板 / 投影 / 白边（尤其小选区时胶囊条会把窗口撑宽） |
| 4 | `Win + V` | 弹 WinKit 剪贴板面板而非系统自带面板 |
| 5 | `Alt + Shift + S` 框选一段带文字的图 | 结果窗出文字、状态栏显示置信度；识别途中切到别的窗口，结果不会「凭空消失」 |
| 6 | 托盘 → 关于 | OCR 引擎状态正常、无模型回退告警 |
| 7 | 编辑器点第 5 键「T」，在图上拖一个框，输入两行文字 | 拖框阶段是亮绿 1px 虚线框；松手后框内自动聚焦、上方浮出样式条（5 色 / 字号 −+ / ✓✕）。改颜色与字号能实时看到变化；`Enter` 或 ✓ 提交后成为普通标注，出现虚线选中框 + 8 个白色控制点，可拖动 / 改色 / `Delete` 删除 / `Ctrl+Z` 撤销；`Esc` 或 ✕ 取消且不留痕迹；空文本提交应被丢弃。编辑途中按 `Delete` 或 `Ctrl+Z` 只作用于输入框、不会删掉已有标注或关窗口；点「完成」导出的图片里**不能有光标、样式条或控制点**。 |
| 8 | **每个工具各画一个**：矩形 / 箭头 / 画笔 / 椭圆 / 马赛克 / 文字，每次都松手 | **进程不闪退**、标注留在画布上、`Ctrl+Z` 能撤销。历史上 `AddCommitted` 把已在画布里的预览元素二次 `Add`，抛 `ArgumentException` 直接击穿进程（用户连崩三次，事件日志 `.NET Runtime` Id=1026 可查）；这条是回归验证的第一优先级 |
| 9 | 若运行中出现「WinKit 异常」弹窗 | 程序目录生成 `crash.log`（含完整堆栈），托盘图标仍在、进程未退出；把日志内容贴回来排查，不要只看「闪退了」 |
| 10 | 依次试轮 2 新能力：序号 / 直线 / 聚焦高亮 / OCR / 翻译 | **序号**：连点 3 下出现 1/2/3，删掉 2 后 3 仍是 3，`Ctrl+Z` 能撤销；**直线**：拖一下出线，换颜色与线宽生效；**聚焦**：拖框后框外压暗、框内原样，**框内仍能继续画新标注**，点边框可选中 / 拖动 / 删除，导出图里压暗效果正确；**OCR**：点第 12 键后弹出结果窗（文字 + 置信度状态栏），编辑器不关闭、不新建引擎；**翻译**：配好 Key 后点第 13 键出译文，未配 Key 时编辑器状态栏给提示而不是空白结果窗 |
| 11 | 剪贴板同步（需先配好服务端）→ 设置 → 同步 | **长文本**：复制一段 >10240 字符的文本 → 对端收到**全文**而不是前 10240 字前缀；反向从手机 / 官方客户端推一段长文本过来，WinKit 剪贴板也是全文，且不会把截断内容再推回去（观察几轮，两端内容不来回抖动）。**文件夹**：资源管理器右键选一个**含子目录**的文件夹 →「同步到手机（WinKit）」→ 推送成功、服务端不返 400 `Hash is not match data.`，对端解包后目录层级完整；文件 + 文件夹混选同理；拉下一个 Group 后文件应进本地剪贴板历史。**认证**：故意把密码改错 → 同步状态显示「认证失败（401/403），已暂停同步」，不再每 5 秒打一次请求；改回正确密码并保存（会热重启引擎）后立即恢复。**推送成功后**：对端仍能正常拉到刚推的图片 / 文件（客户端只在 PUT 失败时清暂存，成功时绝不 `DELETE /file/{name}` —— 官方 Server / WebDAV 上该 URL 可能已指向持久文件，误删会破坏数据） |

任何一项不过，先记进 `CHANGELOG.md` 的 `[Unreleased]` / `Fixed`，别直接发版。

---

## 四、发版四件套 + 三镜像资产

按顺序做，**前三步是本地文本改动，第 4 步是真实发布动作，需你明确下达「发布」指令后才执行**：

1. `WinKit.csproj` → `<Version>` 改为目标版本。
2. `CHANGELOG.md` → `## [Unreleased]` 改为 `## [x.y.z] — YYYY-MM-DD`。
3. `README.md` → 快捷键表、功能说明与新版本一致。
4. `latest.json` → `tag` / `version` / `publishedAt` / `zip` / `downloadUrl` / `mirror` / `cnb` 全部改为新版本；`note` 里去掉「待发布」字样。
5. 打包：`WinKit-vX.Y.Z-win-x64.zip`，确认解压后约 69MB、含 `models/`、无 `runtimes/`。
6. 三个镜像各传一份同名 zip：
   - Gitee `worhllo/WinKit` Releases（**自动更新探测取这里**）
   - GitHub `oishijie/WinKit` Releases（302 探测 + 下载镜像）
   - CNB `/-/releases`（**仅作下载镜像**：其 release API 与 git/raw 匿名一律 401，不能做探测源；上传走「取 asset-upload-url → PUT 上传地址 → POST verify_url」三步）
7. 更新仓库根 `latest.json` 并推送；确认三处匿名下载均返回 200 且 `Content-Type` 不是 HTML。

> 版本探测只认 **Gitee API + GitHub 302** 两条路；CNB 网页 `/-/raw/` 会返回 HTTP 200 的 SPA HTML，只看状态码会被骗。
> `latest.json` 只供人 / 脚本取镜像清单，**程序不读取它**。

---

## 五、常见失败与处置

| 现象 | 原因 / 处置 |
| :--- | :--- |
| `No .NET SDKs were found` | PATH 命中了只有运行时的 `dotnet.exe`。装 SDK，或用带 SDK 的 `dotnet.exe` 全路径 |
| `NU1102` / ref pack 缺 8.0.29 | `Directory.Build.props` 把 ref pack 覆盖到镜像已有的 8.0.33；换 SDK 后按实际可用补丁调整，**不要删这个文件** |
| 出现 `runtimes/` | `RuntimeIdentifier` 丢了；恢复 `win-x64` 后**清空 `bin/Release` + `obj/Release` 再构建**，旧产物不会自动消失 |
| `models/` 校验失败 | 克隆仓库时漏了 `models/`（约 37MB），补齐 7 个文件 |
| `error MC3090` 指向 `StreamGeometry` | XAML 不允许在「用初始化字符串经 TypeConverter 创建对象」的元素上再写元素特性（如 `FillRule`）；去掉该特性即可，`StreamGeometry` 默认 `FillRule` 就是 `EvenOdd` |
| 门禁输出中文乱码 / 摘要解析异常 | 脚本已固定 `DOTNET_CLI_UI_LANGUAGE=en`；若手工 `dotnet build` 出现乱码，加同一环境变量即可，勿在 ps1 里写中文字面量（PS 5.1 按 ANSI 解码 UTF-8 无 BOM） |
| `MSB3026` / `MSB3027` 提示文件被 `WinKit (PID)` 占用 | 托盘里的旧实例还在跑，锁住了 `WinKit.exe` / `WinKit.dll`。**先从托盘退出旧实例**再构建；这类失败只发生在产物复制阶段，源码编译本身是通过的（日志里不会有 CS / MC 错误）。不要去结束用户进程 |
| 改动后门禁全绿但 GUI 仍异常 | 走第三节人工冒烟；这类缺陷静态检查抓不到 |
