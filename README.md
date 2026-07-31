# SteamDl — Steam 下载器（MAUI UI + .NET 引擎）

在手机/PC 上下载 Steam 游戏文件（Windows 版 depot），用于拷贝到 PC 安装。
下载引擎复用 [DepotDownloader](https://github.com/SteamRE/DepotDownloader)
（GPL-2.0，源码 vendored 于 `external/DepotDownloader/`，commit `1e8e20c`）。

## 架构

UI 为 .NET MAUI Blazor Hybrid 跨平台界面，下载引擎为 .NET 自包含 sidecar 子进程，
两者通过 `http://127.0.0.1:8630` 的 `/api` 契约进程间通信：

```
SteamDl.sln
src/
├── SteamDl.Core/       # 引擎:DD 源码编入 + Console 中继(交互转发) + 任务状态机 + /api HTTP 服务
├── SteamDl.Server/     # 引擎 sidecar 入口(net9.0),发布产物名为 steamdl-engine
│                       #   桌面: win-x64 单文件,由 MAUI 进程 spawn
│                       #   Android: linux-bionic-arm64 松散自包含目录,打包为 engine-bionic.zip
└── SteamDl.Maui/       # MAUI Blazor Hybrid 应用(C# UI + Android C# 平台层)
    ├── Pages/          # 账号/下载/游戏库/任务/设置 UI
    ├── Services/       # /api 客户端、状态管理、引擎 sidecar 生命周期
    └── Platforms/Android/ # MainActivity + EngineService(前台服务守护引擎)
external/
└── DepotDownloader/    # 上游源码,零修改(Program/Ansi 等由 Core/Shims 替身实现替代)
```

Android 端关键机制：

- 引擎以 `engine-bionic.zip` 作为 MAUI Android asset 打进 APK，首次启动或 APK 更新后
  解包到 `filesDir/engine-bionic` 后 exec，避免 Android 对 jniLibs 文件名和加载方式的限制。
- `targetSdk=28`：保持 legacy 外置存储行为，让 `WRITE_EXTERNAL_STORAGE` 可写
  `/sdcard/Download`；引擎运行目录仍位于应用私有目录。
- Android 无系统 OpenSSL，CI 会将 bionic 预编译的 `libssl_3.so/libcrypto_3.so`
  （KDAB/android_openssl）与 `libe_sqlite3.so` 一起放入引擎 zip，通过 `LD_LIBRARY_PATH`
  提供给 .NET；CA 证书用打包的 `cacert.pem` + `SSL_CERT_FILE`。
- 前台服务持有引擎进程 stdin，服务销毁即关闭管道，引擎（`STEAMDL_SIDECAR=1`）
  读到 EOF 自动退出，不留孤儿进程；进程意外退出由守护线程自动重拉。

## 构建（一律通过 GitHub Actions）

本机不需要安装任何构建工具链。工作流：[.github/workflows/build-apk.yml](.github/workflows/build-apk.yml)

```text
push 到 develop                → 构建 Debug APK（上传 Artifacts）
push tag（形如 v1.0.0）        → 构建 Release APK + Windows 桌面包，并发布 GitHub Release
pull_request                   → 构建 Debug 验证
workflow_dispatch              → 手动触发，可选 Release/Debug 与发布 tag
```

流水线任务：

```text
build-engine-bionic  dotnet publish linux-bionic-arm64 松散自包含引擎
build-apk            注入引擎/OpenSSL/SQLite/cacert → dotnet publish MAUI APK（含签名）
build-windows        dotnet publish win-x64 引擎 + MAUI Windows → zip
release              tag/手动触发时把 APK 与 Windows zip 发布到 Release
```

Release 签名 Secrets（与旧版一致）：

```text
ANDROID_KEYSTORE_BASE64  # keystore 文件 base64（只填等号右侧的值）
ANDROID_KEY_ALIAS
ANDROID_STORE_PASS
ANDROID_KEY_PASS
```

只要 Secret 存在，Debug 和 Release 都使用同一份签名，产物可互相覆盖安装。

## 本地开发辅助（可选）

```bash
node build.mjs doctor           # 检查 dotnet/java
node build.mjs run              # 本机启动引擎 http://127.0.0.1:8630
node build.mjs maui-run         # 启动 MAUI UI（需要对应平台 MAUI workload）
node build.mjs maui-build       # 构建 MAUI 项目（可能需要 workload）
node build.mjs clean
```

MAUI UI 开发时可先 `node build.mjs run` 起引擎，再运行 `node build.mjs maui-run`
或用 IDE 启动 `src/SteamDl.Maui/SteamDl.Maui.csproj`，UI 通过 127.0.0.1 契约直连本机引擎。

## 使用说明

- 先在"账号"页登录 Steam（支持 Guard App 确认 / 验证码输入；登录成功保存
  refresh token，勾选记住密码可在 token 失效后一键重登）。
- "游戏库"页增量/全量同步账号拥有的游戏，选择游戏跳转下载。
- "下载"页也可直接粘贴商店链接 / steam:// 链接 / AppID 解析。
- DLC:账号拥有即随本体一起下载;单独补 DLC 填其 Depot ID（steamdb.info 可查）。
- 保存目录:Android 用系统目录选择器（SAF 解析为真实路径），Windows 用资源管理器。
  默认 `/sdcard/Download/steamdl`。
- 任务支持暂停/继续/取消/重试/断点恢复；删除任务可选"仅记录/记录+文件"。
- 拷贝到 PC:将下载目录内的文件放入 Steam 库的 `Steam\steamapps\common\<游戏安装目录名>`，
  Steam 里点"安装"会自动发现现有文件并只校验补差。

## 注意

- 本项目引擎部分为 GPL-2.0，若公开分发 APK 需同样以 GPL 开源。
- 大陆网络直连 Steam CM 服务器可能失败（api.steampowered.com 不可达），
  与代码无关，需自行解决网络问题。
- 升级安装:MAUI 版与旧版包名一致（app.steamdl），使用同一 keystore 时可覆盖安装；
  账号与任务数据位于应用私有目录，覆盖安装保留。
