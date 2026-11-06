# Codex Local Project Recovery (CLPR)

中文 Windows 桌面工具，用于从已知正常的本地快照恢复 Codex 的项目列表与聊天归属。**非 OpenAI 官方工具，不进行账号切换，不改变云端授权。**

## 下载与使用

首次使用请阅读 **[完整操作指南](docs/USER_GUIDE.md)**，包含切号前保存、切号后检查、预览恢复、回滚和常见问题。下载包内也附有 `使用指南.md`。

在本仓库 **Releases** 下载 `CLPR-1.0.0-Windows-x64.zip`，解压后双击 `Clpr.Desktop.exe`。免安装，自带 .NET 运行时，适用于 Windows x64。

- **切号前**：在「状态与备份」确认项目正常，点击「保存当前快照」。保存后即可正常切号，不需要立即恢复。
- **切号后项目消失**：完全退出 Codex，进入「恢复项目」，选择之前保存的快照 → 生成预览 → 检查通过后点击亮起的「恢复项目」。
- **需要撤销**：进入「操作记录」，选择可回滚的操作并确认。

桌面版每次启动默认使用当前 Windows 用户的 `%USERPROFILE%\.codex`。如已迁移 Codex 数据，请点击「选择目录」。不会默认进入开发演示目录，也不使用桌面启动参数覆盖目录。

未预览或检查不通过时恢复按钮禁用。更换快照、目录或检测到状态文件变化，旧预览失效。恢复前自动备份；Codex 仍在运行、哈希变化、目标非空或结构不支持时停止写入。项目数量和聊天归属数量全部动态读取。

## 数据与隐私

程序无需联网，不调用 AI API，不上传快照或遥测，不读取 `auth.json`，不修改登录凭据、配置、SQLite 业务行或源码。

快照与操作备份仅保存在本机 `%LOCALAPPDATA%\CLPR`。备份可能包含项目路径与历史数据库，导出的计划包含本地路径，请妥善保管，不要提交到公开仓库。发布包与源码仅包含程序和人工合成测试样例，不包含开发者或用户的真实 Codex 状态、快照、账号信息、日志、截图、PDB 或个人路径。

## 支持范围

恢复只修改全局 JSON 的四组字段：`local-projects`、`project-order`、`app-server-project-id-by-legacy-project-id-by-host`、`thread-project-assignments`。

当前 V1 适配器要求项目含名称和根目录数组、顺序完整、聊天归属为有效本地关联。非空目标不自动覆盖或合并；未知结构停止自动恢复。SQLite 仅备份，不验证数据库 ID。此工具使用应用内部数据结构，未来 Codex 更新可能需要适配。文件级成功后请打开 Codex 检查项目实际显示。

## 开发

需要 .NET 10 SDK。WPF 桌面版与 CLI 共用核心引擎，无第三方 NuGet 依赖。

```powershell
dotnet build CLPR.slnx -c Release
dotnet run --project tests/Clpr.Tests -c Release
pwsh -File tools/Build-Release.ps1
```

38 项核心测试使用临时合成 profile，覆盖动态数量、无效结构、悬空引用、并发变化、进程阻止、备份、写后验证与回滚。不会写入真实 Codex profile。

CLI 帮助：`Clpr.Cli.exe`。CLI 可用 `--home` 指定目录，默认遵循 `CODEX_HOME`，未设置时使用用户 `.codex`；桌面版使用前述默认目录与手动选择规则。

发布物未代码签名，附 SHA-256 校验文件。MIT License。
