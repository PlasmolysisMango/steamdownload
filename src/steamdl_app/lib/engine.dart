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
  int? _lastExitCode;
  String _lastOutput = '';
  bool _starting = false;
  Timer? _watchdog;

  EngineController(this.api);

  /// 启动引擎并等待就绪;应用启动时调用一次,之后由看门狗守护。
  Future<void> ensureStarted() async {
    if (await api.ping(timeout: const Duration(milliseconds: 700))) {
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
      'STEAMDL_BIND_HOST': api.host,
      'PORT': '${api.port}',
    });
    _process = process;
    _lastExitCode = null;
    _lastOutput = '';
    // 输出转发到宿主日志,便于诊断
    process.stdout
        .transform(const SystemEncoding().decoder)
        .listen((s) => _recordOutput(s, stdout));
    process.stderr
        .transform(const SystemEncoding().decoder)
        .listen((s) => _recordOutput(s, stderr));
    unawaited(process.exitCode.then((code) {
      _lastExitCode = code;
      _process = null;
    }));
  }

  void _recordOutput(String text, IOSink sink) {
    sink.write(text);
    _lastOutput = (_lastOutput + text);
    if (_lastOutput.length > 2000) {
      _lastOutput = _lastOutput.substring(_lastOutput.length - 2000);
    }
  }

  Future<void> _waitReady() async {
    const startupTimeout = Duration(seconds: 10);
    final deadline = DateTime.now().add(startupTimeout);
    while (DateTime.now().isBefore(deadline)) {
      if (await api.ping(timeout: const Duration(milliseconds: 500))) return;
      if (Platform.isAndroid) {
        final status = await _androidEngineStatus();
        final state = (status['status'] ?? '').toString();
        if (state == 'failed' || state == 'exited') {
          final error = (status['error'] ?? '').toString();
          final output = (status['last_output'] ?? '').toString().trim();
          throw StateError('Android 引擎启动失败($state)'
              '${error.isEmpty ? '' : ': $error'}'
              '${output.isEmpty ? '' : '\n$output'}');
        }
      } else if (_lastExitCode != null) {
        final log = _lastOutput.trim();
        throw StateError('引擎进程已退出(ExitCode=$_lastExitCode)${log.isEmpty ? '' : ': $log'}');
      }
      await Future<void>.delayed(const Duration(milliseconds: 200));
    }
    throw StateError('引擎启动超时: ${startupTimeout.inSeconds} 秒内未响应 '
        'http://${api.host}:${api.port}/api/config');
  }

  Future<Map<dynamic, dynamic>> _androidEngineStatus() async {
    try {
      final status = await platformChannel.invokeMethod<Map<dynamic, dynamic>>(
          'engineServiceStatus');
      return status ?? const {};
    } catch (_) {
      return const {};
    }
  }

  void _armWatchdog() {
    _watchdog ??= Timer.periodic(const Duration(seconds: 5), (_) async {
      if (_starting) return;
      if (!await api.ping(timeout: const Duration(seconds: 1))) {
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
