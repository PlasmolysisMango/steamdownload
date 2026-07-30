// 下载页：解析链接/AppID → 展示游戏信息 → 配置平台/Depot/保存目录 → 创建任务。
import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../engine.dart';
import '../models.dart';
import '../state_controllers.dart';
import '../common_widgets.dart';

class DownloadPage extends StatefulWidget {
  final VoidCallback onJobCreated;

  const DownloadPage({super.key, required this.onJobCreated});

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

  @override
  void dispose() {
    _url.dispose();
    _depot.dispose();
    _outputDir.dispose();
    super.dispose();
  }

  void _applyDefaults(SettingsController settings) {
    final current = settings.settings;
    if (current == null || _appliedDefaults) return;
    _appliedDefaults = true;
    _os = current.defaultPlatformOs;
    if (_outputDir.text.isEmpty) _outputDir.text = current.defaultDownloadDir;
  }

  void _consumeSeed(UiController ui) {
    final seed = ui.takeDownloadSeed();
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
    final api = context.read<AppController>().api;
    final info = await api.appInfo(appId);
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
              sizeBytes: info.sizeBytes > 0
                  ? info.sizeBytes
                  : (fallback?.sizeBytes ?? 0),
            )
          : fallback;
    });
  }

  Future<void> _parse() async {
    final app = context.read<AppController>();
    final ui = context.read<UiController>();
    try {
      final parsed = await app.api.parse(_url.text);
      setState(() {
        _parsed = parsed;
        _appInfo = null;
      });
      if (parsed.kind == 'app') await _fetchAppInfo(parsed.id);
    } catch (e) {
      ui.showToast(e.toString());
    }
  }

  Future<void> _pickDirectory() async {
    final ui = context.read<UiController>();
    try {
      final picked = await pickNativeDirectory();
      if (picked != null && picked.trim().isNotEmpty) {
        setState(() => _outputDir.text = picked.trim());
        ui.showToast('已选择保存目录');
      }
    } catch (e) {
      ui.showToast('目录选择失败: $e');
    }
  }

  Future<void> _start() async {
    final parsed = _parsed;
    if (parsed == null || _starting) return;
    setState(() => _starting = true);
    final app = context.read<AppController>();
    final auth = context.read<AuthController>();
    final jobs = context.read<JobsController>();
    final ui = context.read<UiController>();
    try {
      final installDir = _appInfo?.installDir.isNotEmpty == true
          ? _appInfo!.installDir
          : (_appInfo?.name ?? '');
      final job = await app.api.createJob(
        kind: parsed.kind,
        id: parsed.id,
        username: auth.selectedAccount,
        os: _os,
        depot: _depot.text.trim(),
        outputDir: _outputDir.text.trim(),
        installDir: installDir,
        name: _appInfo?.name ?? '',
      );
      jobs.setActiveJob(job);
      ui.showToast(
          '任务已创建: ${job.jobId.substring(0, job.jobId.length < 8 ? job.jobId.length : 8)}');
      await app.refresh();
      widget.onJobCreated();
    } catch (e) {
      ui.showToast(e.toString());
    } finally {
      if (mounted) setState(() => _starting = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final auth = context.watch<AuthController>();
    final settings = context.watch<SettingsController>();
    final ui = context.read<UiController>();
    _applyDefaults(settings);
    _consumeSeed(ui);

    return PageFrame(
      children: [
        AppCard(
          title: '解析链接下载',
          subtitle: '当前账号：${auth.selectedAccount}。也可以从游戏库选择游戏后自动跳转到这里。',
          children: [
            Row(
              children: [
                Expanded(
                  child: TextField(
                    controller: _url,
                    decoration: const InputDecoration(
                        hintText:
                            'https://store.steampowered.com/app/730/... 或 AppID'),
                    onSubmitted: (_) => _parse(),
                  ),
                ),
                const SizedBox(width: 8),
                FilledButton(onPressed: _parse, child: const Text('解析')),
              ],
            ),
          ],
        ),
        const SizedBox(height: 16),
        _GameInfoCard(parsed: _parsed, appInfo: _appInfo),
        const SizedBox(height: 16),
        AppCard(
          title: '下载选项',
          children: [
            DropdownButtonFormField<String>(
              value: _os,
              decoration: const InputDecoration(labelText: '目标平台'),
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
                  labelText: 'Depot ID（可选）', hintText: '例如 731'),
            ),
            const SizedBox(height: 12),
            Row(
              children: [
                Expanded(
                  child: TextField(
                    controller: _outputDir,
                    decoration: InputDecoration(
                      labelText: '保存目录',
                      hintText: settings.settings?.defaultDownloadDir ??
                          '/storage/emulated/0/Download/steamdl',
                    ),
                  ),
                ),
                const SizedBox(width: 8),
                OutlinedButton(
                    onPressed: _pickDirectory, child: const Text('选择')),
              ],
            ),
            const SizedBox(height: 8),
            const Text('实际下载会自动在该目录下创建游戏目录（优先使用 Steam 游戏安装目录名）。',
                style: TextStyle(fontSize: 12, color: Colors.white70)),
            const SizedBox(height: 12),
            SizedBox(
              width: double.infinity,
              child: FilledButton(
                onPressed: _parsed == null || _starting ? null : _start,
                child: Text(
                    _starting ? '创建中…' : '使用 ${auth.selectedAccount} 开始下载'),
              ),
            ),
          ],
        ),
      ],
    );
  }
}

class _GameInfoCard extends StatelessWidget {
  final ParsedTarget? parsed;
  final AppInfo? appInfo;

  const _GameInfoCard({required this.parsed, required this.appInfo});

  @override
  Widget build(BuildContext context) {
    final header = appInfo?.headerImage ?? '';
    return Card(
      clipBehavior: Clip.antiAlias,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          if (header.isNotEmpty)
            AspectRatio(
              aspectRatio: 460 / 215,
              child: Image.network(
                header,
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
                      style: TextStyle(fontSize: 24, color: Colors.white24))),
            ),
          Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  appInfo?.name.isNotEmpty == true
                      ? appInfo!.name
                      : (parsed != null
                          ? '${parsed!.kind} ${parsed!.id}'
                          : '等待选择游戏'),
                  style: Theme.of(context).textTheme.titleMedium,
                ),
                const SizedBox(height: 4),
                Text(
                  parsed != null
                      ? '类型 ${parsed!.kind} · ID ${parsed!.id}'
                          '${(appInfo?.sizeBytes ?? 0) > 0 ? ' · 待下载大小 ${formatBytes(appInfo!.sizeBytes)}' : ''}'
                      : '从游戏库选择，或解析链接后创建下载任务',
                  style: const TextStyle(fontSize: 12, color: Colors.white70),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}
