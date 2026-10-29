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
