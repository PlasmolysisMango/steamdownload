// 设置页:默认下载目录/默认平台/最大线程/自动恢复 + 日志查看器。
import 'package:flutter/material.dart';

import '../app_state.dart';
import '../engine.dart';
import '../models.dart';

class SettingsPage extends StatefulWidget {
  final AppState state;

  const SettingsPage({super.key, required this.state});

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

  AppState get state => widget.state;

  @override
  void initState() {
    super.initState();
    state.addListener(_onState);
    _syncFromSettings();
  }

  @override
  void dispose() {
    state.removeListener(_onState);
    _downloadDir.dispose();
    _maxDownloads.dispose();
    super.dispose();
  }

  void _onState() {
    if (!mounted) return;
    _syncFromSettings();
    setState(() {});
  }

  void _syncFromSettings() {
    final settings = state.settings;
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

  Future<void> _save() async {
    final saved = await state.saveSettings(AppSettings(
      defaultDownloadDir: _downloadDir.text.trim(),
      defaultPlatformOs: _platformOs,
      maxDownloads: int.tryParse(_maxDownloads.text) ?? 8,
      autoResume: _autoResume,
    ));
    if (saved != null) {
      setState(() => _dirty = false);
    }
  }

  Future<void> _pickDirectory() async {
    try {
      final picked = await pickNativeDirectory();
      if (picked != null && picked.trim().isNotEmpty) {
        setState(() {
          _downloadDir.text = picked.trim();
          _dirty = true;
        });
      }
    } catch (e) {
      state.showToast('目录选择失败: $e');
    }
  }

  String get _logText {
    if (_logJobId.isNotEmpty && state.activeJob?.jobId == _logJobId) {
      return state.activeJob?.log ?? '';
    }
    final activeLog = state.activeJob?.log ?? '';
    if (activeLog.isNotEmpty) return activeLog;
    return state.loginState.log;
  }

  @override
  Widget build(BuildContext context) {
    final settings = state.settings;

    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        Card(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: settings == null
                ? const Text('加载设置中…')
                : Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text('设置',
                          style: Theme.of(context).textTheme.titleMedium),
                      const SizedBox(height: 16),
                      Row(
                        children: [
                          Expanded(
                            child: TextField(
                              controller: _downloadDir,
                              decoration: const InputDecoration(
                                labelText: '默认下载目录',
                                hintText:
                                    '/storage/emulated/0/Download/steamdl',
                                border: OutlineInputBorder(),
                                isDense: true,
                              ),
                              onChanged: (_) => _dirty = true,
                            ),
                          ),
                          const SizedBox(width: 8),
                          OutlinedButton(
                              onPressed: _pickDirectory,
                              child: const Text('选择')),
                        ],
                      ),
                      const SizedBox(height: 12),
                      DropdownButtonFormField<String>(
                        value: _platformOs,
                        decoration: const InputDecoration(
                          labelText: '默认平台',
                          border: OutlineInputBorder(),
                          isDense: true,
                        ),
                        items: const [
                          DropdownMenuItem(
                              value: 'windows', child: Text('Windows')),
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
                        decoration: const InputDecoration(
                          labelText: '最大下载线程',
                          border: OutlineInputBorder(),
                          isDense: true,
                        ),
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
                          onPressed: _save, child: const Text('保存设置')),
                    ],
                  ),
          ),
        ),
        const SizedBox(height: 16),
        Card(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Expanded(
                      child: Text('日志',
                          style: Theme.of(context).textTheme.titleMedium),
                    ),
                    TextButton(
                      onPressed: () => state.refresh(),
                      child: const Text('刷新'),
                    ),
                  ],
                ),
                if (state.jobs.isNotEmpty) ...[
                  const SizedBox(height: 8),
                  DropdownButtonFormField<String>(
                    value: _logJobId.isNotEmpty &&
                            state.jobs.any((j) => j.jobId == _logJobId)
                        ? _logJobId
                        : '',
                    decoration: const InputDecoration(
                      labelText: '选择任务日志',
                      border: OutlineInputBorder(),
                      isDense: true,
                    ),
                    items: [
                      const DropdownMenuItem(
                          value: '', child: Text('登录日志 / 当前任务')),
                      for (final job in state.jobs)
                        DropdownMenuItem(
                          value: job.jobId,
                          child: Text(
                            '${job.displayTitle} · ${stateText(job.state)}',
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                          ),
                        ),
                    ],
                    onChanged: (v) async {
                      _logJobId = v ?? '';
                      if (_logJobId.isNotEmpty) {
                        await state.loadJobDetail(_logJobId);
                      }
                      setState(() {});
                    },
                  ),
                ],
                if (state.activeJob != null) ...[
                  const SizedBox(height: 8),
                  Text(
                    '当前任务：${state.activeJob!.displayTitle} · ${stateText(state.activeJob!.state)}',
                    style:
                        const TextStyle(fontSize: 12, color: Colors.white70),
                  ),
                ] else if (state.loginState.state != 'idle') ...[
                  const SizedBox(height: 8),
                  Text(
                    '当前登录流程：${stateText(state.loginState.state)}',
                    style:
                        const TextStyle(fontSize: 12, color: Colors.white70),
                  ),
                ],
                const SizedBox(height: 8),
                Container(
                  width: double.infinity,
                  constraints: const BoxConstraints(maxHeight: 360),
                  padding: const EdgeInsets.all(10),
                  decoration: BoxDecoration(
                    color: Colors.black.withValues(alpha: 0.35),
                    borderRadius: BorderRadius.circular(8),
                  ),
                  child: SingleChildScrollView(
                    child: SelectableText(
                      _logText.isNotEmpty ? _logText : '暂无日志',
                      style: const TextStyle(
                          fontFamily: 'monospace', fontSize: 11.5, height: 1.5),
                    ),
                  ),
                ),
              ],
            ),
          ),
        ),
      ],
    );
  }
}
