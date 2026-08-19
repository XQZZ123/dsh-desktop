# DSH 桌面版（DshDesktop）

把 DeepSeek Harness 的网页版包成一个**真正的桌面应用**：双击一个 EXE，自动启动后端（`dsh web`），并打开一个**原生桌面窗口**（内嵌 WebView2），不再依赖浏览器标签页。

## 特性

- **一键启动后端**：若 `127.0.0.1:3080` 已有后端在运行则直接连接；否则自动隐藏启动 `node …/dsh web --host 127.0.0.1 --port 3080`。
- **原生桌面窗口**：内嵌 WebView2（Chrome 内核）承载 DSH 界面，登录状态/会话数据持久化。
- **自动兜底**：若 WebView2 不可用，自动改用 Edge 的 `--app` 桌面模式打开，仍然是一个独立桌面窗口。
- **托盘常驻**：最小化到托盘，随时“显示窗口 / 退出”。
- **干净退出**：关闭窗口时询问是否同时关闭后端（仅在本次由它启动时；连接已有后端时不打扰）。
- **完全离线构建**：只用 Windows 自带的 `.NET Framework csc.exe` 与本机已有的 WebView2 程序集，不需要网络、不需要 dotnet SDK、不需要 npm。

## 快速开始

```powershell
# 1) 进入项目目录（clone 后）
cd dsh-desktop

# 2) 构建
powershell -ExecutionPolicy Bypass -File build.ps1

# 3) （可选）桌面创建快捷方式
powershell -ExecutionPolicy Bypass -File build.ps1 -MakeShortcut

# 4) 自检（可选，检查 node / dsh 库 / 端口 / WebView2）
.\bin\DshDesktop.exe --check

# 5) 运行
.\bin\DshDesktop.exe
```

> 不想自己构建？从 **GitHub Releases** 下载预编译的 `DshDesktop-bin.zip`，解压后直接双击 `bin\DshDesktop.exe` 即可（需本机有 WebView2 运行时，Win11 / 新版 Win10 自带）。

双击 `bin\DshDesktop.exe` 即可使用。窗口底部状态栏显示后端连接状态；系统托盘有图标。

## 配置

配置文件位于 `%APPDATA%\DshDesktop\config.json`（首次运行自动生成）。可改项：

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `nodePath` | `""` | 指定 node.exe；留空则自动探测（PATH、常见安装目录） |
| `dshLib` | `""` | 指定 dsh 的 `lib/bin.js` 路径；留空则自动探测 npx 缓存 / `dsh` 命令 |
| `dshHome` | `""` | 指定 `DSH_HOME`；留空默认 `%USERPROFILE%\.dsh` |
| `host` | `127.0.0.1` | 后端监听/连接地址 |
| `port` | `3080` | 后端端口 |
| `startTimeoutSeconds` | `90` | 等待后端就绪的最长时间 |
| `killBackendOnExit` | `true` | 关闭窗口时提示是否同时关闭后端 |
| `edgeFallback` | `true` | WebView2 失败时是否回退 Edge 桌面模式 |
| `title` | `DeepSeek Harness` | 窗口标题 |
| `userDataFolder` | `""` | WebView2 用户数据目录；默认 `%APPDATA%\DshDesktop\WebView2` |

> 注意：JSON 不支持 `//` 注释。若 `dshHome` 留空，程序会保证把 `DSH_HOME` 传给后端进程（沿用你当前的 `.dsh` 目录，会话数据与网页版一致）。

## 自检（--check）

`DshDesktop.exe --check` 会依次检查：配置文件位置、node.exe、dsh 库、Edge、当前端口是否在线、WebView2 环境、进程“启动-等待-关闭”能力，并汇总结果。适合排障。

## 常见问题

- **WebView2 初始化失败**：程序会自动回退 Edge 桌面模式。原因通常是本机 WebView2 运行时缺失或加载器架构不匹配（构建脚本会提示 loader 架构）。确保 Windows 更新/Edge 已安装最新 WebView2 运行时。
- **后端端口被占用**：说明已有 DSH 后端在运行，程序直接连接（不重复启动）。
- **想换端口**：在配置里改 `port`，重启程序即可（同时会以该端口启动后端）。
- **日志**：后端输出写入 `%APPDATA%\DshDesktop\backend.log`。

## 目录结构

```
dsh-desktop/
├─ src/                  C# 源码（C# 5，.NET Framework 4.8）
│  ├─ Program.cs         入口 / 单实例 / --check 自检
│  ├─ Config.cs          配置读写（%APPDATA%\DshDesktop\config.json）
│  ├─ Locator.cs         node / dsh 库 / Edge 离线定位
│  ├─ Backend.cs         后端进程启动 / 探测 / 停止
│  └─ MainForm.cs        主窗口（WebView2 + Edge 回退 + 托盘）
├─ build.ps1             离线构建脚本（csc 编译 + 复制 WebView2 程序集）
├─ make-icon.ps1         鲸鱼图标生成脚本
├─ dsh-desktop.config.json  配置样例
├─ app.manifest          DPI 感知 manifest
├─ assets/               图标素材
├─ LICENSE               MIT 许可证
└─ bin/                  构建产物（DshDesktop.exe + WebView2 DLL，不入库）
```

## 图标

EXE、任务栏与托盘使用 **DeepSeek 鲸鱼 logo（黑色 + 透明背景）**：脚本从 DSH 网页前端的 `favicon.svg`
里解析鲸鱼路径，用 WPF 栅格化，渲染为「黑色鲸鱼 + 透明背景」，打包成 16/32/48/64/128/256 多尺寸
`assets\app.ico` 并在编译时通过 `/win32icon` 内嵌进 EXE。构建时若找不到 favicon.svg 会自动跳过图标生成。

想换鲸鱼颜色（例如适配深色任务栏改成白色）：`powershell -ExecutionPolicy Bypass -File make-icon.ps1 -WhaleColor '#FFFFFF'`，再重跑 `build.ps1`。

## 技术说明

- 编译：Windows 自带的 `.NET Framework csc.exe`（C# 5，.NET Framework 4.8 自带），无需 dotnet SDK。
- 高 DPI 清晰：通过内嵌 `app.manifest`（`dpiAwareness=PerMonitorV2`）+ 启动时 `SetProcessDpiAwarenessContext` 双保险，125%/150% 缩放下仍按物理像素渲染，不模糊。
- WebView2 程序集：`Microsoft.Web.WebView2.Core.dll` / `WinForms.dll` / `WebView2Loader.dll` 由 `build.ps1` 自动从本机已安装应用（Microsoft 365、WSL、NuGet 缓存等）搜索最新版本离线复制——无需 NuGet 网络；也可用 `-WebView2Core / -WebView2WinForms / -WebView2Loader` 参数显式指定来源。
- 后端：`node <dsh lib>/lib/bin.js web --host 127.0.0.1 --port <port>`，隐藏窗口运行，输出重定向到日志。
- 若在别的机器部署：把 `bin` 整个目录拷走即可；WebView2 运行时需在该机存在（Win11 / 新版 Win10 自带）。
