// 引擎 /api 契约的 HTTP 客户端(127.0.0.1 IPC)。
// 使用 dart:io HttpClient,无第三方依赖。
import 'dart:convert';
import 'dart:io';

import 'models.dart';

class ApiException implements Exception {
  final int status;
  final String message;

  ApiException(this.status, this.message);

  @override
  String toString() => message;
}

class ApiClient {
  final String host;
  final int port;
  final HttpClient _client = HttpClient()
    ..connectionTimeout = const Duration(seconds: 5);

  ApiClient({this.host = '127.0.0.1', this.port = 8630});

  Uri _uri(String path, [Map<String, String>? query]) => Uri(
      scheme: 'http',
      host: host,
      port: port,
      path: path,
      queryParameters: query);

  Future<Map<String, dynamic>> _request(
    String method,
    String path, {
    Map<String, dynamic>? body,
    Map<String, String>? query,
    Duration timeout = const Duration(seconds: 20),
  }) async {
    final req =
        await _client.openUrl(method, _uri(path, query)).timeout(timeout);
    if (body != null) {
      req.headers.contentType = ContentType.json;
      req.add(utf8.encode(jsonEncode(body)));
    }
    final res = await req.close().timeout(timeout);
    final text = await utf8.decodeStream(res).timeout(timeout);
    Map<String, dynamic> data = const {};
    if (text.isNotEmpty) {
      try {
        final parsed = jsonDecode(text);
        if (parsed is Map<String, dynamic>) data = parsed;
      } catch (_) {
        // 非 JSON 响应按空对象处理
      }
    }
    if (res.statusCode < 200 || res.statusCode >= 300) {
      throw ApiException(res.statusCode,
          (data['error'] as String?) ?? 'HTTP ${res.statusCode}');
    }
    return data;
  }

  Future<Map<String, dynamic>> _get(
    String path, {
    Map<String, String>? query,
    Duration timeout = const Duration(seconds: 20),
  }) =>
      _request('GET', path, query: query, timeout: timeout);

  Future<Map<String, dynamic>> _post(String path,
          [Map<String, dynamic>? body]) =>
      _request('POST', path, body: body ?? const {});

  // ---- 健康检查 ----

  Future<bool> ping({Duration timeout = const Duration(seconds: 1)}) async {
    try {
      await _get('/api/config', timeout: timeout);
      return true;
    } catch (_) {
      return false;
    }
  }

  Future<Map<String, dynamic>> config() => _get('/api/config');

  Future<Map<String, dynamic>> diagnosticsLog() => _get('/api/diagnostics/log');

  // ---- 账号 ----

  Future<(List<String>, List<AccountDetail>, LoginState, String)>
      accounts() async {
    final data = await _get('/api/accounts');
    final names = ((data['accounts'] as List?) ?? const [])
        .map((x) => x.toString())
        .toList();
    final details = ((data['account_details'] as List?) ?? const [])
        .whereType<Map<String, dynamic>>()
        .map(AccountDetail.fromJson)
        .toList();
    final login = data['login'] is Map<String, dynamic>
        ? LoginState.fromJson(data['login'] as Map<String, dynamic>)
        : LoginState.idle;
    return (names, details, login, (data['selected_account'] ?? '').toString());
  }

  Future<LoginState> login(
      String username, String password, bool rememberPassword) async {
    final data = await _post('/api/accounts/login', {
      'username': username,
      'password': password,
      'remember_password': rememberPassword,
    });
    return LoginState.fromJson(data);
  }

  Future<LoginState> loginInput(String answer) async {
    final data = await _post('/api/accounts/login/input', {'answer': answer});
    return LoginState.fromJson(data);
  }

  Future<LoginState> relogin(String username) async {
    final data = await _post('/api/accounts/relogin', {'username': username});
    return LoginState.fromJson(data);
  }

  Future<void> logout(String username) =>
      _post('/api/accounts/logout', {'username': username});

  Future<void> selectAccount(String username) =>
      _post('/api/accounts/select', {'username': username});

  // ---- 解析与游戏信息 ----

  Future<ParsedTarget> parse(String url) async {
    final data = await _post('/api/parse', {'url': url});
    return ParsedTarget.fromJson(data);
  }

  Future<AppInfo?> appInfo(String appId) async {
    try {
      final data = await _get('/api/appinfo/$appId');
      return AppInfo.fromJson(data);
    } on ApiException {
      return null; // 商店无信息不影响下载
    }
  }

  // ---- 游戏库 ----

  Future<LibraryStatus> libraryStatus(String username) async {
    final data =
        await _get('/api/library/status', query: {'username': username});
    return LibraryStatus.fromJson(data);
  }

  Future<LibraryStatus> librarySync(String username,
      {required bool full}) async {
    final data = await _post('/api/library/sync', {
      'username': username,
      'mode': full ? 'full' : 'incremental',
      'force_full_sync': full,
    });
    return LibraryStatus.fromJson(data);
  }

  // ---- 任务 ----

  Future<List<Job>> jobs() async {
    final data = await _get('/api/jobs');
    return ((data['jobs'] as List?) ?? const [])
        .whereType<Map<String, dynamic>>()
        .map(Job.fromJson)
        .toList();
  }

  Future<Job?> job(String jobId) async {
    try {
      final data = await _get('/api/jobs/$jobId');
      return Job.fromJson(data);
    } on ApiException {
      return null;
    }
  }

  Future<Job> createJob({
    required String kind,
    required String id,
    required String username,
    required String os,
    String depot = '',
    String outputDir = '',
    String installDir = '',
    String name = '',
  }) async {
    final data = await _post('/api/jobs', {
      'kind': kind,
      'id': id,
      'username': username,
      'anonymous': false,
      'os': os,
      'depot': depot,
      'output_dir': outputDir,
      'install_dir': installDir,
      'installdir': installDir,
      'name': name,
    });
    return Job.fromJson((data['job'] as Map<String, dynamic>?) ?? data);
  }

  Future<void> jobAction(String jobId, String action) =>
      _post('/api/jobs/$jobId/$action');

  Future<void> jobInput(String jobId, String answer) =>
      _post('/api/jobs/$jobId/input', {'answer': answer});

  Future<void> deleteJob(String jobId, {required bool deleteFiles}) =>
      _request('DELETE', '/api/jobs/$jobId',
          body: {'delete_files': deleteFiles});

  Future<void> deleteAllJobs({required bool deleteFiles}) =>
      _request('DELETE', '/api/jobs', body: {'delete_files': deleteFiles});

  // ---- 设置 ----

  Future<AppSettings> settings() async =>
      AppSettings.fromJson(await _get('/api/settings'));

  Future<AppSettings> saveSettings(AppSettings settings) async =>
      AppSettings.fromJson(await _post('/api/settings', settings.toJson()));

  void close() {
    _client.close(force: true);
  }
}
