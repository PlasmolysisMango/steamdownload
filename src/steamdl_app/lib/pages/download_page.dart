// 下载页:解析链接/AppID → 展示游戏信息 → 配置平台/Depot/保存目录 → 创建任务。
// 支持从游戏库带 seed 跳转预填。
import 'package:flutter/material.dart';

import '../app_state.dart';
import '../engine.dart';
import '../models.dart';

class DownloadPage extends StatefulWidget {
  final AppState state;
  final VoidCallback onJobCreated;

  const DownloadPage({
    super.key,
    required this.state,
    required this.onJobCreated,
  });

  @override
  State<DownloadPage> createState() => _DownloadPageState();
}

class _DownloadPageState extends State<DownloadPage> {
  final _url = TextEditingController();
  final _depot = TextEditingController();
  final _outputDir = TextEditingController();

  ParsedTarget? _parsed;
  AppInfo? _appInfo;
  String _os = 'windows';
  bool _appliedDefaults = false;
  bool _starting = false;

  AppState get state => widget.state;

  @override
  void initState() {
    super.initState();
    state.addListener(_onState);
    _applyDefaults();
    _consumeSeed();
  }

  @override
  void dispose() {
    state.removeListener(_onState);
    _url.dispose();
    _depot.dispose();
    _outputDir.dispose();
    super.dispose();
  }

  void _onState() {
    if (!mounted) return;
    _applyDefaults();
    _consumeSeed();
    setState(() {});
  }

  void _applyDefaults() {
    final settings = state.settings;
    if (settings == null || _appliedDefaults) return;
    _appliedDefaults = true;
    _os = settings.defaultPlatformOs;
    if (_outputDir.text.isEmpty) _outputDir.text = settings.defaultDownloadDir;
  }

  void _consumeSeed() {
    final seed = state.takeDownloadSeed();
    if (seed == null) return;
    _parsed = ParsedTarget(kind: seed.kind, id: seed.id);
    _appInfo = seed.name.isNotEmpty
        ? AppInfo(
            name: seed.name,
            installDir: seed.installDir,
            headerImage:
                'https://cdn.cloudflare.steamstatic.com/steam/apps/${seed.id}/header.jpg',
            sizeBytes: seed.sizeBytes,
          )
        : null;
    if (seed.kind == 'app') _fetchAppInfo(seed.id, fallback: _appInfo);
  }

  Future<void> _fetchAppInfo(String appId, {AppInfo? fallback}) async {
    final info = await state.api.appInfo(appId);
    if (!mounted) return;
    setState(() {
      _appInfo = info != null
          ? AppInfo(
              name: info.name.isNotEmpty ? info.name : (fallback?.name ?? ''),
              installDir: info.installDir.isNotEmpty
                  ? info.installDir
                  : (fallback?.installDir ?? ''),
              headerImage: info.headerImage.isNotEmpty
                  ? info.headerImage
                  : (fallback?.headerImage ?? ''),
              sizeBytes:
                  info.sizeBytes > 0 ? info.sizeBytes : (fallback?.sizeBytes ?? 0),
            )
          : fallback;
    });
  }

  Future<void> _parse() async {
    try {
      final parsed = await state.api.parse(_url.text);
      setState(() {
        _parsed = parsed;
        _appInfo = null;
      });
      if (parsed.kind == 'app') await _fetchAppInfo(parsed.id);
    } catch (e) {
      state.showToast(e.toString());
    }
  }

  Future<void> _pickDirectory() async {
    try {
      final picked = await pickNativeDirectory();
      if (picked != null && picked.trim().isNotEmpty) {
        setState(() => _outputDir.text = picked.trim());
        state.showToast('已选择保存目录');
      }
    } catch (e) {
      state.showToast('目录选择失败: $e');
    }
  }

  Future<void> _start() async {
    final parsed = _parsed;
    if (parsed == null || _starting) return;
    setState(() => _starting = true);
    try {
      final installDir = _appInfo?.installDir.isNotEmpty == true
          ? _appInfo!.installDir
          : (_appInfo?.name ?? '');
      final job = await state.api.createJob(
        kind: parsed.kind,
        id: parsed.id,
        username: state.selectedAccount,
        os: _os,
        depot: _depot.text.trim(),
        outputDir: _outputDir.text.trim(),
        installDir: installDir,
        name: _appInfo?.name ?? '',
      );
      state.setActiveJob(job);
      state.showToast('任务已创建: ${job.jobId.substring(0, job.jobId.length < 8 ? job.jobId.length : 8)}');
      await state.refresh();
      widget.onJobCreated();
    } catch (e) {
      state.showToast(e.toString());
    } finally {
      if (mounted) setState(() => _starting = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        Card(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text('解析链接下载',
                    style: Theme.of(context).textTheme.titleMedium),
                const SizedBox(height: 4),
                Text(
                  '当前账号：${state.selectedAccount}。也可以从游戏库选择游戏后自动跳转到这里。',
                  style: const TextStyle(fontSize: 12, color: Colors.white70),
                ),
                const SizedBox(height: 12),
                Row(
                  children: [
                    Expanded(
                      child: TextField(
                        controller: _url,
                        decoration: const InputDecoration(
                          hintText:
                              'https://store.steampowered.com/app/730/... 或 AppID',
                          border: OutlineInputBorder(),
                          isDense: true,
                        ),
                        onSubmitted: (_) => _parse(),
                      ),
                    ),
                    const SizedBox(width: 8),
                    FilledButton(onPressed: _parse, child: const Text('解析')),
                  ],
                ),
              ],
            ),
          ),
        ),
        const SizedBox(height: 16),
        Card(
          clipBehavior: Clip.antiAlias,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              if (_appInfo?.headerImage.isNotEmpty == true)
                AspectRatio(
                  aspectRatio: 460 / 215,
                  child: Image.network(
                    _appInfo!.headerImage,
                    fit: BoxFit.cover,
                    errorBuilder: (_, __, ___) =>
                        const ColoredBox(color: Color(0xFF1B2838)),
                  ),
                )
              else
                const SizedBox(
                  height: 96,
                  child: Center(
                      child: Text('Steam',
                          style: TextStyle(
                              fontSize: 24, color: Colors.white24))),
                ),
              Padding(
                padding: const EdgeInsets.all(16),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      _appInfo?.name.isNotEmpty == true
                          ? _appInfo!.name
                          : (_parsed != null
                              ? '${_parsed!.kind} ${_parsed!.id}'
                              : '等待选择游戏'),
                      style: Theme.of(context).textTheme.titleMedium,
                    ),
                    const SizedBox(height: 4),
                    Text(
                      _parsed != null
                          ? '类型 ${_parsed!.kind} · ID ${_parsed!.id}'
                              '${(_appInfo?.sizeBytes ?? 0) > 0 ? ' · 待下载大小 ${formatBytes(_appInfo!.sizeBytes)}' : ''}'
                          : '从游戏库选择，或解析链接后创建下载任务',
                      style:
                          const TextStyle(fontSize: 12, color: Colors.white70),
                    ),
                  ],
                ),
              ),
            ],
          ),
        ),
        const SizedBox(height: 16),
        Card(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text('下载选项', style: Theme.of(context).textTheme.titleMedium),
                const SizedBox(height: 12),
                DropdownButtonFormField<String>(
                  value: _os,
                  decoration: const InputDecoration(
                    labelText: '目标平台',
                    border: OutlineInputBorder(),
                    isDense: true,
                  ),
                  items: const [
                    DropdownMenuItem(value: 'windows', child: Text('Windows')),
                    DropdownMenuItem(value: 'linux', child: Text('Linux')),
                    DropdownMenuItem(value: 'any', child: Text('全部平台')),
                  ],
                  onChanged: (v) => setState(() => _os = v ?? 'windows'),
                ),
                const SizedBox(height: 12),
                TextField(
                  controller: _depot,
                  decoration: const InputDecoration(
                    labelText: 'Depot ID（可选）',
                    hintText: '例如 731',
                    border: OutlineInputBorder(),
                    isDense: true,
                  ),
                ),
                const SizedBox(height: 12),
                Row(
                  children: [
                    Expanded(
                      child: TextField(
                        controller: _outputDir,
                        decoration: InputDecoration(
                          labelText: '保存目录',
                          hintText: state.settings?.defaultDownloadDir ??
                              '/storage/emulated/0/Download/steamdl',
                          border: const OutlineInputBorder(),
                          isDense: true,
                        ),
                      ),
                    ),
                    const SizedBox(width: 8),
                    OutlinedButton(
                        onPressed: _pickDirectory, child: const Text('选择')),
                  ],
                ),
                const SizedBox(height: 8),
                const Text(
                  '实际下载会自动在该目录下创建游戏目录（优先使用 Steam 游戏安装目录名）。',
                  style: TextStyle(fontSize: 12, color: Colors.white70),
                ),
                const SizedBox(height: 12),
                SizedBox(
                  width: double.infinity,
                  child: FilledButton(
                    onPressed: _parsed == null || _starting ? null : _start,
                    child: Text(_starting
                        ? '创建中…'
                        : '使用 ${state.selectedAccount} 开始下载'),
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
