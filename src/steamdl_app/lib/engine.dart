// 引擎 sidecar 生命周期管理。
// - Android: 通过 MethodChannel 让原生前台服务从 nativeLibraryDir 拉起
//   libsteamdl_engine.so(.NET linux-bionic-arm64 自包含单文件),前台服务保活。
// - 桌面(Windows/Linux): 直接 spawn 可执行文件(与 Flutter 可执行同目录的
//   engine/ 子目录),stdin 管道断开时引擎自动退出。
import 'dart:async';
import 'dart:io';

import 'package:flutter/services.dart';

import 'api_client.dart';

const MethodChannel platformChannel = MethodChannel('app.steamdl/platform');

class EngineController {
  final ApiClient api;
  Process? _process;
  bool _starting = false;
  Timer? _watchdog;

  EngineController(this.api);

  /// 启动引擎并等待就绪;应用启动时调用一次,之后由看门狗守护。
  Future<void> ensureStarted() async {
    if (await api.ping()) {
      _armWatchdog();
      return;
    }
    await _start();
    _armWatchdog();
  }

  Future<void> _start() async {
    if (_starting) return;
    _starting = true;
    try {
      if (Platform.isAndroid) {
        // 原生前台服务负责 exec sidecar 并持有 WakeLock
        await platformChannel.invokeMethod<void>('startEngineService');
      } else {
        await _spawnDesktop();
      }
      await _waitReady();
    } finally {
      _starting = false;
    }
  }

  Future<void> _spawnDesktop() async {
    if (_process != null) return;
    final exeDir = File(Platform.resolvedExecutable).parent.path;
    final name = Platform.isWindows ? 'steamdl-engine.exe' : 'steamdl-engine';
    final candidates = [
      '$exeDir${Platform.pathSeparator}engine${Platform.pathSeparator}$name',
      '$exeDir${Platform.pathSeparator}$name',
    ];
    final exe = candidates.firstWhere(
      (p) => File(p).existsSync(),
      orElse: () => '',
    );
    if (exe.isEmpty) {
      throw StateError('未找到引擎程序(steamdl-engine),请检查安装目录: $candidates');
    }
    final process = await Process.start(exe, const [], environment: {
      'STEAMDL_SIDECAR': '1',
      'PORT': '${api.port}',
    });
    _process = process;
    // 输出转发到宿主日志,便于诊断
    process.stdout.transform(const SystemEncoding().decoder).listen((s) => stdout.write(s));
    process.stderr.transform(const SystemEncoding().decoder).listen((s) => stderr.write(s));
    unawaited(process.exitCode.then((code) {
      _process = null;
    }));
  }

  Future<void> _waitReady() async {
    for (var i = 0; i < 100; i++) {
      if (await api.ping()) return;
      await Future<void>.delayed(const Duration(milliseconds: 300));
    }
    throw StateError('引擎启动超时,请重启应用');
  }

  void _armWatchdog() {
    _watchdog ??= Timer.periodic(const Duration(seconds: 5), (_) async {
      if (_starting) return;
      if (!await api.ping()) {
        try {
          await _start();
        } catch (_) {
          // 下个周期继续重试
        }
      }
    });
  }

  Future<void> dispose() async {
    _watchdog?.cancel();
    _watchdog = null;
    // stdin 管道关闭后 sidecar 会自行退出(STEAMDL_SIDECAR=1)
    _process?.kill(ProcessSignal.sigterm);
    _process = null;
  }
}

/// 平台目录选择。Android 走 SAF(原生解析真实路径);
/// Windows 用 PowerShell FolderBrowserDialog;Linux 用 zenity/kdialog。
Future<String?> pickNativeDirectory() async {
  if (Platform.isAndroid) {
    return platformChannel.invokeMethod<String>('pickDirectory');
  }
  if (Platform.isWindows) {
    const script = "Add-Type -AssemblyName System.Windows.Forms; "
        "\$d = New-Object System.Windows.Forms.FolderBrowserDialog; "
        "\$d.Description = '选择 SteamDl 保存目录'; "
        "\$d.ShowNewFolderButton = \$true; "
        "if (\$d.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) "
        "{ [Console]::Write(\$d.SelectedPath) }";
    final result = await Process.run(
        'powershell', ['-NoProfile', '-STA', '-Command', script]);
    final path = (result.stdout as String).trim();
    return result.exitCode == 0 && path.isNotEmpty ? path : null;
  }
  if (Platform.isLinux) {
    final zenity = await Process.run('which', ['zenity']);
    if (zenity.exitCode == 0) {
      final result = await Process.run('zenity',
          ['--file-selection', '--directory', '--title=选择 SteamDl 保存目录']);
      final path = (result.stdout as String).trim();
      return result.exitCode == 0 && path.isNotEmpty ? path : null;
    }
  }
  return null;
}

/// Android: 请求通知/所有文件访问权限(应用首次启动后调用)。
Future<void> requestPlatformPermissions() async {
  if (Platform.isAndroid) {
    try {
      await platformChannel.invokeMethod<void>('requestPermissions');
    } catch (_) {
      // 权限请求失败不阻塞主流程
    }
  }
}
