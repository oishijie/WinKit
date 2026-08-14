# AGENTS.md — WinKit

统一桌面助手：TodoList + Clipboard + 本地 OCR 翻译（常驻系统托盘）。

## 怎么跑起来
- 还原并构建：`dotnet restore` → `dotnet build -c Release`（输出 `bin/Release/net8.0-windows/`）。
- 本地 OCR 依赖 PaddleOCRSharp / Paddle.Runtime.win_x64，约 390MB 原生库，**必须先 `dotnet restore`**，否则 `VerifyLocalOcrRuntime` 构建目标报错。
- 无头跑 `dotnet WinKit.dll` 可抓启动期托管异常（GUI 崩溃在桌面上只表现为「打不开」）。

## 技术栈
- net8.0-windows、x64（PaddleOCR 原生库仅 x64，进程锁定 x64）、WPF + WinForms 混合。
- 本地 OCR：PaddleOCRSharp 6.2.0 + Paddle.Runtime.win_x64 3.3.0.1，CPU 推理，不联网。
- 翻译：默认 Google 免费端点；可在设置切到 OpenAI 兼容 `/chat/completions`。

## 目录与约定
- `Common/`：`App.xaml(.cs)` 入口、`SettingsManager`（配置中枢，单例持有 `AppSettings` + `SettingsChanged` 事件）、`SettingsWindow`（5 分区设置中心，改一项存一项、即时生效）、`TrayHelper`（托盘 + 焦点宿主）、`AppSettings`（持久化模型，普通属性 JSON 序列化）、`AutoStartHelper`。
- `Todo/`、`Clipboard/`、`Translate/`：三大功能模块。
- `Translate/TranslateModule.cs`：编排 OCR→翻译四条链路，注册 `Alt+S / Alt+D / Alt+Shift+S / Alt+Shift+F / Alt+A` 热键；持 `PaddleOcrProvider`、`ITranslator`（Google / OpenAI 实现）。
- 配置项增删：在 `AppSettings` 加属性即可自动持久化；需在设置窗 `LoadAll` 回填 + 对应 handler 写回 + `Save()`。
- 敏感信息（OpenAI API Key）使用 Windows DPAPI 加密存储，同机同用户自动解密，`SettingsManager` 保存时加密、加载时解密 + 明文自动迁移。
- 快捷键支持 UI 录制/保存/热重载：`SettingsWindow` 快捷键列表可点击录制新组合，保存后 `TranslateModule` 自动注销旧键、注册新键。
- OCR 模型支持热切换：设置窗口切换模型后 `PaddleOcrProvider.Reconfigure` 销毁旧引擎并后台预热新引擎，无需重启应用。

## 当前状态与下一步
- v2.1.0（当前）：快捷键 UI 可配置、剪贴板图片支持 + 截图功能、OCR 模型热切换、DPAPI 加密、Pin 钉住、SQLite 连接优化。
- 维护仓库 worldoi/WinKit；原项目 li5bo5/WinKit。
- 待办：DeepL / Azure 翻译接入（接口已预留，按需实现）。
