# ROADMAP — WinKit

统一桌面助手：TodoList + Clipboard + 截图标注 + 本地 OCR 翻译（常驻托盘，net8.0-windows x64，WPF + WinForms）。
维护仓库 [oishijie/WinKit](https://github.com/oishijie/WinKit)，上游 [li5bo5/WinKit](https://github.com/li5bo5/WinKit)，协议 **AGPL-3.0**。

本文件是路线图：规划方向、排序优先级、记录决策。已完成项沉淀进 `CHANGELOG.md`，协作与架构约定见 `AGENTS.md`。

---

## 当前基线（v2.2.0 + Unreleased：剪贴板同步）

已落地：四大功能模块（TodoList / 剪贴板历史 / 截图标注 / 本地 OCR 翻译）、快捷键全面可配置（单一数据源 `HotkeyCatalog`）、OCR 引擎 ONNX Runtime（分发包 44MB）、识别历史、深色反色、单实例保护、DPAPI 加密。
上游移植：FastClipboard、待办回收站、图片缓存生命周期清理（详见 `CHANGELOG.md` 2.2.0 段）。
**新增（Unreleased）：剪贴板同步模块** —— 兼容 SyncClipboard 官方协议的文本/图片/文件推拉，服务器用 SyncClipboardCfServer（CF Workers）；推送时机分级（文本自动推、图片/文件默认仅入历史）、资源管理器右键显式推送、设置中心「同步」分区（7 分区）。详见 `CHANGELOG.md` Unreleased 段。

## 近期（下一版目标）

### 0. 剪贴板同步二期（已完成，2026-10-29）
- [x] **Group 多文件同步**（官方 zip 打包语义）：右键多选自动打包 `File_时间戳.zip` 推送，远端 Group 拉取解包写回剪贴板；条目清单 hash 对齐官方 CalculateEntriesHashAsync
- [x] 拉取图片质量优化：原始字节直接入库（保留 jpg/png 原格式，免二次编码），剪贴板双格式写入（位图 + FileDropList，可粘贴出原文件）
- [x] 同步状态可视化：设置页「同步」页顶部显示状态（运行/已停用/连续失败），含上次成功时间与最近错误
- [x] 失败重试退避：连续失败拉长轮询间隔（上限 8 倍），恢复后立即回落

### 1. 待办回收站收尾（已完成，2026-10-29）
- [x] 设置中心「待办」分区暴露「回收站保留天数」（1-365）
- [x] 回收站窗口支持 Shift/Ctrl 多选批量还原 / 删除（统计栏旁按钮，带选中计数）

### 2. 图片缓存清理可视化（已完成，2026-10-29）
- [x] 设置中心「剪贴板」分区新增「图片缓存」卡片：保留天数 / 容量上限（10-1024 MB）
- [x] 当前占用实时显示（进入设置或切到该页时计算一次），附「打开文件夹」入口

### 3. 截图标注增强（按需）
- [ ] 步骤序号工具（截图教程场景高频）
- [ ] 文本标注（当前只有矩形/箭头/画笔/马赛克）
- [ ] 贴边描边裁切修正：`ComposeImage` 对贴边矩形/箭头的描边外溢会被裁掉约 1px，可给 RTB 加 4px 内边距解决
- [ ] 全局热键按下后选区窗口偶发抢不到焦点（已加 `_activatedOnce` 门闩，若仍复现再补 SetForegroundWindow 强制链）

### 4. 常用短语（上游 v2.11 的 vv 模块，可选）
- [ ] 移植 `QuickPhrase*` 系列；核心难点：vv 连击判定与中文输入法状态识别长在 `KeyboardHookService` 里，需把该段（约 60 行）手工移植进本仓库钩子，**不得整体覆盖**（会毁掉 0xFF 防开始菜单机制）

### 5. 晾衣架（Rack）MVP 可用性（已完成，2026-11-07）
- [x] ~~首次打开自动装载待办 / 剪贴板内容~~ → **2026-11-07 复刻修正**：该行为与 SnowAir 相反，已删除；首次打开只恢复 `rack_items.json` 里的卡片，空态提示、托盘菜单状态（收起 / 展开）同步保留
- [x] 修复高 DPI 下顶部热区失效、挂入卡片 1.2 秒后消失、自动收起后需点两次、透明窗口吞掉屏幕顶部点击
- [x] 补全模块文档（`AGENTS.md` 架构铁律 + README 功能说明 + CHANGELOG）
- [x] 持久化（重启清空）、卡片单独移除 / 拖回排序 —— **2026-11-07 一比一复刻时一并完成**：`rack_items.json`（20 字段）+ `rack_images/<hash>.png`、hash 去重、上限淘汰、横向拖动重排落盘、右键移除 / 锁定；另加 4 套主题、信封 / 篮子、14 项设置分区；**两个合并池**：信封＝剪贴板按内容去重（文本 / 图片 / 文件），篮子＝待办按标题合并（只读 `todos.md`），两边点「上绳」才挂，只有截图自动上绳、同正文只晃原卡；**视觉**：官方 PNG 全套 + `iconfont.ttf` 图标字体 + `letter.png` 信纸卡 + 380×550 几何气泡 + 金属夹子

## 中期

- **net10 迁移（可行性已评估 2026-11-07，⭐ 只评估、未动 TFM）**：`net8.0-windows` 的官方支持在 **2026-11-10** 结束（微软支持策略，最后补丁 8.0.33）。实测结论 **可行、风险中低**：
  - 本机已有 SDK **10.0.302**、`Microsoft.WindowsDesktop.App` 运行时 **10.0.10 / 10.0.13**、ref pack **10.0.10**，工具链齐备。
  - **RapidOcrNet 4.2.0 直接支持**：`lib/net10.0` + `lib/net8.0` 双资产，nuspec 里明确声明 `targetFramework="net10.0"` 依赖组（Clipper2 2.0.0 / ONNX Runtime 1.29.0 / SkiaSharp 3.119.1）。迁移不必换 OCR 引擎。
  - ONNX 原生 `onnxruntime.dll` 位于 `runtimes/win-x64/native`，**与 TFM 无关**；托管 `OnnxRuntime.Managed` 最细到 `net8.0` / `netstandard2.0`，net10 项目可向下引用。`SkiaSharp 3.119.1`、`System.Drawing.Common 8.0.7`、`Sqlite 8.0.0`、`ProtectedData 8.0.0` 同理都有 net8.0 或 netstandard2.0 资产。
  - ⚠️ **唯一硬阻塞**：`Directory.Build.props` 把三个 ref pack 版本钉死在 **8.0.33**（SDK 10 还原 net8 的镜像补丁）。改 TFM 到 `net10.0-windows` 时必须先删掉或改成「仅 net8 生效」的条件属性，否则 SDK 10 会拿 8.0.33 去当 net10 的引用程序集。
  - 前置四步（**本轮不做**）：① 条件化 `Directory.Build.props` → ② 用命令行 `-p:TargetFramework=net10.0-windows` 试构建，`obj/bin` 用 `-p:BaseIntermediateOutputPath` 隔离到临时目录，不污染工作区 → ③ 过 `tools/preflight.ps1` + `docs` 的 13 项人工冒烟（重点验 OCR 预热 / 全局热键 / 低级钩子）→ ④ 才改 `.csproj` 的 TFM 并更新门禁脚本的输出路径常量。
- **翻译引擎补全**：DeepL / Azure 接入（`ITranslator` 接口已预留位），补设置项与测试。
- **OCR 识别质量（第二轮，已完成 2026-10-29）**：大图缩放上限 960→2048、小字增强两遍制、置信度过滤与状态栏展示、`DetectAsync` 真取消、反色改 3×3 网格投票、`IOcrProvider` 接口补齐。详见 `CHANGELOG.md` Unreleased 段。
- **OCR 剩余可选项**（按收益排序，均未开工）：
  - [ ] **分块识别**：解决 >3000px 的超长截图 / 4K 整屏。当前是等比缩到 2048（保精度就丢分辨率，保分辨率就爆内存），正解是切成带重叠的块、每块原生分辨率识别、再按坐标合并去重。复杂度集中在「跨块文本行的缝合」，容易引入重复文字。
  - [ ] **识别模型 INT8 动态量化**：rec_small 是 FP32（21.2MB / 520 万参数），动态量化约可降到 6MB，分发包 46.5MB → 约 31.5MB，业界实测精度损失 1~2 个百分点。⚠️ **只量化 rec，不要量 det** —— DBNet 输出经 sigmoid 压到 [0,1]，INT8 在这么窄的值域上分辨率不足，是已知坑。
  - [ ] **字级坐标（划词翻译地基）**：`ReturnWordBox = true` 后 CJK 返回单字框、拉丁返回单词框。前置条件是先确认 `WordResults` 的元素类型与属性名（本仓库尚未验证）。
  - [ ] **系统 OCR 第二引擎**（`Windows.Media.Ocr`）：零模型零原生库、本机已有 zh-Hans 语言包、单图上限 10000px。代价是 TFM 要改成 `net8.0-windows10.0.19041.0`，且不提供置信度与字级框。**用户 2026-10-29 决定暂不做。**
  - [x] ~~PP-OCRv6 medium 档~~ —— 官方指标只比 small 高 1.6~2.1 个百分点，体积却要 +107MB（约 138MB），投入产出不成立。**用户 2026-10-29 决定不做。**
- **多显示器 DPI 精修**：进程仍为 System DPI Aware（无 manifest 声明 PerMonitorV2）；混合 DPI 双屏下截图坐标系在副屏非 100% 缩放时仍有理论偏差，若用户场景出现再上 `GetDpiForMonitor` 逐屏换算。
- **设置项与 `SettingsWindow` 自动同步**：当前新增配置需手改 `LoadAll` + handler，可考虑反射/约定自动绑定（收益中等，改动面大，缓行）。

## 长期 / 想法池

- 插件化模块架构（Todo / Clipboard / Capture / Translate 各模块已无横向依赖，具备拆分条件）
- 剪贴板历史搜索增强（当前为前缀包含匹配，可升级为 SQLite FTS5）
- 多语言 UI（当前硬编码中文）

## 不做

- ❌ GitHub Actions 相关的一切（用户账号红线，见 AGENTS.md）
- ❌ Paddle 系引擎回退（体积死局，见 AGENTS.md「发布体积约束」）
- ❌ Todo 存储从 Markdown 换回 JSON（多端同步是核心魔改点，`MarkdownStorage` 保留）
- ❌ 同步上传快捷键（用户决策 2026-10-29：显式通道只有右键菜单，快捷键与「复制即推」语义重叠，不加）

---

维护约定：**每个大版本更新本文件**——做完的挪进 CHANGELOG，新增计划写进对应时段，决策变更当场改写。
