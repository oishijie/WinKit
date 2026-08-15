# WinKit - 统一桌面助手：待办 · 剪贴板 · 本地 OCR 翻译 🚀

`WinKit` 是一款专为 Windows 10 / 11（64 位）设计的轻量、美观、**本地优先**的效率工具箱。它把三件高频桌面事务整合进一个常驻系统托盘的程序：

- **TodoList 待办清单** — 毛玻璃悬浮窗，常驻桌面
- **Clipboard 剪贴板历史** — 全局拦截 `Win + V`
- **本地 OCR 翻译** — PaddleOCR 离线识别 + 在线/大模型翻译，截图即翻

视觉体系采用磨砂半透明（毛玻璃），并自研贴合 Windows 11 Fluent 风格的系统托盘菜单与统一设置中心。

> 本项目基于 [li5bo5/WinKit](https://github.com/li5bo5/WinKit/releases) 二次开发，沿用 **AGPL-3.0** 协议开源。当前维护仓库：[worldoi/WinKit](https://github.com/worldoi/WinKit)。

---

## ✨ 核心特性

### 1. 📂 TodoList（待办清单）
* **无干扰悬浮窗**：常驻桌面，支持鼠标拖拽调整位置与大小。
* **快捷状态控制**：一键开启「置顶显示」或「鼠标穿透」（穿透后不干扰桌面正常操作）。置顶 / 穿透开关也集成在设置中心 → 待办，实时生效。
* **轻量本地持久化**：待办条目采用标准 Markdown 格式实时同步至本地 `%AppData%\WinKit\todos.md`，方便多端同步与查看。
* **双击快速交互**：双击列表空白处即可轻松添加新待办，双击已完成条目即可一键删除。

### 2. 📋 Clipboard（剪贴板历史）
* **系统级拦截（`Win + V`）**：全局低级键盘钩子接管原生剪贴板，弹出本工具专属的历史窗口，并聚焦到列表。
* **开始菜单防弹 Bug 修复**：独创 `0xFF` 虚拟击键防骚扰机制，彻底解决了拦截 `Win` 组合键时系统容易误弹出「开始菜单」的痛点。
* **文本 + 图片双支持**：不仅记录文本历史，同时监听并存储剪贴板图片（截图、复制的图片），列表中显示缩略图，双击即可回填粘贴。
* **内置截图功能**：标题栏 📷 按钮或全局快捷键 `Alt + A` 即可框选截图，截图自动存入剪贴板历史，无需第三方工具。
* **窗口钉住（Pin）**：标题栏 📌 按钮可将面板钉在屏幕上，失焦后不再自动隐藏，方便反复查看历史记录。
* **极致轻量界面**：去除了累赘的搜索框，背景不透明度支持 40% ~ 100% 多档个性化调节（默认 **80%**），提供高对比度的文字排版。
* **双击自动粘贴（回填）**：双击历史条目即可自动复制、隐藏窗口、并在 80ms 后模拟 `Ctrl + V` 物理击键，将内容直接填入原焦点输入框中。
* **隐私监听开关**：支持在托盘子菜单中一键「开启 / 关闭监听」，保障敏感工作环境的隐私安全。

### 3. 🔍 本地 OCR 与翻译
* **截图翻译（`Alt + S`）**：框选截图 → 本地 PaddleOCR 识别 → 翻译 → 结果窗（原文 + 译文）。
* **划词翻译（`Alt + D`）**：复制选中文本 → 翻译 → 结果窗。
* **本地 OCR（`Alt + Shift + S`）**：仅识别截图中的文字，不做翻译。
* **静默 OCR（`Alt + Shift + F`）**：识别后自动复制到剪贴板，不弹窗。
* **截图到剪贴板（`Alt + A`）**：框选截图后直接存入剪贴板历史，无需翻译或 OCR，快速归档。
* **完全离线识别**：OCR 由 **PaddleOCR（CPU 推理）** 在本地完成，不联网、不上传任何图像。
* **模型热切换**：在设置中心切换 OCR 模型后无需重启应用，引擎自动在后台重建并预热，下次快捷键即时响应。
* **可切换翻译引擎**（默认国内大模型，国内网络直连、无需代理）：
  - **国内大模型（DeepSeek 等 OpenAI 兼容）**：默认引擎，预填 `https://api.deepseek.com/v1` 端点，填 Key 即用；也可改接通义、本地 Ollama / vLLM 等任意 OpenAI 兼容服务；
  - **百度翻译**：在设置中填写 APP ID + SecretKey，国内直连、无需代理；
  - **通用 OpenAI 兼容**：自定义 Base URL（如 `api.openai.com`）；
  - **Google**：免费免 Key 端点（国内需代理）。
  - API Key / SecretKey 均使用 Windows DPAPI 加密存储，不明文落盘。
* **超大截图兜底**：超长图自动降采样（`max_side_len=960` 兜底），避免推理卡死。
* **模型缺失自动回退**：所选 OCR 模型文件缺失时，自动回退到另一个可用模型并在状态区提示。

### 4. ⚙️ 统一设置中心
* 入口：托盘右键「**设置**」，或 Todo 标题栏齿轮「**⚙**」。
* 五个分区：**通用 / 待办 / 剪贴板 / 翻译与 OCR / 关于**。
* **改一项存一项、即时生效**；窗口不透明度滑块拖动时主窗口实时变淡预览。
* **快捷键可配置**：翻译与 OCR 分区内可点击录制新的快捷键组合，保存后立即生效，无需手动改代码。
* 关于页显示版本、作者、项目地址、AGPL-3.0 协议与 OCR 引擎运行状态（含模型回退提示）。

### 5. 🎨 现代自研 Fluent 托盘右键菜单
* **WPF 上下文菜单接管**：彻底弃用 WinForms 的陈旧菜单，采用 WPF 自研设计系统，带来 8px 圆角、微透明磨砂白底色、高档投影与 hover 动态高亮。
* **完美的失焦自动关闭**：引入常驻后台的 0×0 像素焦点宿主窗口 `MenuHostWindow`，菜单弹出时被置于前台，点击屏幕其他任意区域即瞬间合起，无任何闪退。
* **不透明度子菜单**：托盘菜单内直接选择 40% / 60% / 70% / 80% / 90% / 100% 档位，即时刷新并保存。

---

## ⌨️ 快捷键

| 快捷键 | 功能 |
| :--- | :--- |
| `Alt + S` | 截图翻译 |
| `Alt + D` | 划词翻译 |
| `Alt + Shift + S` | 本地 OCR（仅识别） |
| `Alt + Shift + F` | 静默 OCR（识别后复制到剪贴板） |
| `Alt + A` | 截图到剪贴板历史 |

> 所有快捷键均可在设置中心「翻译与 OCR → 快捷键」页点击录制修改，保存后立即生效。

---

## 🖥️ 系统要求

* **操作系统**：Windows 10 / 11（64 位）
* **运行时**：[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)，或下载自带运行时的独立免装版
* **本地 OCR 依赖**：约 390MB 原生推理库（PaddleOCR + Paddle Runtime），首次 `dotnet restore` 时自动还原

---

## 💾 下载

* **本项目发布页（worldoi/WinKit）**：[GitHub Releases](https://github.com/worldoi/WinKit/releases)
* **原项目 / 上游（li5bo5/WinKit）**：[GitHub Releases](https://github.com/li5bo5/WinKit/releases)

---

## 🛠️ 编译与发布

> ⚠️ 本地 OCR 需要原生推理库，构建前**必须**先 `dotnet restore` 还原 PaddleOCRSharp / Paddle.Runtime.win_x64（否则 `VerifyLocalOcrRuntime` 构建目标会直接报错）。

```powershell
# 0. 还原依赖（首次必须，下载约 390MB 本地 OCR 原生运行时与模型）
dotnet restore

# 1. 编译开发版（输出至 bin/Release/net8.0-windows/）
dotnet build -c Release
```

发布为单文件可执行（二选一）：

```powershell
# 极致精简版（Lite）：需预装 .NET 8 Desktop Runtime
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:PublishReadyToRun=true -o Publish_Releases\Lite

# 独立免装版（Standalone）：自带运行时，双击即用
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true -o Publish_Releases\Standalone
```

---

## 📄 开源协议

本项目采用 **AGPL-3.0 (GNU Affero General Public License v3.0)** 协议开源，基于 [li5bo5/WinKit](https://github.com/li5bo5/WinKit/releases) 二次开发。
