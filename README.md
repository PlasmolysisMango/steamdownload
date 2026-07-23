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

## 桌面运行（已在 Linux 验证）

```bash
dotnet run --project src/SteamDl.Server   # 浏览器打开 http://127.0.0.1:8630
```

## 构建 Android APK

需要:.NET 9 SDK、JDK 17、Android SDK（装过 Android Studio 即有）。

```bash
dotnet workload install android                # 首次一次即可
dotnet publish src/SteamDl.Android -c Release  # 产物: bin/Release/net9.0-android/*-Signed.apk
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
