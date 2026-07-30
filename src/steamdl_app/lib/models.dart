// 与引擎 /api 契约(JSON 字段)一一对应的数据模型。
// 字段命名保持 snake_case 契约到 Dart camelCase 的直接映射,不做语义加工。

class Job {
  final String jobId;
  final String kind;
  final String id;
  final String name;
  final String title;
  final String username;
  final bool anonymous;
  final String os;
  final String depot;
  final String outputDir;
  final String state;
  final String prompt;
  final bool promptSecret;
  final double percent;
  final String progressText;
  final String error;
  final bool downloaded;
  final String log;
  final String updatedAt;

  const Job({
    required this.jobId,
    required this.kind,
    required this.id,
    required this.name,
    required this.title,
    required this.username,
    required this.anonymous,
    required this.os,
    required this.depot,
    required this.outputDir,
    required this.state,
    required this.prompt,
    required this.promptSecret,
    required this.percent,
    required this.progressText,
    required this.error,
    required this.downloaded,
    required this.log,
    required this.updatedAt,
  });

  factory Job.fromJson(Map<String, dynamic> json) => Job(
        jobId: _str(json['job_id']),
        kind: _str(json['kind'], 'app'),
        id: _str(json['id']),
        name: _str(json['name']),
        title: _str(json['title']),
        username: _str(json['username']),
        anonymous: json['anonymous'] == true,
        os: _str(json['os'], 'windows'),
        depot: _str(json['depot']),
        outputDir: _str(json['output_dir']),
        state: _str(json['state'], 'idle'),
        prompt: _str(json['prompt']),
        promptSecret: json['prompt_secret'] == true,
        percent: _num(json['percent']),
        progressText: _str(json['progress_text']),
        error: _str(json['error']),
        downloaded: json['downloaded'] == true,
        log: _str(json['log']),
        updatedAt: _str(json['updated_at']),
      );

  String get displayTitle =>
      title.isNotEmpty ? title : (name.isNotEmpty ? name : '$kind $id');

  bool get isActive => const {
        'queued',
        'starting',
        'running',
        'waiting_input',
      }.contains(state);
}

class AppSettings {
  final String defaultDownloadDir;
  final String defaultPlatformOs;
  final int maxDownloads;
  final bool autoResume;

  const AppSettings({
    required this.defaultDownloadDir,
    required this.defaultPlatformOs,
    required this.maxDownloads,
    required this.autoResume,
  });

  factory AppSettings.fromJson(Map<String, dynamic> json) => AppSettings(
        defaultDownloadDir: _str(json['default_download_dir']),
        defaultPlatformOs: _str(json['default_platform_os'], 'windows'),
        maxDownloads: _num(json['max_downloads'], 8).toInt(),
        autoResume: json['auto_resume'] != false,
      );

  Map<String, dynamic> toJson() => {
        'default_download_dir': defaultDownloadDir,
        'default_platform_os': defaultPlatformOs,
        'max_downloads': maxDownloads,
        'auto_resume': autoResume,
      };

  AppSettings copyWith({
    String? defaultDownloadDir,
    String? defaultPlatformOs,
    int? maxDownloads,
    bool? autoResume,
  }) =>
      AppSettings(
        defaultDownloadDir: defaultDownloadDir ?? this.defaultDownloadDir,
        defaultPlatformOs: defaultPlatformOs ?? this.defaultPlatformOs,
        maxDownloads: maxDownloads ?? this.maxDownloads,
        autoResume: autoResume ?? this.autoResume,
      );
}

class LibraryGame {
  final String appId;
  final String name;
  final String headerImage;
  final String installDir;
  final int sizeBytes;
  final String sizeText;
  final bool isDownloaded;
  final String downloadedAt;

  const LibraryGame({
    required this.appId,
    required this.name,
    required this.headerImage,
    required this.installDir,
    required this.sizeBytes,
    required this.sizeText,
    required this.isDownloaded,
    required this.downloadedAt,
  });

  factory LibraryGame.fromJson(Map<String, dynamic> json) {
    final appId = _str(json['app_id'], _str(json['id']));
    return LibraryGame(
      appId: appId,
      name: _str(json['name'], 'App $appId'),
      headerImage: _str(json['header_image']),
      installDir: _str(json['install_dir'], _str(json['installdir'])),
      sizeBytes: _num(json['size_bytes']).toInt(),
      sizeText: _str(json['size_text']),
      isDownloaded: json['is_downloaded'] == true,
      downloadedAt: _str(json['downloaded_at']),
    );
  }
}

class LibraryStatus {
  final String username;
  final String state;
  final String syncMode;
  final String message;
  final List<LibraryGame> items;
  final int appCount;
  final int scannedAppCount;
  final int progress;

  const LibraryStatus({
    required this.username,
    required this.state,
    required this.syncMode,
    required this.message,
    required this.items,
    required this.appCount,
    required this.scannedAppCount,
    required this.progress,
  });

  factory LibraryStatus.fromJson(Map<String, dynamic> json) => LibraryStatus(
        username: _str(json['username']),
        state: _str(json['state'], 'idle'),
        syncMode: _str(json['sync_mode'], 'full'),
        message: _str(json['message']),
        items: ((json['items'] as List?) ?? const [])
            .whereType<Map<String, dynamic>>()
            .map(LibraryGame.fromJson)
            .toList(),
        appCount: _num(json['app_count']).toInt(),
        scannedAppCount: _num(json['scanned_app_count']).toInt(),
        progress: _num(json['progress']).toInt(),
      );

  static const idle = LibraryStatus(
    username: '',
    state: 'idle',
    syncMode: 'full',
    message: '',
    items: [],
    appCount: 0,
    scannedAppCount: 0,
    progress: 0,
  );
}

class AccountDetail {
  final String username;
  final bool loggedIn;
  final bool rememberPassword;
  final bool hasSavedPassword;
  final String lastUsedAt;

  const AccountDetail({
    required this.username,
    required this.loggedIn,
    required this.rememberPassword,
    required this.hasSavedPassword,
    required this.lastUsedAt,
  });

  factory AccountDetail.fromJson(Map<String, dynamic> json) => AccountDetail(
        username: _str(json['username']),
        loggedIn: json['logged_in'] == true,
        rememberPassword: json['remember_password'] == true,
        hasSavedPassword: json['has_saved_password'] == true,
        lastUsedAt: _str(json['last_used_at']),
      );
}

class LoginState {
  final String username;
  final String state;
  final String prompt;
  final bool promptSecret;
  final String error;
  final String log;

  const LoginState({
    required this.username,
    required this.state,
    required this.prompt,
    required this.promptSecret,
    required this.error,
    required this.log,
  });

  factory LoginState.fromJson(Map<String, dynamic> json) => LoginState(
        username: _str(json['username']),
        state: _str(json['state'], 'idle'),
        prompt: _str(json['prompt']),
        promptSecret: json['prompt_secret'] == true,
        error: _str(json['error']),
        log: _str(json['log']),
      );

  static const idle = LoginState(
    username: '',
    state: 'idle',
    prompt: '',
    promptSecret: false,
    error: '',
    log: '',
  );

  bool get busy => state == 'running' || state == 'waiting_input';
}

class ParsedTarget {
  final String kind;
  final String id;

  const ParsedTarget({required this.kind, required this.id});

  factory ParsedTarget.fromJson(Map<String, dynamic> json) =>
      ParsedTarget(kind: _str(json['kind'], 'app'), id: _str(json['id']));
}

class AppInfo {
  final String name;
  final String installDir;
  final String headerImage;
  final int sizeBytes;

  const AppInfo({
    required this.name,
    required this.installDir,
    required this.headerImage,
    required this.sizeBytes,
  });

  factory AppInfo.fromJson(Map<String, dynamic> json) => AppInfo(
        name: _str(json['name']),
        installDir: _str(json['install_dir'], _str(json['installdir'])),
        headerImage: _str(json['header_image']),
        sizeBytes: _num(json['size_bytes']).toInt(),
      );
}

/// 从游戏库/解析页带到下载页的预填信息。
class DownloadSeed {
  final String kind;
  final String id;
  final String name;
  final String installDir;
  final int sizeBytes;

  const DownloadSeed({
    required this.kind,
    required this.id,
    this.name = '',
    this.installDir = '',
    this.sizeBytes = 0,
  });
}

const Map<String, String> jobStateText = {
  'idle': '空闲',
  'queued': '排队中',
  'starting': '启动中',
  'running': '下载中',
  'paused': '已暂停',
  'waiting_input': '等待输入',
  'interrupted': '已中断',
  'done': '完成',
  'error': '出错',
  'cancelled': '已取消',
};

String stateText(String state) => jobStateText[state] ?? state;

String formatBytes(num bytes) {
  var value = bytes.toDouble();
  if (!value.isFinite || value <= 0) return '';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  var unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  final digits = value >= 10 || unit == 0 ? 0 : 1;
  return '${value.toStringAsFixed(digits)} ${units[unit]}';
}

String _str(dynamic value, [String fallback = '']) {
  if (value == null) return fallback;
  final text = value.toString();
  return text.isEmpty ? fallback : text;
}

double _num(dynamic value, [double fallback = 0]) {
  if (value is num) return value.toDouble();
  return double.tryParse(value?.toString() ?? '') ?? fallback;
}
