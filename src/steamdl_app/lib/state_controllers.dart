import 'dart:async';

import 'package:flutter/foundation.dart';

import 'api_client.dart';
import 'engine.dart';
import 'models.dart';

class UiController extends ChangeNotifier {
  String toast = '';
  DownloadSeed? _downloadSeed;
  Timer? _toastTimer;
  bool _disposed = false;

  void showToast(String message) {
    toast = message;
    notifyListeners();
    _toastTimer?.cancel();
    _toastTimer = Timer(const Duration(seconds: 5), clearToast);
  }

  void clearToast() {
    toast = '';
    _toastTimer?.cancel();
    if (!_disposed) notifyListeners();
  }

  void seedDownload(DownloadSeed? seed) {
    _downloadSeed = seed;
    notifyListeners();
  }

  DownloadSeed? takeDownloadSeed() {
    final seed = _downloadSeed;
    _downloadSeed = null;
    return seed;
  }

  @override
  void dispose() {
    _disposed = true;
    _toastTimer?.cancel();
    super.dispose();
  }
}

class AuthController extends ChangeNotifier {
  final ApiClient api;
  final UiController ui;
  void Function(String username, {required bool full})? onAccountReady;

  AuthController(this.api, this.ui);

  List<String> accounts = [];
  List<AccountDetail> accountDetails = [];
  LoginState loginState = LoginState.idle;
  String selectedAccount = '';

  String _loginFlowAccount = '';
  String _fullSyncDoneFor = '';
  String _autoIncrementalFor = '';

  bool get loggedIn =>
      selectedAccount.isNotEmpty && accounts.contains(selectedAccount);

  Map<String, AccountDetail> get detailMap => {
        for (final detail in accountDetails)
          detail.username.toLowerCase(): detail,
      };

  Future<void> refresh() async {
    final res = await api.accounts();
    accounts = res.$1;
    accountDetails = res.$2;
    _restoreSelectedAccount(res.$4);
    _handleLoginTransition(res.$3);
    loginState = res.$3;
    notifyListeners();
  }

  void _restoreSelectedAccount(String remembered) {
    if (accounts.isEmpty) {
      selectedAccount = '';
      return;
    }
    if (selectedAccount.isNotEmpty && accounts.contains(selectedAccount)) {
      return;
    }

    final trimmed = remembered.trim();
    final next = trimmed.isNotEmpty && accounts.contains(trimmed)
        ? trimmed
        : accounts.first;
    setSelectedAccount(next, silent: true, persist: trimmed != next);
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
        onAccountReady?.call(username, full: true);
      }
    }
  }

  void setSelectedAccount(String username,
      {bool silent = false, bool persist = true}) {
    final next = username.trim();
    if (selectedAccount == next) return;
    selectedAccount = next;
    if (persist) unawaited(api.selectAccount(next).catchError((_) {}));
    if (next.isNotEmpty &&
        accounts.contains(next) &&
        _autoIncrementalFor != next) {
      _autoIncrementalFor = next;
      onAccountReady?.call(next, full: false);
    }
    if (!silent) notifyListeners();
  }

  Future<bool> login(String username, String password, bool remember) async {
    try {
      loginState = await api.login(username, password, remember);
      notifyListeners();
      ui.showToast('登录已启动，请根据提示完成 Guard/2FA');
      return true;
    } catch (e) {
      ui.showToast(e.toString());
      return false;
    }
  }

  Future<void> submitLoginInput(String answer) async {
    try {
      loginState = await api.loginInput(answer);
      notifyListeners();
      await refresh();
    } catch (e) {
      ui.showToast(e.toString());
    }
  }

  Future<void> relogin(String username) async {
    try {
      loginState = await api.relogin(username);
      notifyListeners();
      ui.showToast('重新登录已启动，请根据提示完成可能需要的 Guard/2FA');
    } catch (e) {
      ui.showToast(e.toString());
    }
  }

  Future<void> logout(String username) async {
    try {
      await api.logout(username);
      if (selectedAccount == username) setSelectedAccount('', silent: true);
      await refresh();
    } catch (e) {
      ui.showToast(e.toString());
    }
  }
}

class LibraryController extends ChangeNotifier {
  final ApiClient api;
  final AuthController auth;
  final UiController ui;

  LibraryController(this.api, this.auth, this.ui);

  LibraryStatus status = LibraryStatus.idle;
  Timer? _poller;
  bool _disposed = false;

  Future<void> loadStatus() async {
    if (auth.selectedAccount.isEmpty) return;
    try {
      status = await api.libraryStatus(auth.selectedAccount);
      notifyListeners();
      _pollWhileRunning();
    } catch (_) {}
  }

  Future<void> sync({required bool full, bool silent = false}) async {
    if (auth.selectedAccount.isEmpty) return;
    try {
      status = await api.librarySync(auth.selectedAccount, full: full);
      notifyListeners();
      _pollWhileRunning();
    } catch (e) {
      if (!silent) ui.showToast(e.toString());
    }
  }

  void _pollWhileRunning() {
    if (status.state != 'running') return;
    _poller?.cancel();
    _poller = Timer.periodic(const Duration(milliseconds: 700), (timer) async {
      if (_disposed || auth.selectedAccount.isEmpty) {
        timer.cancel();
        return;
      }
      try {
        status = await api.libraryStatus(auth.selectedAccount);
        notifyListeners();
        if (status.state != 'running') {
          timer.cancel();
          if (status.state == 'error' && status.message.isNotEmpty) {
            ui.showToast(status.message);
          }
        }
      } catch (_) {
        timer.cancel();
      }
    });
  }

  @override
  void dispose() {
    _disposed = true;
    _poller?.cancel();
    super.dispose();
  }
}

class JobsController extends ChangeNotifier {
  final ApiClient api;
  final UiController ui;
  LibraryController? library;

  JobsController(this.api, this.ui);

  List<Job> jobs = [];
  Job? activeJob;

  Future<void> refresh() async {
    jobs = await api.jobs();
    await refreshActiveJob();
    notifyListeners();
  }

  Future<void> refreshActiveJob() async {
    final current = activeJob;
    if (current != null) {
      activeJob = await api.job(current.jobId) ?? activeJob;
      return;
    }
    for (final job in jobs) {
      if (job.isActive) {
        activeJob = await api.job(job.jobId);
        return;
      }
    }
  }

  void setActiveJob(Job? job) {
    activeJob = job;
    notifyListeners();
  }

  Future<void> loadJobDetail(String jobId) async {
    activeJob = await api.job(jobId);
    notifyListeners();
  }

  Future<void> action(Job job, String action) async {
    try {
      await api.jobAction(job.jobId, action);
      await refresh();
      if (activeJob?.jobId == job.jobId) await loadJobDetail(job.jobId);
    } catch (e) {
      ui.showToast(e.toString());
    }
  }

  Future<void> submitInput(Job job, String answer) async {
    try {
      await api.jobInput(job.jobId, answer);
      await loadJobDetail(job.jobId);
    } catch (e) {
      ui.showToast(e.toString());
    }
  }

  Future<void> deleteJob(Job job, {required bool deleteFiles}) async {
    try {
      await api.deleteJob(job.jobId, deleteFiles: deleteFiles);
      if (activeJob?.jobId == job.jobId) activeJob = null;
      await refresh();
      await library?.loadStatus();
      ui.showToast(deleteFiles ? '任务和文件已删除' : '任务记录已删除');
    } catch (e) {
      ui.showToast(e.toString());
    }
  }

  Future<void> deleteAll({required bool deleteFiles}) async {
    try {
      await api.deleteAllJobs(deleteFiles: deleteFiles);
      activeJob = null;
      await refresh();
      await library?.loadStatus();
      ui.showToast(deleteFiles ? '全部任务和文件已删除' : '全部任务记录已删除');
    } catch (e) {
      ui.showToast(e.toString());
    }
  }
}

class SettingsController extends ChangeNotifier {
  final ApiClient api;
  final UiController ui;

  SettingsController(this.api, this.ui);

  AppSettings? settings;

  Future<void> refresh() async {
    settings = await api.settings();
    notifyListeners();
  }

  Future<AppSettings?> save(AppSettings next) async {
    try {
      settings = await api.saveSettings(next);
      notifyListeners();
      ui.showToast('设置已保存');
      return settings;
    } catch (e) {
      ui.showToast(e.toString());
      return null;
    }
  }
}

class AppController extends ChangeNotifier {
  final ApiClient api;
  final EngineController engine;
  final UiController ui;
  final AuthController auth;
  final JobsController jobs;
  final LibraryController library;
  final SettingsController settings;

  AppController({
    required this.api,
    required this.engine,
    required this.ui,
    required this.auth,
    required this.jobs,
    required this.library,
    required this.settings,
  }) {
    auth.onAccountReady = (username, {required bool full}) {
      unawaited(library.sync(full: full, silent: true));
    };
    jobs.library = library;
  }

  bool serviceOnline = false;
  bool engineStarting = true;
  String engineError = '';
  String engineLog = '';
  String engineLogPath = '';
  bool canPickDirectory = false;
  Timer? _poller;
  bool _disposed = false;

  Future<void> start() async {
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

  Future<void> refresh() async {
    if (_disposed) return;
    try {
      final config = await api.config();
      canPickDirectory = config['can_pick_directory'] == true ||
          defaultTargetPlatform == TargetPlatform.android ||
          defaultTargetPlatform == TargetPlatform.windows ||
          defaultTargetPlatform == TargetPlatform.linux;
      serviceOnline = true;
      engineLogPath = (config['log_path'] ?? '').toString();
      final jobManagerError = (config['job_manager_error'] ?? '').toString();
      engineError =
          jobManagerError.isEmpty ? '' : 'JobManager 初始化失败：$jobManagerError';
    } catch (e) {
      serviceOnline = false;
      engineError = '无法连接下载引擎：$e';
      notifyListeners();
      return;
    }

    await refreshDiagnostics(notify: false);

    try {
      await jobs.refresh();
    } catch (e) {
      engineError = '读取任务失败：$e';
      jobs.jobs = const [];
    }

    try {
      await auth.refresh();
    } catch (e) {
      engineError = '读取账号/登录状态失败：$e';
      auth.accounts = const [];
      auth.accountDetails = const [];
      auth.loginState = LoginState.idle;
    }

    try {
      await settings.refresh();
    } catch (e) {
      engineError = '读取设置失败：$e';
    }

    notifyListeners();
  }

  Future<void> refreshDiagnostics({bool notify = true}) async {
    try {
      final data = await api.diagnosticsLog();
      engineLog = (data['log'] ?? '').toString();
      engineLogPath = (data['log_path'] ?? engineLogPath).toString();
      final error = (data['job_manager_error'] ?? '').toString();
      if (error.isNotEmpty) engineError = 'JobManager 初始化失败：$error';
      if (notify && !_disposed) notifyListeners();
    } catch (_) {}
  }

  @override
  void dispose() {
    _disposed = true;
    _poller?.cancel();
    unawaited(engine.dispose());
    api.close();
    super.dispose();
  }
}
