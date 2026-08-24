# 动态桌面 (Dynamic Wallpaper)

轻量、简洁的动态壁纸工具，支持把**视频 / 图片 / 网页(3D)** 渲染到桌面图标背后（对标 Lively Wallpaper 的精简实现）。

## 功能特性

- **多屏每屏独立壁纸**：顶部「应用到」选择目标屏（主屏 / 屏幕N / 所有屏幕），每张壁纸可放到不同显示器；分配持久化，重启自动恢复；支持显示器热插拔后自动重建。
- **视频壁纸**：基于 WPF 内置 `MediaElement`（系统解码，**零外部依赖**，轻量），支持 mp4/webm/mkv 等系统解码器能播放的格式。
- **图片壁纸**：铺满屏幕。
- **网页 / 3D 壁纸（可选模块）**：基于 WebView2（Chromium），支持 HTML/CSS/Canvas/WebGL 动画。独立程序集 `DynamicWallpaper.Web.dll`，缺失时主程序仍可正常编译运行，仅该功能不可用。
- **缩略图预览**：图片直接解码；视频复用 Windows Shell 生成的“首帧”缩略图；卡片角标显示已应用到哪些屏幕。
- **一键「设为」/「解除」**：卡片上未应用时显示蓝色「设为」；已应用到任意屏幕时显示红色「解除」，一键取消该壁纸在所有屏幕的显示。
- **右键菜单**：右键卡片可「设为到指定屏 / 所有屏幕 / 从库移除」。
- **缩略图预览**：图片直接解码；视频复用 Windows Shell 生成的“首帧”缩略图；加载过程在后台线程执行，避免卡界面；卡片角标显示已应用到哪些屏幕。
- **自动暂停**：前台全屏（游戏/视频）时暂停、笔记本用电池时暂停，释放 GPU/CPU。
- 静音开关、开机自启；关闭主窗口自动缩到系统托盘，壁纸继续运行。
- 资源管理器重启、显示器热插拔后自动恢复。

## 技术栈

- C# / .NET 8 / WPF（界面）+ WinForms（系统托盘）
- 视频解码使用 WPF 内置 `MediaElement`（零外部依赖，轻量）
- 网页壁纸使用 `Microsoft.Web.WebView2`（独立可选模块）
- Win32 `WorkerW` 注入（把渲染窗口放到图标背后）

## 构建要求

- Windows 10 / 11
- .NET 8 SDK + Windows Desktop 工作负载

```powershell
# 若未安装桌面工作负载
dotnet workload install windowsdesktop
```

## 构建与运行（主程序，离线即可）

主程序**零外部依赖**，可离线编译：

```powershell
cd DynamicWallpaper
dotnet build -c Release
dotnet run -c Release
# 或直接从 bin\Release\net8.0-windows\DynamicWallpaper.exe 启动
```

## 打包与分发

本工程**零外部依赖**，可离线发布为独立可运行程序。

### 已生成的可分发包

- 发布目录：`bin/Release/net8.0-windows/win-x64/publish/`（含 `DynamicWallpaper.exe` + 依赖清单，约 280KB）
- 压缩包：`DynamicWallpaper-win-x64.zip`（同上，便于传输）

> 这是**框架依赖**发布：把整个 `publish` 文件夹（或解压 zip 后）拷贝到目标 Windows 即可运行。

### 目标机运行要求

需要安装 **.NET 8 桌面运行时**（含 WPF）。未安装时启动会提示下载：

- 下载地址：<https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0>（选择 “.NET Desktop Runtime 8.x”）
- 或一行安装：`winget install Microsoft.DotNet.DesktopRuntime.8`

### 想要「单个 exe 文件 / 开箱即用（免装运行时）」

框架依赖版需目标机装运行时。若要**真正的一个 exe 文件**（自包含，内置 .NET 运行时，约 100–150MB），在有网络的机器上执行：

```powershell
# 自包含 + 单文件（无需目标机装运行时，但体积大）
cd DynamicWallpaper
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

# 或仅单文件但仍需目标机装 .NET 8 运行时（体积更小）
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

> 说明：单文件发布依赖 `Microsoft.NET.ILLink.Tasks` 包，本仓库开发沙箱无外网访问，故此处提供的是离线可成的**框架依赖文件夹版**；单文件/自包含版请在可联网的机器上按上面命令构建一次。

## 构建网页壁纸模块（可选，需联网一次）

网页/3D 壁纸依赖 WebView2 NuGet 包，需在有网络的机器上还原：

```powershell
cd DynamicWallpaper\DynamicWallpaper.Web
dotnet build -c Release
# 把生成目录下的 DynamicWallpaper.Web.dll 放到主程序 (DynamicWallpaper.exe) 同目录
```

放置后主程序启动时自动检测并启用「网页」按钮；目标机需已安装 WebView2 运行环境（Win10/11 通常自带，否则会从微软自动下载）。

## 使用说明

1. 启动后点「添加壁纸」或直接把视频/图片拖入窗口；点「网页」可输入网址或选择本地 HTML 作为网页壁纸。
2. 顶部「应用到」选择目标屏，在卡片上点「设为」即可；壁纸已在某屏运行时卡片会显示「解除」，一键取消。
3. 右键卡片可按屏精细设置或移除。
4. 「设置」里可调整：静音、全屏自动暂停、电池自动暂停、开机自启。
4. 关闭窗口不会停止壁纸，会缩到托盘；从托盘「退出」才真正结束。

## 从链接添加壁纸（在线导入）

点顶部「添加链接」，粘贴一个**视频/图片直链**（如 `https://.../xxx.mp4`），软件会把它下载到本机
`%LOCALAPPDATA%\DynamicWallpaper\Wallpapers\` 并加入壁纸库；勾选「下载后直接设为壁纸」可一步到位。
文件名/类型由链接与响应头推断（支持 mp4/webm/mkv 及常见图片格式）。

> 这非常适合接入像 uiuiui.in 这类视频壁纸站的**直链**：在浏览器打开壁纸详情页 → F12 → 网络 →
> 筛选 Media → 复制那条 `.mp4` 链接 → 粘贴进来即可。
>
> **合规提醒**：请确保你有权使用该链接内容，遵守来源站点条款与当地版权法规，仅用于个人使用。
> 本功能为“用户主动粘贴单个直链”，不内置任何全站批量抓取行为。

## 架构

```
UI (WPF 主窗口 / 设置 / 网页对话框 / 托盘)
  └─ Core (WallpaperManager 调度 + 全屏/电源/屏幕/配置)
       └─ Providers (Video / Image / Web，统一 IWallpaperProvider)
            └─ Desktop (WorkerWInjector：Win32 注入)
DynamicWallpaper.Web (可选：WebView2 网页/3D 壁纸，反射延迟加载)
```

新增壁纸类型只需实现 `IWallpaperProvider`（如 Web/3D），不动核心层；网页类型通过反射从可选模块加载，保持主程序轻量。

## 故障排查

**1. 缩略图不显示 / 一直是“视频/图片”占位文字**
- 视频缩略图来自 Windows Shell 的 `IShellItemImageFactory`（即资源管理器里看到的“首帧”），代码已确保在 STA 公寓线程调用。
- 若仍空白，通常是该视频编码/容器 Shell 不生成缩略图，属系统限制，会自动降级为占位文字，**不影响“设为”壁纸**。
- 图片缩略图为直接解码，一般都能显示。

**2. “添加链接”下载失败 / 提示“这是网页不是视频文件”**
- 根因：很多壁纸站（如 uiuiui.in）的视频**直链是 JS 动态加载的**，**详情页网址（HTML 页面）不能直接下载成视频**。
- 正确做法：浏览器打开壁纸详情页 → 按 **F12 → 网络(Network) → 筛选 Media** → 复制真正的 `.mp4` 链接，粘贴到“添加链接”。
- 下载已自动带上 `User-Agent` 和同源 `Referer`，可绕过多数防盗链；若仍 403/404，说明该直链需登录或已失效。
- 若下载到的其实是网页（Content-Type 为 text/html 或文件头是 `<html`），软件会拒绝并明确提示。

**3. 想让我帮忙定位下载问题**
- 所有下载/设置过程都会写日志到：`%LOCALAPPDATA%\DynamicWallpaper\app.log`
- 把该文件内容发我，即可看到具体状态码、Content-Type、异常，精准定位。

**4. 旧版 exe 无法覆盖更新**
- 若旧程序仍在运行（缩在托盘），其 exe 被系统锁定，无法直接覆盖。请先在托盘图标右键“退出”，再用新版 exe 目录启动。

## 已知限制与后续路线

- 网页/3D 壁纸需单独构建并放置 `DynamicWallpaper.Web.dll`，且本机有 WebView2 运行环境。
- WebView2 模块在本仓库外的有网环境构建（本机沙箱无 NuGet 访问，未做编译验证）。
- 视频缩略图依赖 Windows Shell 提取（已在 STA 线程调用，避免 COM 公寓错误）；极少数编码/文件可能取不到时降级为类型占位图。
