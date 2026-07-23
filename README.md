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

## Makefile 一键命令

推荐在 Linux/macOS/WSL 开发机上直接使用根目录 `Makefile`：

```bash
make doctor        # 检查 dotnet/java/Android SDK 环境
make install-deps  # 本地安装依赖(.NET/JDK/Android SDK/workload/restore)
make build         # 构建桌面服务端
make run           # 运行 Web UI，默认 http://127.0.0.1:8630
make build-apk     # 构建 Android APK，输出到 artifacts/apk
```

可覆盖常用变量：

```bash
make run PORT=9000
make build-apk CONFIG=Release ANDROID_API=35 ANDROID_BUILD_TOOLS=35.0.0
```

`make install-deps` 会优先复用系统已有的 `dotnet` / `java` / `sdkmanager`；缺失时会把 `.NET SDK`、JDK 17、Android cmdline-tools 安装到项目本地 `.tools/`，不污染系统全局环境。

## Windows Node.js 一键命令

Windows 原生环境推荐使用根目录 [build.mjs](file:///root/workspace/project/steamdownload/build.mjs)。先安装 Node.js LTS（官网安装包或 `winget install OpenJS.NodeJS.LTS`），然后执行：

```powershell
node .\build.mjs doctor        # 检查 dotnet/java/Android SDK 环境
node .\build.mjs install-deps  # 本地安装依赖(.NET/JDK/Android SDK/workload/restore)
node .\build.mjs build         # 构建桌面服务端
node .\build.mjs run --port=8630
node .\build.mjs build-apk     # 构建 Android APK，输出到 artifacts\apk
node .\build.mjs clean
```

`build.mjs` 无 npm 依赖，会优先复用系统已有的 `dotnet` / `java` / `sdkmanager`；缺失时会把 `.NET SDK`、Temurin JDK 17、Android cmdline-tools 安装到项目本地 `.tools\`。

## 桌面运行（已在 Linux 验证）

```bash
make run
# 或者不用 Makefile:
dotnet run --project src/SteamDl.Server   # 浏览器打开 http://127.0.0.1:8630
```

## 构建 Android APK

需要:.NET 9 SDK、JDK 17、Android SDK（装过 Android Studio 即有）。

```bash
make install-deps
make build-apk
# 或者不用 Makefile:
dotnet workload restore src/SteamDl.Android
dotnet publish src/SteamDl.Android -c Release
```

侧载安装后首次启动会跳转"所有文件访问"授权页（写 /sdcard/Download 需要），
授权后返回应用即可使用。下载目录默认 `/sdcard/Download/steamdl/app_<appid>`。

## 使用说明

- 粘贴商店链接 / steam:// 链接 / AppID → 解析 → 选择账号与平台 → 下载。
- DLC:账号拥有即随本体一起下载;单独补 DLC 填其 Depot ID（steamdb.info 可查）。
- 登录只保存 refresh token（DD 的 account.config 机制），密码不落盘。
- Steam Guard:可直接在 Steam 手机 App 点确认，或在页面输入框提交令牌。
- 拷贝到 PC:将 `app_<appid>` 内的文件放入 `Steam\steamapps\common\<游戏安装目录名>`，
  Steam 里点"安装"会自动发现现有文件并只校验补差。

## 注意

- 本项目引擎部分为 GPL-2.0，若公开分发 APK 需同样以 GPL 开源。
- 大陆网络直连 Steam CM 服务器可能失败（api.steampowered.com 不可达），
  与代码无关，需自行解决网络问题。
