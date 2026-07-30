// 全局应用状态:1 秒周期轮询引擎(与原 Web UI 契约语义一致),
// 复刻的业务规则:
// - 登录完成后自动触发该账号的全量游戏库同步(仅登录流程触发一次)
// - 切换选中账号触发一次增量同步
// - Toast 5 秒自动消失
// - 删除任务后联动刷新游戏库
import 'dart:async';

import 'package:flutter/foundation.dart';

import 'api_client.dart';
import 'engine.dart';
import 'models.dart';

class AppState extends ChangeNotifier {
  final ApiClient api;
  final EngineController engine;

  AppState(this.api, this.engine);

  // ---- 服务状态 ----
  bool serviceOnline = false;
  bool engineStarting = true;
  String engineError = '';
  String engineLog = '';
  String engineLogPath = '';

  // ---- 轮询数据 ----
  List<Job> jobs = [];
  List<String> accounts = [];
  List<AccountDetail> accountDetails = [];
  LoginState loginState = LoginState.idle;
  AppSettings? settings;
  bool canPickDirectory = false;

  // ---- 选中/详情 ----
  String selectedAccount = '';
  Job? activeJob;
  DownloadSeed? downloadSeed;

  // ---- 游戏库 ----
  LibraryStatus libraryStatus = LibraryStatus.idle;
  Timer? _libraryPoller;

  // ---- Toast ----
  String toast = '';
  Timer? _toastTimer;

  Timer? _poller;
  String _loginFlowAccount = '';
  String _fullSyncDoneFor = '';
  String _autoIncrementalFor = '';
  bool _disposed = false;

  bool get loggedIn =>
      selectedAccount.isNotEmpty && accounts.contains(selectedAccount);

  Future<void> start() async {
    // 重试入口可重复调用,避免叠加轮询定时器
    _poller?.cancel();
    _poller = null;
    engineStarting = true;
    notifyListeners();
    try {
      await engine.ensureStarted();
      engineError = '';
    } catch (e) {
      engineError = e.toString();
    }
    engineStarting = false;
    notifyListeners();
    if (engineError.isNotEmpty) {
      serviceOnline = false;
      return;
    }
    await refresh();
    _poller = Timer.periodic(const Duration(seconds: 1), (_) => refresh());
  }

  void showToast(String message) {
    toast = message;
    notifyListeners();
    _toastTimer?.cancel();
    _toastTimer = Timer(const Duration(seconds: 5), () {
      toast = '';
      if (!_disposed) notifyListeners();
    });
  }

  void clearToast() {
    toast = '';
    _toastTimer?.cancel();
    notifyListeners();
  }

  Future<void> refresh() async {
    if (_disposed) return;
    Map<String, dynamic> config;
    try {
      config = await api.config();
      canPickDirectory = config['can_pick_directory'] == true ||
          defaultTargetPlatform == TargetPlatform.android ||
          defaultTargetPlatform == TargetPlatform.windows ||
          defaultTargetPlatform == TargetPlatform.linux;
      serviceOnline = true;
      engineLogPath = (config['log_path'] ?? '').toString();
      final jobManagerError = (config['job_manager_error'] ?? '').toString();
      engineError = jobManagerError.isEmpty ? '' : 'JobManager 初始化失败：$jobManagerError';
    } catch (e) {
      serviceOnline = false;
      engineError = '无法连接下载引擎：$e';
      notifyListeners();
      return;
    }

    await refreshDiagnostics(notify: false);

    try {
      jobs = await api.jobs();
    } catch (e) {
      engineError = '读取任务失败：$e';
      jobs = const [];
    }

    try {
      final accountsRes = await api.accounts();
      accounts = accountsRes.$1;
      accountDetails = accountsRes.$2;
      final nextLogin = accountsRes.$3;
      _handleLoginTransition(nextLogin);
      loginState = nextLogin;
    } catch (e) {
      engineError = '读取账号/登录状态失败：$e';
      accounts = const [];
      accountDetails = const [];
      loginState = LoginState.idle;
    }

    try {
      settings = await api.settings();
    } catch (e) {
      engineError = '读取设置失败：$e';
    }

    await _refreshActiveJob();
    notifyListeners();
  }

  Future<void> refreshDiagnostics({bool notify = true}) async {
    try {
      final data = await api.diagnosticsLog();
      engineLog = (data['log'] ?? '').toString();
      engineLogPath = (data['log_path'] ?? engineLogPath).toString();
      final error = (data['job_manager_error'] ?? '').toString();
      if (error.isNotEmpty) {
        engineError = 'JobManager 初始化失败：$error';
      }
      if (notify && !_disposed) notifyListeners();
    } catch (_) {
      // 诊断日志读取失败不影响主流程。
    }
  }

  void _handleLoginTransition(LoginState next) {
    if ((next.state == 'running' || next.state == 'waiting_input') &&
        next.username.isNotEmpty) {
      _loginFlowAccount = next.username.trim();
    }
    if (next.state == 'error') _loginFlowAccount = '';
    if (next.state == 'done' &&
        next.username.isNotEmpty &&
        accounts.contains(next.username)) {
      final username = next.username.trim();
      setSelectedAccount(username, silent: true);
      if (_loginFlowAccount == username && _fullSyncDoneFor != username) {
        _fullSyncDoneFor = username;
        _autoIncrementalFor = username;
        _loginFlowAccount = '';
        unawaited(syncLibrary(full: true, silent: true));
      }
    }
  }

  Future<void> _refreshActiveJob() async {
    final current = activeJob;
    if (current != null) {
      activeJob = await api.job(current.jobId) ?? activeJob;
      return;
    }
    Job? running;
    for (final job in jobs) {
      if (job.isActive) {
        running = job;
        break;
      }
    }
    if (running != null) {
      activeJob = await api.job(running.jobId);
    }
  }

  void setSelectedAccount(String username, {bool silent = false}) {
    final next = username.trim();
    if (selectedAccount == next) return;
    selectedAccount = next;
    // 切换账号后触发一次增量同步
    if (next.isNotEmpty && accounts.contains(next) && _autoIncrementalFor != next) {
      _autoIncrementalFor = next;
      unawaited(syncLibrary(full: false, silent: true));
    }
    if (!silent) notifyListeners();
  }

  void setActiveJob(Job? job) {
    activeJob = job;
    notifyListeners();
  }

  Future<void> loadJobDetail(String jobId) async {
    activeJob = await api.job(jobId);
    notifyListeners();
  }

  void seedDownload(DownloadSeed? seed) {
    downloadSeed = seed;
    notifyListeners();
  }

  DownloadSeed? takeDownloadSeed() {
    final seed = downloadSeed;
    downloadSeed = null;
    return seed;
  }

  // ---- 游戏库 ----

  Future<void> loadLibraryStatus() async {
    if (selectedAccount.isEmpty) return;
    try {
      libraryStatus = await api.libraryStatus(selectedAccount);
      notifyListeners();
      _pollLibraryWhileRunning();
    } catch (_) {
      // 引擎暂不可用时静默
    }
  }

  Future<void> syncLibrary({required bool full, bool silent = false}) async {
    if (selectedAccount.isEmpty) return;
    try {
      libraryStatus = await api.librarySync(selectedAccount, full: full);
      notifyListeners();
      _pollLibraryWhileRunning();
    } catch (e) {
      if (!silent) showToast(e.toString());
    }
  }

  void _pollLibraryWhileRunning() {
    if (libraryStatus.state != 'running') return;
    _libraryPoller?.cancel();
    _libraryPoller =
        Timer.periodic(const Duration(milliseconds: 700), (timer) async {
      if (_disposed || selectedAccount.isEmpty) {
        timer.cancel();
        return;
      }
      try {
        libraryStatus = await api.libraryStatus(selectedAccount);
        notifyListeners();
        if (libraryStatus.state != 'running') {
          timer.cancel();
          if (libraryStatus.state == 'error' && libraryStatus.message.isNotEmpty) {
            showToast(libraryStatus.message);
          }
        }
      } catch (_) {
        timer.cancel();
      }
    });
  }

  // ---- 任务操作 ----

  Future<void> jobAction(Job job, String action) async {
    try {
      await api.jobAction(job.jobId, action);
      await refresh();
      if (activeJob?.jobId == job.jobId) await loadJobDetail(job.jobId);
    } catch (e) {
      showToast(e.toString());
    }
  }

  Future<void> deleteJob(Job job, {required bool deleteFiles}) async {
    try {
      await api.deleteJob(job.jobId, deleteFiles: deleteFiles);
      if (activeJob?.jobId == job.jobId) activeJob = null;
      await refresh();
      await loadLibraryStatus(); // 删除联动刷新游戏库
      showToast(deleteFiles ? '任务和文件已删除' : '任务记录已删除');
    } catch (e) {
      showToast(e.toString());
    }
  }

  Future<void> deleteAllJobs({required bool deleteFiles}) async {
    try {
      await api.deleteAllJobs(deleteFiles: deleteFiles);
      activeJob = null;
      await refresh();
      await loadLibraryStatus();
      showToast(deleteFiles ? '全部任务和文件已删除' : '全部任务记录已删除');
    } catch (e) {
      showToast(e.toString());
    }
  }

  // ---- 账号操作 ----

  Future<bool> login(String username, String password, bool remember) async {
    try {
      loginState = await api.login(username, password, remember);
      notifyListeners();
      showToast('登录已启动，请根据提示完成 Guard/2FA');
      return true;
    } catch (e) {
      showToast(e.toString());
      return false;
    }
  }

  Future<void> submitLoginInput(String answer) async {
    try {
      loginState = await api.loginInput(answer);
      notifyListeners();
      await refresh();
    } catch (e) {
      showToast(e.toString());
    }
  }

  Future<void> relogin(String username) async {
    try {
      loginState = await api.relogin(username);
      notifyListeners();
      showToast('重新登录已启动，请根据提示完成可能需要的 Guard/2FA');
    } catch (e) {
      showToast(e.toString());
    }
  }

  Future<void> logout(String username) async {
    try {
      await api.logout(username);
      if (selectedAccount == username) selectedAccount = '';
      await refresh();
    } catch (e) {
      showToast(e.toString());
    }
  }

  // ---- 设置 ----

  Future<AppSettings?> saveSettings(AppSettings next) async {
    try {
      settings = await api.saveSettings(next);
      notifyListeners();
      showToast('设置已保存');
      return settings;
    } catch (e) {
      showToast(e.toString());
      return null;
    }
  }

  @override
  void dispose() {
    _disposed = true;
    _poller?.cancel();
    _libraryPoller?.cancel();
    _toastTimer?.cancel();
    unawaited(engine.dispose());
    api.close();
    super.dispose();
  }
}
