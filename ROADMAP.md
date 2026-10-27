# ROADMAP — WinKit

统一桌面助手：TodoList + Clipboard + 截图标注 + 本地 OCR 翻译（常驻托盘，net8.0-windows x64，WPF + WinForms）。
维护仓库 [oishijie/WinKit](https://github.com/oishijie/WinKit)，上游 [li5bo5/WinKit](https://github.com/li5bo5/WinKit)，协议 **AGPL-3.0**。

本文件是路线图：规划方向、排序优先级、记录决策。已完成项沉淀进 `CHANGELOG.md`，协作与架构约定见 `AGENTS.md`。

---

## 当前基线（v2.1.3 + Unreleased）

已落地：四大功能模块（TodoList / 剪贴板历史 / 截图标注 / 本地 OCR 翻译）、快捷键全面可配置（单一数据源 `HotkeyCatalog`）、OCR 引擎 ONNX Runtime（分发包 44MB）、识别历史、深色反色、单实例保护、DPAPI 加密。
上游移植：FastClipboard、待办回收站、图片缓存生命周期清理（详见 `CHANGELOG.md` Unreleased 段）。

## 近期（下一版目标）

### 1. 待办回收站收尾
- [ ] 设置中心「待办」分区暴露「回收站保留天数」（`RecycleBinRetentionDays` 已就位，缺 UI）
- [ ] 回收站窗口支持 Shift 多选批量还原 / 删除

### 2. 图片缓存清理可视化
- [ ] 设置中心「剪贴板」分区暴露保留天数与容量上限（字段已就位：`ClipboardImageRetentionDays` / `ClipboardImageMaxStorageMB`）
- [ ] 状态显示当前 images 目录占用（进入设置时计算一次即可）

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

---

维护约定：**每个大版本更新本文件**——做完的挪进 CHANGELOG，新增计划写进对应时段，决策变更当场改写。
