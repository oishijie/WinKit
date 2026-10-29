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

## 中期

- **翻译引擎补全**：DeepL / Azure 接入（`ITranslator` 接口已预留位），补设置项与测试。
- **OCR 引擎可插拔评估**：`IOcrProvider` 已抽象；关注更轻量模型（内存占用为关键指标），评估 Fun-ASR 之外的识别模型换挡空间。
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
