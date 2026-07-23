# SteamDl — Steam 下载器（桌面 + Android APK）

在手机/PC 上下载 Steam 游戏文件（Windows 版 depot），用于拷贝到 PC 安装。
下载引擎复用 [DepotDownloader](https://github.com/SteamRE/DepotDownloader)
（GPL-2.0，源码 vendored 于 `external/DepotDownloader/`，commit `1e8e20c`）。

## 目录结构

```
SteamDl.sln
src/
├── SteamDl.Core/       # 引擎:DD 源码编入 + Console 中继(交互转发) + 任务状态机 + HTTP API
│   └── wwwroot/        # Web UI 单页(嵌入资源)
├── SteamDl.Server/     # 桌面/服务器入口(net9.0),Windows/Linux/macOS 直接跑
└── SteamDl.Android/    # APK 壳(net9.0-android):前台服务承载引擎 + WebView 加载 UI
external/
└── DepotDownloader/    # 上游源码,零修改(Program/Ansi 等由 Core/Shims 替身实现替代)
```

## Node.js 一键命令

所有平台统一使用根目录 [build.mjs](file:///root/workspace/project/steamdownload/build.mjs)。先安装 Node.js LTS：Windows 可用官网安装包或 `winget install OpenJS.NodeJS.LTS`，Linux/macOS 可用系统包管理器或 Node.js 官网安装包。

```bash
node build.mjs doctor        # 检查 dotnet/java/Android SDK 环境
node build.mjs install-deps  # 本地安装依赖(.NET/JDK/Android SDK/workload/restore)
node build.mjs build         # 构建桌面服务端
node build.mjs run --port=8630
node build.mjs build-apk     # 构建 Android APK，输出到 artifacts/apk
node build.mjs clean
```

可覆盖常用参数：

```bash
node build.mjs run --port=9000
node build.mjs build-apk --config=Release --android-api=35 --android-build-tools=35.0.0
node build.mjs build-apk --nuget-source=https://api.nuget.org/v3/index.json
```

`build.mjs` 无 npm 依赖，会优先复用系统已有的 `dotnet` / `java` / `sdkmanager`；缺失时会把 `.NET SDK`、Temurin JDK 17、Android cmdline-tools 安装到项目本地 `.tools/`。NuGet 源默认使用 `https://api.nuget.org/v3/index.json`，如果你的网络需要镜像，可以追加 `--nuget-source=<URL>` 或设置环境变量 `NUGET_SOURCE`。

## 桌面运行（已在 Linux 验证）

```bash
node build.mjs run --port=8630
# 或者直接运行 .NET 项目:
dotnet run --project src/SteamDl.Server   # 浏览器打开 http://127.0.0.1:8630
```

## 构建 Android APK

需要:.NET 9 SDK、JDK 17、Android SDK（装过 Android Studio 即有）。

```bash
node build.mjs install-deps
node build.mjs build-apk
# 或者直接运行 .NET 命令:
dotnet workload restore src/SteamDl.Android --source https://api.nuget.org/v3/index.json
dotnet publish src/SteamDl.Android -c Release
```

侧载安装后首次启动会跳转"所有文件访问"授权页（写 /sdcard/Download 需要），
授权后返回应用即可使用。下载目录默认 `/sdcard/Download/steamdl/app_<appid>`。

## 使用说明

- 粘贴商店链接 / steam:// 链接 / AppID → 解析。
- 账号模式可二选一：勾选匿名下载（仅限免费/服务器内容），或输入 Steam 用户名后点击“登录”，登录成功后再下载。
- DLC:账号拥有即随本体一起下载;单独补 DLC 填其 Depot ID（steamdb.info 可查）。
- 登录成功后会保存 Steam refresh token 到应用数据目录的 `SteamDl/account.config`，后续同一账号可免密码复用；密码不落盘。
- Steam Guard:登录或下载过程需要验证时，可直接在 Steam 手机 App 点确认，或在页面输入框提交令牌。
- 保存目录可点击“选择”调用系统目录选择器：Windows 使用资源管理器目录选择，Android 使用系统文件管理器；如果选择器不可用或路径不可写，可继续手动输入。保存目录会持久化到应用配置中。
- 下载完成后默认目录会尽量使用 Steam appinfo 的安装目录名（即 `steamapps/common/<游戏安装目录名>` 中的那一段）；如果 Steam 元数据取不到，才回退到 `app_<appid>`。
- Android 可尝试选择外置存储或 U 盘目录；是否可直接写入取决于系统是否把该目录暴露为可写真实路径（如 `/storage/XXXX-XXXX/...`）以及“所有文件访问”授权。若文件管理器只返回 SAF URI 且无法转换为真实路径，则当前下载引擎无法直接写入，需要手动输入可写挂载路径或先下载到手机存储后再复制。
- 下载完成后可点击“打开所在位置”；若当前平台/文件管理器不支持自动打开，可按页面显示的目录手动进入。
- 拷贝到 PC:将下载目录内的文件放入 Steam 库的 `Steam\steamapps\common\<游戏安装目录名>`，
  Steam 里点"安装"会自动发现现有文件并只校验补差。

## 注意

- 本项目引擎部分为 GPL-2.0，若公开分发 APK 需同样以 GPL 开源。
- 大陆网络直连 Steam CM 服务器可能失败（api.steampowered.com 不可达），
  与代码无关，需自行解决网络问题。
