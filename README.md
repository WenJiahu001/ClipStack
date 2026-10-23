# ClipStack

Windows 剪贴板历史工具。下载 `ClipStack.exe` 后双击运行，无需安装 .NET、数据库或浏览器组件，无需管理员权限；文本、图片及缩略图加密保存在当前用户目录。

面向个人自用，保持轻量、实用。使用 MIT 许可证开源。

## 下载

前往 [GitHub Releases](https://github.com/WenJiahu001/ClipStack/releases/latest)，下载 `ClipStack-win-x64.zip`，解压后运行 `ClipStack.exe`。也可直接下载单文件 EXE。

支持 Windows 10 22H2 / Windows 11 x64。当前版本未签名，下载运行时可能出现未知发布者或 SmartScreen 提示。

## 使用

- 默认按 `Ctrl + Alt + V` 打开面板；方向键选择，回车尝试粘贴，`Ctrl + C` 仅复制，`Esc` 隐藏。
- 支持文本与截图、搜索、收藏备注、自动清理、托盘暂停及开机启动；首次靠右呼出，拖动后记住位置。
- 浅色界面，支持紧凑、标准、宽松三种密度；收藏备注原位编辑，最多 50 个可见字符。
- 可配置记住位置、跟随鼠标、屏幕中间或屏幕右侧，以及激活时回到列表首条。
- 托盘图标可关闭，窗口不占用任务栏；退出可使用托盘菜单或主界面「更多 → 退出」。详细说明见 [使用说明](docs/使用说明.md)。

## 开发与验证

需要 Windows x64 和 .NET 10 SDK；SDK 版本由 `global.json` 锁定。项目内 `.tools/dotnet/dotnet.exe` 可替代下列命令中的 `dotnet`。

```powershell
dotnet build src/ClipStack.App/ClipStack.App.csproj -c Release
dotnet run --project tests/ClipStack.Tests/ClipStack.Tests.csproj -c Release
.\scripts\Publish.ps1
.\scripts\Test-Release.ps1
```

发布产物位于 `artifacts/publish/win-x64/ClipStack.exe`，只有 EXE 是运行必需文件。SQLite 及 .NET 原生库随 EXE 打包，启动时自动释放到运行时的临时缓存。

发布脚本统一将 EXE、ZIP、SQL、截图及检查报告输出到 `artifacts/publish/win-x64`；构建前会停止该目录中正在运行的旧版。公开 ZIP 为 `ClipStack-win-x64.zip`，仅包含程序、使用说明、许可证和校验文件；本机诊断报告保留在本地。

实际第三方依赖仅 `Microsoft.Data.Sqlite` 及其 SQLitePCLRaw 传递依赖，锁文件随源码提交；不使用 UI 框架包、ORM 或额外测试框架。

发布版在「设置 → 开源组件与许可」中内置完整许可，不依赖旁边的文档文件。

## 工程结构

- `src/ClipStack.Core`：数据模型、DPAPI、HMAC 内容指纹、SQLite 存储、迁移及清理。
- `src/ClipStack.App`：WPF 视图、视图模型、Windows 剪贴板/快捷键/焦点/托盘集成。
  - `Controls`：独立行内备注控件，输入、验证和草稿状态。
  - `Services`：历史操作协调、可视区缩略图加载、采集保存队列、设置保存和启动参数。
  - `Platform`：剪贴板、图片解码、快捷键、粘贴、托盘、单实例和窗口位置等 Windows 适配。
  - `Diagnostics`：启动、窗口、性能、系统集成检查及示例预览，与正常启动流程分离。
- `tests/ClipStack.Tests`：存储与异常恢复回归测试，使用独立临时目录。
- `scripts`：图标生成、测试与自包含发布脚本。

`App.xaml.cs` 负责服务组装与应用生命周期；视图通过回调发出用户操作，历史操作统一交给 `HistoryController`。详细职责、异步约束及扩展方式见 [架构说明](docs/架构说明.md)。

数据库建表 SQL 位于 `src/ClipStack.Core/Migrations/001_initial.sql`，应用首次启动自动在事务内执行，用户无需操作数据库。

收藏备注字段的升级 SQL 为 `src/ClipStack.Core/Migrations/002_favorite_notes.sql`。已有 v1 数据库会自动备份并在事务内升级到 v2，备注同样使用 DPAPI 加密。

## 诊断入口

```powershell
ClipStack.exe --smoke-test "C:\temp\clipstack-smoke.json" --data-dir "C:\temp\clipstack-smoke-data"
ClipStack.exe --preview "C:\temp\clipstack-preview.png" --data-dir "C:\temp\clipstack-preview-data"
ClipStack.exe --preview-settings "C:\temp\clipstack-settings.png" --data-dir "C:\temp\clipstack-preview-data"
ClipStack.exe --panel-test "C:\temp\panel-checks.json" --data-dir "C:\temp\clipstack-panel-data"
ClipStack.exe --performance-test "C:\temp\performance-checks.json" --data-dir "C:\temp\clipstack-performance-data"
```

上述入口不监听或修改系统剪贴板、不注册快捷键，也不修改正式用户数据；省略 `--data-dir` 时创建临时诊断数据目录。`--preview` 的示例内容仅用于界面验收。诊断路径必须位于可写目录。

`--panel-test` 会短暂打开独立测试窗口，验证靠右呼出、位置保存、行内编辑和按钮行为，并渲染主面板及编辑态预览；预览只包含应用自身视觉树，场景阴影为示意渲染。需要交互式 Windows 桌面。每次使用新的测试目录，以验证首次呼出。

`--performance-test` 使用合成图片与文本，检查虚拟化、可视区加载、缩略图缓存容量、延迟原图预览、搜索取消和连续保存。发布脚本会执行该检查。历史查询的前后基准与内存取舍见 [性能优化报告](docs/性能优化报告.md)。

另有显式系统集成检查：`ClipStack.exe --integration-test "C:\temp\integration.json" --data-dir "C:\temp\integration-data"`。它临时修改系统剪贴板、注册诊断快捷键并打开自动关闭的粘贴测试窗口；退出时恢复原剪贴板，报告只记录布尔结果。无法安全备份原格式时终止检查。执行时请勿同时复制其他内容。

## 发布状态

首版兼容目标为 Windows 10 22H2 / Windows 11 x64。源代码及当前机器验证不等于完成全部兼容验收；准确结果见 [验收记录](docs/验收记录.md)。

当前发布包未签名，干净机器与多显示器兼容验证仍待补充。签名可降低未知发布者问题，但不能保证新程序在每台设备上均无 SmartScreen 提示。企业策略可能阻止未知应用。

## 许可证

本项目采用 [MIT](LICENSE) 许可证。第三方组件保留各自的许可证，见 [第三方组件](docs/第三方组件.md)。
