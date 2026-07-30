// 设置页：默认下载目录/默认平台/最大线程/自动恢复 + 日志查看器。
import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../engine.dart';
import '../models.dart';
import '../state_controllers.dart';
import '../common_widgets.dart';

class SettingsPage extends StatefulWidget {
  const SettingsPage({super.key});

  @override
  State<SettingsPage> createState() => _SettingsPageState();
}

class _SettingsPageState extends State<SettingsPage> {
  final _downloadDir = TextEditingController();
  final _maxDownloads = TextEditingController();
  String _platformOs = 'windows';
  bool _autoResume = true;
  bool _dirty = false;
  bool _initialized = false;
  String _logJobId = '';

  static const _engineLogId = '__engine__';

  @override
  void dispose() {
    _downloadDir.dispose();
    _maxDownloads.dispose();
    super.dispose();
  }

  void _syncFromSettings(AppSettings? settings) {
    if (settings == null || _dirty) return;
    if (!_initialized ||
        _downloadDir.text != settings.defaultDownloadDir ||
        _platformOs != settings.defaultPlatformOs) {
      _initialized = true;
      _downloadDir.text = settings.defaultDownloadDir;
      _maxDownloads.text = settings.maxDownloads.toString();
      _platformOs = settings.defaultPlatformOs;
      _autoResume = settings.autoResume;
    }
  }

  Future<void> _save(SettingsController settings) async {
    final current = settings.settings;
    final saved = await settings.save(AppSettings(
      defaultDownloadDir: _downloadDir.text.trim(),
      defaultPlatformOs: _platformOs,
      maxDownloads: int.tryParse(_maxDownloads.text) ?? 8,
      autoResume: _autoResume,
      selectedAccount: current?.selectedAccount ?? '',
    ));
    if (saved != null && mounted) setState(() => _dirty = false);
  }

  Future<void> _pickDirectory(UiController ui) async {
    try {
      final picked = await pickNativeDirectory();
      if (picked != null && picked.trim().isNotEmpty) {
        setState(() {
          _downloadDir.text = picked.trim();
          _dirty = true;
        });
      }
    } catch (e) {
      ui.showToast('目录选择失败: $e');
    }
  }

  String _logText(AppController app, AuthController auth, JobsController jobs) {
    if (_logJobId == _engineLogId) return app.engineLog;
    if (_logJobId.isNotEmpty && jobs.activeJob?.jobId == _logJobId) {
      return jobs.activeJob?.log ?? '';
    }
    final activeLog = jobs.activeJob?.log ?? '';
    if (activeLog.isNotEmpty) return activeLog;
    return auth.loginState.log;
  }

  String? _logHint(
      AppController app, AuthController auth, JobsController jobs) {
    if (_logJobId == _engineLogId && app.engineLogPath.isNotEmpty) {
      return '日志文件：${app.engineLogPath}';
    }
    if (jobs.activeJob != null) {
      return '当前任务：${jobs.activeJob!.displayTitle} · ${stateText(jobs.activeJob!.state)}';
    }
    if (auth.loginState.state != 'idle') {
      return '当前登录流程：${stateText(auth.loginState.state)}';
    }
    return null;
  }

  @override
  Widget build(BuildContext context) {
    final app = context.watch<AppController>();
    final settings = context.watch<SettingsController>();
    final auth = context.watch<AuthController>();
    final jobs = context.watch<JobsController>();
    final ui = context.read<UiController>();
    final current = settings.settings;
    _syncFromSettings(current);
    final logText = _logText(app, auth, jobs);

    return PageFrame(
      children: [
        AppCard(
          title: '设置',
          children: [
            if (current == null)
              const Text('加载设置中…')
            else ...[
              Row(
                children: [
                  Expanded(
                    child: TextField(
                      controller: _downloadDir,
                      decoration: const InputDecoration(
                          labelText: '默认下载目录',
                          hintText: '/storage/emulated/0/Download/steamdl'),
                      onChanged: (_) => _dirty = true,
                    ),
                  ),
                  const SizedBox(width: 8),
                  OutlinedButton(
                      onPressed: () => _pickDirectory(ui),
                      child: const Text('选择')),
                ],
              ),
              const SizedBox(height: 12),
              DropdownButtonFormField<String>(
                value: _platformOs,
                decoration: const InputDecoration(labelText: '默认平台'),
                items: const [
                  DropdownMenuItem(value: 'windows', child: Text('Windows')),
                  DropdownMenuItem(value: 'linux', child: Text('Linux')),
                  DropdownMenuItem(value: 'any', child: Text('全部平台')),
                ],
                onChanged: (v) {
                  setState(() {
                    _platformOs = v ?? 'windows';
                    _dirty = true;
                  });
                },
              ),
              const SizedBox(height: 12),
              TextField(
                controller: _maxDownloads,
                keyboardType: TextInputType.number,
                decoration: const InputDecoration(labelText: '最大下载线程'),
                onChanged: (_) => _dirty = true,
              ),
              SwitchListTile(
                value: _autoResume,
                onChanged: (v) => setState(() {
                  _autoResume = v;
                  _dirty = true;
                }),
                title: const Text('服务重启后自动恢复未完成任务',
                    style: TextStyle(fontSize: 14)),
                contentPadding: EdgeInsets.zero,
                dense: true,
              ),
              FilledButton(
                  onPressed: () => _save(settings), child: const Text('保存设置')),
            ],
          ],
        ),
        const SizedBox(height: 16),
        LogViewer(
          value: logText,
          selected: _logJobId.isNotEmpty &&
                  (_logJobId == _engineLogId ||
                      jobs.jobs.any((j) => j.jobId == _logJobId))
              ? _logJobId
              : '',
          hint: _logHint(app, auth, jobs),
          items: [
            const DropdownMenuItem(value: _engineLogId, child: Text('引擎诊断日志')),
            const DropdownMenuItem(value: '', child: Text('登录日志 / 当前任务')),
            for (final job in jobs.jobs)
              DropdownMenuItem(
                value: job.jobId,
                child: Text('${job.displayTitle} · ${stateText(job.state)}',
                    maxLines: 1, overflow: TextOverflow.ellipsis),
              ),
          ],
          onChanged: (v) async {
            _logJobId = v ?? '';
            if (_logJobId == _engineLogId) {
              await app.refreshDiagnostics();
            } else if (_logJobId.isNotEmpty) {
              await jobs.loadJobDetail(_logJobId);
            }
            if (mounted) setState(() {});
          },
          onRefresh: () async {
            await app.refreshDiagnostics();
            await app.refresh();
          },
          onCopy: () => copyText(context, logText, () => ui.showToast('日志已复制')),
        ),
      ],
    );
  }
}
