# 动态桌面 (Dynamic Wallpaper)

轻量、简洁的动态壁纸工具，支持把**视频 / 图片 / GIF / 网页(3D)** 渲染到桌面图标背后（对标 Lively Wallpaper 的精简实现）。

> 适用于 Windows 10 / 11。视频与网页壁纸依赖 WebView2 运行环境；图片 / GIF 壁纸为纯本地渲染，无外部依赖。

## 功能特性

- **多屏每屏独立壁纸**：顶部「应用到」选择目标屏（主屏 / 屏幕N / 所有屏幕），每张壁纸可放到不同显示器；分配持久化，重启自动恢复；支持显示器热插拔后自动重建。
- **视频壁纸**：基于 **WebView2 + HTML5 video** 渲染（Win11 24H2/25H2 raised desktop 兼容方案 —— WPF `MediaElement` 在该环境下无法被 DWM 合成到桌面，WebView2 内容可被真实合成）。系统无法硬解 HEVC/H.265 等编码时，自动用**内嵌 ffmpeg 转码**为 H.264 再播放，避免黑屏。
- **图片壁纸**：支持三种适应方式 —— 铺满裁剪(fill) / 完整显示(fit) / 原始居中(center)。动→静切换时由 `StaticFadeProvider` 提供 **300ms 渐入过渡**，视觉无缝、不闪旧图。
- **GIF 壁纸**：`GifProvider` 自绘循环播放（避免 `MediaElement` 对 GIF 循环不稳定）。
- **网页 / 3D 壁纸（可选模块）**：基于 WebView2（Chromium），支持 HTML/CSS/Canvas/WebGL 动画。独立程序集 `DynamicWallpaper.Web.dll`，缺失时主程序仍可正常编译运行，仅该功能不可用。
- **在线壁纸（内置多源爬虫）**：内置 netbian、3gbizhi（3G壁纸）、Wallhaven 三个来源，支持分类 / 分辨率筛选、分页加载、一键下载到本地库。详见「在线壁纸」一节。
- **缩略图预览**：图片直接解码；视频复用 Windows Shell 生成的“首帧”缩略图；在线壁纸抓取其远程缩略图；卡片角标显示已应用到哪些屏幕。
- **一键「设为」/「解除」**：卡片上未应用时显示「设为桌面」；已应用到任意屏幕时显示「解除壁纸」，一键取消该壁纸在所有屏幕的显示。
- **右键菜单**：右键卡片可「设为到指定屏 / 所有屏幕 / 从库移除」。
- **状态栏切换提示**：底部状态栏实时显示切换状态（切换中 / 成功并显示壁纸信息 / 失败并显示原因），并用竖线与前置信息分隔。
- **自动暂停**：前台全屏（游戏/视频）时暂停、笔记本用电池时暂停，释放 GPU/CPU。
- 静音开关、开机自启（`--silent` 静默启动，仅驻留托盘不弹主窗）；关闭主窗口自动缩到系统托盘，壁纸继续运行。
- 资源管理器重启、显示器热插拔后自动恢复壁纸。

## 技术栈

- C# / .NET 8 / WPF（界面）+ WinForms（系统托盘）
- 视频 / 网页渲染：`Microsoft.Web.WebView2`（v1.0.4129.50，已锁定实验验证版本）
- 视频转码：内嵌 **ffmpeg**（编译为资源，运行时按需解压到程序目录，亦会优先复用本机已有 ffmpeg）
- 中文壁纸站 GBK 解码：`System.Text.Encoding.CodePages`
- Win32 `WorkerW` 注入（把渲染窗口放到图标背后）

## 构建要求

- Windows 10 / 11
- .NET 8 SDK + Windows Desktop 工作负载

```powershell
# 若未安装桌面工作负载
dotnet workload install windowsdesktop
```

- 构建视频转码需要一个 `ffmpeg.exe` 放在**项目根目录**（约 60MB，不纳入 git）：csproj 将其作为 `EmbeddedResource` 嵌入 exe，缺失会导致 CS1566 编译错误。可从 <https://ffmpeg.org> 或 `winget install ffmpeg` 获取后放入根目录；若不需要 HEVC 自动转码，也可移除该 `EmbeddedResource` 项。

## 构建与运行（单文件发布，默认已配置）

csproj 已默认开启单文件发布（`PublishSingleFile=true`、`SelfContained=false`、`RuntimeIdentifier=win-x64`、`IncludeNativeLibrariesForSelfExtract=true`），ffmpeg 作为内嵌资源，因此构建产物就是**一个 exe 文件**：

```powershell
cd DynamicWallpaper
dotnet publish -c Release -r win-x64
# 产物：bin/Release/net8.0-windows/win-x64/publish/DynamicWallpaper.exe（约 62MB，已含 ffmpeg + WebView2Loader）
```

- 直接运行 `publish/DynamicWallpaper.exe` 即可；目标机需安装 **.NET 8 桌面运行时**（含 WPF），未安装时启动会提示下载。
- 若要让目标机**免装 .NET 运行时**，改为自包含发布（体积更大，约 150MB+，需联网还原运行时包）：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true
```

## 打包与分发

- 默认交付物为**单一 exe 文件**（框架依赖，需目标机装 .NET 8 桌面运行时）。
- exe 已内嵌 ffmpeg 与 WebView2Loader.dll，拷贝到任意目录即可运行，**无需外置这些依赖文件**。
- **视频 / 网页壁纸**需要目标机已安装 **WebView2 Runtime**（Win10/11 通常自带，否则会从微软自动下载）。静态 / 图片 / GIF 壁纸不依赖 WebView2。
- 可选网页/3D 壁纸需把独立编译的 `DynamicWallpaper.Web.dll` 放到 exe 同目录。

## 数据存储位置（全部在 exe 根目录）

单文件发布时运行时会被解压到 `C:\Users\<user>\AppData\Local\Temp\.net\...`，但本程序通过 `Environment.ProcessPath` 解析 exe **真实所在目录**，所有数据都固定在 exe 旁边，不会污染系统临时目录：

| 内容 | 路径 |
|------|------|
| 日志 | `<exe目录>\app.log` |
| 配置 | `<exe目录>\config.json` |
| 壁纸库 / 在线下载 | `<exe目录>\Wallpapers\` |
| WebView2 用户数据 | `<exe目录>\WebView2\` |
| 可选网页模块 | `<exe目录>\DynamicWallpaper.Web.dll` |

> 直接把整个 exe 所在文件夹（含 Wallpapers、WebView2 子目录）整体拷贝 / 移动即可迁移，无需重装。

## 使用说明

1. 启动后点「添加壁纸」或直接把视频/图片/GIF 拖入窗口；点「网页」可输入网址或选择本地 HTML 作为网页壁纸。
2. 顶部「应用到」选择目标屏，在卡片上点「设为桌面」即可；壁纸已在某屏运行时卡片会显示「解除壁纸」，一键取消。
3. 右键卡片可按屏精细设置或移除。
4. 「设置」里可调整：静音、全屏自动暂停、电池自动暂停、开机自启、壁纸适应方式（铺满/完整/居中）。
5. 关闭窗口不会停止壁纸，会缩到托盘；从托盘「退出」才真正结束。

## 从链接添加壁纸（在线直链导入）

点顶部「添加链接」，粘贴一个**视频/图片/GIF 直链**（如 `https://.../xxx.mp4`），软件会把它下载到本机 `<exe目录>\Wallpapers\` 并加入壁纸库；勾选「下载后直接设为壁纸」可一步到位。
文件名/类型由链接与响应头推断（支持 mp4/webm/mkv 及常见图片、GIF 格式）。

> 这非常适合接入像 uiuiui.in 这类视频壁纸站的**直链**：在浏览器打开壁纸详情页 → F12 → 网络 → 筛选 Media → 复制那条 `.mp4` 链接 → 粘贴进来即可。
>
> **合规提醒**：请确保你有权使用该链接内容，遵守来源站点条款与当地版权法规，仅用于个人使用。本功能为“用户主动粘贴单个直链”，不内置任何全站批量抓取行为。

## 在线壁纸（内置多源爬虫）

除手动粘贴直链外，程序内置了三个中文/国际壁纸站的爬虫，无需离开软件即可浏览并下载：

- **netbian**（netbian.com）：15 个分类（最新 / 4K / 风景 / 美女 / 动漫 / 游戏 / 影视 / 明星 / 汽车 / 动物 / 植物 / 美食 / 节日 / 简约 / 日历），其中「动态」分类提供 1920x1080 高清 GIF 动画。
- **3gbizhi（3G壁纸）**（3gbizhi.com）：18 个分类（美女 / 风景 / 动漫 / 明星 / 汽车 / 影视 / 游戏 / 植物 / 动物 / 节日 / 简约 / 唯美 / 车模 / 创意 / 动态 / 可爱 / 精美 / 体育），「动态」分类提供 GIF。
- **Wallhaven**（wallhaven.cc）：按内容分类（General / Anime / People / SFW / Sketchy）与分辨率（含 21:9 超宽屏与 4K/8K）筛选。

使用方式：切到「在线」标签页 → 选择来源 → 勾选分类 / 分辨率 → 点「加载更多」翻页浏览 → 卡片上点下载按钮即可把壁纸保存到本地 `Wallpapers\` 并加入壁纸库，之后像本地壁纸一样「设为桌面」。

> 爬虫仅供个人本地使用，仅使用 .NET 内置 `HttpClient` + 正则解析，请遵守各站点服务条款与版权规定，勿用于批量盗链或商业分发。

## 架构

```
UI (WPF 主窗口 / 设置 / 网页对话框 / 在线标签页 / 托盘)
  └─ Core (WallpaperManager 调度 + 全屏/电源/屏幕/配置 + AppPaths + VideoCodecDetector)
       ├─ Providers (Video[WebView2] / Image / StaticFade / Gif / Web[可选]，统一 IWallpaperProvider)
       │     └─ Desktop (WorkerWInjector：Win32 注入到图标背后)
       └─ OnlineWallpaperCrawler (netbian / 3gbizhi / wallhaven 三源抓取)
DynamicWallpaper.Web (可选：WebView2 网页/3D 壁纸，反射延迟加载)
```

新增壁纸类型只需实现 `IWallpaperProvider`（如 Web/3D），不动核心层；网页类型通过反射从可选模块加载，保持主程序轻量。视频类型通过 `VideoProvider` 复用同一 WebView2 环境。

## 故障排查

**1. 缩略图不显示 / 一直是“视频/图片”占位文字**
- 视频缩略图来自 Windows Shell 的 `IShellItemImageFactory`（即资源管理器里看到的“首帧”），代码已确保在 STA 公寓线程调用。
- 若仍空白，通常是该视频编码/容器 Shell 不生成缩略图，属系统限制，会自动降级为占位文字，**不影响“设为”壁纸**。
- 图片缩略图为直接解码，一般都能显示。

**2. “添加链接”下载失败 / 提示“这是网页不是视频文件”**
- 根因：很多壁纸站（如 uiuiui.in）的视频**直链是 JS 动态加载的**，详情页网址（HTML 页面）不能直接下载成视频。
- 正确做法：浏览器打开壁纸详情页 → 按 **F12 → 网络(Network) → 筛选 Media** → 复制真正的 `.mp4` 链接，粘贴到“添加链接”。
- 下载已自动带上 `User-Agent` 和同源 `Referer`，可绕过多数防盗链；若仍 403/404，说明该直链需登录或已失效。
- 若下载到的其实是网页（Content-Type 为 text/html 或文件头是 `<html`），软件会拒绝并明确提示。

**3. 视频壁纸无反应 / 黑屏 / 提示缺少 WebView2**
- 视频（及网页）壁纸依赖 **WebView2 Runtime**：请确认目标机已安装（Win10/11 通常自带，或到微软官网安装 Microsoft Edge WebView2 Runtime）。
- 若提示缺少 `WebView2Loader.dll` 或 VC++ 运行库：安装 **VC++ 2015-2022 Redistributable** 并确保程序目录完整（单文件 exe 会自动解压该 dll 到临时目录）。
- 缺少 WebView2 时：静态/图片壁纸会自动降级走系统 API 仍可用；仅视频/网页壁纸不可用。
- HEVC/H.265 视频若黑屏：软件会用内嵌 ffmpeg 自动转码为 H.264（需 ffmpeg，已内嵌；若使用本机 ffmpeg 缺编码器会回退到内嵌版）。

**4. 想让我帮忙定位问题**
- 所有下载/设置/切换过程都会写日志到：`<exe目录>\app.log`（不是系统目录）
- 把该文件内容发我，即可看到具体状态码、Content-Type、异常，精准定位。

**5. 旧版 exe 无法覆盖更新**
- 若旧程序仍在运行（缩在托盘），其 exe 被系统锁定，无法直接覆盖。请先在托盘图标右键“退出”，再用新版 exe 目录启动。

## 已知限制与后续路线

- 视频 / 网页壁纸需目标机安装 WebView2 Runtime；未安装时该两类不可用（静态/图片/GIF 不受影响，静态会自动降级系统 API）。
- 网页/3D 壁纸需单独构建并放置 `DynamicWallpaper.Web.dll`，且本机有 WebView2 运行环境。
- 在线爬虫仅供个人本地使用，遵守各站点条款与版权；站点改版可能导致解析失效，需同步更新 `OnlineWallpaperCrawler`。
- 视频缩略图依赖 Windows Shell 提取（已在 STA 线程调用）；极少数编码/文件可能取不到时降级为类型占位图。
- 单文件发布下，WebView2 用户数据固定在 exe 根目录 `WebView2\`，不会落到 C 盘临时目录；若需彻底清理，删除该文件夹即可（下次启动自动重建）。
