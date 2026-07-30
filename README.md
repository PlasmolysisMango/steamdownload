# SteamDl — Steam 下载器（Flutter 原生 UI + .NET 引擎）

在手机/PC 上下载 Steam 游戏文件（Windows 版 depot），用于拷贝到 PC 安装。
下载引擎复用 [DepotDownloader](https://github.com/SteamRE/DepotDownloader)
（GPL-2.0，源码 vendored 于 `external/DepotDownloader/`，commit `1e8e20c`）。

## 架构

UI 为 Flutter 原生渲染（无 WebView），下载引擎为 .NET 自包含 sidecar 子进程，
两者通过 `http://127.0.0.1:8630` 的 `/api` 契约进程间通信：

```
SteamDl.sln
src/
├── SteamDl.Core/       # 引擎:DD 源码编入 + Console 中继(交互转发) + 任务状态机 + /api HTTP 服务
├── SteamDl.Server/     # 引擎 sidecar 入口(net9.0),发布产物名为 steamdl-engine
│                       #   桌面: win-x64/linux-x64 单文件,由 Flutter 进程 spawn
│                       #   Android: linux-bionic-arm64 单文件,以 libsteamdl_engine.so
│                       #   打入 APK jniLibs,由前台服务从 nativeLibraryDir exec
└── steamdl_app/        # Flutter 应用(Dart UI + Android Kotlin 平台层)
    ├── lib/            # 5 个页面:账号/下载/游戏库/任务/设置;零第三方 pub 依赖
    └── android/        # MainActivity(MethodChannel/SAF) + EngineService(前台服务守护引擎)
external/
└── DepotDownloader/    # 上游源码,零修改(Program/Ansi 等由 Core/Shims 替身实现替代)
```

Android 端关键机制：

- 引擎以 `lib*.so` 命名打进 APK（`useLegacyPackaging=true` 保证解包到
  `nativeLibraryDir`，该目录允许 exec）。
- `targetSdk=28`：.NET 单文件启动时会把原生库解包到应用数据目录再 dlopen，
  Android 10+（targetSdk≥29）禁止此行为；同时 legacy 外置存储让
  `WRITE_EXTERNAL_STORAGE` 即可写 `/sdcard/Download`。
- Android 无系统 OpenSSL，CI 会将 bionic 预编译的 `libssl_3.so/libcrypto_3.so`
  （KDAB/android_openssl）打入 APK，前台服务建立 `libssl.so.3` 符号链接并通过
  `LD_LIBRARY_PATH` 提供给 .NET；CA 证书用打包的 `cacert.pem` + `SSL_CERT_FILE`。
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
build-engine-bionic  dotnet publish linux-bionic-arm64 单文件引擎
build-apk            注入引擎/OpenSSL/cacert → flutter build apk（含签名）
build-windows        dotnet publish win-x64 引擎 + flutter build windows → zip
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
node build.mjs doctor           # 检查 dotnet/flutter/java
node build.mjs run              # 本机启动引擎 http://127.0.0.1:8630
node build.mjs flutter-run      # 启动 Flutter UI(桌面端会自动 spawn 引擎)
node build.mjs publish-engine   # 发布当前平台引擎到 artifacts/engine
node build.mjs clean
```

Flutter UI 开发时可先 `node build.mjs run` 起引擎，再 `flutter run` 任意平台目标，
UI 通过 127.0.0.1 契约直连本机引擎，全程热重载。

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
- 升级安装:2.x（Flutter 版）与 1.x（WebView 版）包名一致（app.steamdl），
  使用同一 keystore 时可覆盖安装；账号与任务数据位于应用私有目录，覆盖安装保留。
