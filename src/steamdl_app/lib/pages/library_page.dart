// 游戏库页:增量/全量同步、搜索、筛选(下载状态/排序/视图)、
// 游戏卡片(封面/已下载徽标/任务进度)、选择并下载。
import 'package:flutter/material.dart';

import '../app_state.dart';
import '../models.dart';

class LibraryPage extends StatefulWidget {
  final AppState state;
  final VoidCallback onPickGame;

  const LibraryPage({super.key, required this.state, required this.onPickGame});

  @override
  State<LibraryPage> createState() => _LibraryPageState();
}

class _LibraryPageState extends State<LibraryPage> {
  final _search = TextEditingController();
  String _downloadFilter = 'all'; // all | downloaded | undownloaded
  String _sortBy = 'name'; // name | app_id
  bool _sortAsc = true;
  bool _gridView = true;
  bool _filterOpen = false;

  AppState get state => widget.state;

  @override
  void initState() {
    super.initState();
    state.addListener(_onState);
    state.loadLibraryStatus();
  }

  @override
  void dispose() {
    state.removeListener(_onState);
    _search.dispose();
    super.dispose();
  }

  void _onState() {
    if (mounted) setState(() {});
  }

  /// 每个 AppID 取最新的任务(与 Web UI jobByAppId 逻辑一致)
  Map<String, Job> get _jobByAppId {
    final map = <String, Job>{};
    for (final job in state.jobs) {
      if (job.kind != 'app') continue;
      if (job.username.isNotEmpty && job.username != state.selectedAccount) {
        continue;
      }
      final current = map[job.id];
      if (current == null ||
          (DateTime.tryParse(job.updatedAt) ?? DateTime(0)).isAfter(
              DateTime.tryParse(current.updatedAt) ?? DateTime(0))) {
        map[job.id] = job;
      }
    }
    return map;
  }

  List<LibraryGame> get _visibleGames {
    final query = _search.text.trim().toLowerCase();
    final jobMap = _jobByAppId;
    final filtered = state.libraryStatus.items.where((g) {
      if (query.isNotEmpty &&
          !g.appId.contains(query) &&
          !g.name.toLowerCase().contains(query)) {
        return false;
      }
      final job = jobMap[g.appId];
      final downloaded =
          g.isDownloaded || job?.downloaded == true || job?.state == 'done';
      if (_downloadFilter == 'downloaded') return downloaded;
      if (_downloadFilter == 'undownloaded') return !downloaded;
      return true;
    }).toList();

    filtered.sort((a, b) {
      final value = _sortBy == 'app_id'
          ? (int.tryParse(a.appId) ?? 0).compareTo(int.tryParse(b.appId) ?? 0)
          : a.name.toLowerCase().compareTo(b.name.toLowerCase());
      return _sortAsc ? value : -value;
    });
    return filtered;
  }

  @override
  Widget build(BuildContext context) {
    final status = state.libraryStatus;
    final syncing = status.state == 'running';
    final games = _visibleGames;
    final modeText = status.syncMode == 'incremental' ? '增量同步' : '全量同步';

    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        Card(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text('游戏库', style: Theme.of(context).textTheme.titleMedium),
                const SizedBox(height: 4),
                Text(
                  '当前账号：${state.selectedAccount}。首次增量同步会自动按全量执行；后续增量同步仅检查新增候选应用。',
                  style: const TextStyle(fontSize: 12, color: Colors.white70),
                ),
                const SizedBox(height: 12),
                Row(
                  children: [
                    OutlinedButton(
                      onPressed: syncing
                          ? null
                          : () => state.syncLibrary(full: false),
                      child: Text(syncing ? '同步中…' : '增量同步'),
                    ),
                    const SizedBox(width: 8),
                    OutlinedButton(
                      onPressed:
                          syncing ? null : () => state.syncLibrary(full: true),
                      child: const Text('全量同步'),
                    ),
                  ],
                ),
                const SizedBox(height: 12),
                Row(
                  children: [
                    Expanded(
                      child: TextField(
                        controller: _search,
                        decoration: const InputDecoration(
                          hintText: '搜索游戏名或 AppID',
                          prefixIcon: Icon(Icons.search, size: 20),
                          border: OutlineInputBorder(),
                          isDense: true,
                        ),
                        onChanged: (_) => setState(() {}),
                      ),
                    ),
                    const SizedBox(width: 8),
                    IconButton(
                      onPressed: () =>
                          setState(() => _filterOpen = !_filterOpen),
                      icon: Icon(Icons.tune,
                          color: _filterOpen
                              ? Theme.of(context).colorScheme.primary
                              : null),
                      tooltip: '筛选和排序',
                    ),
                  ],
                ),
                if (_filterOpen) ...[
                  const SizedBox(height: 12),
                  Wrap(
                    spacing: 12,
                    runSpacing: 12,
                    children: [
                      _dropdown('下载状态', _downloadFilter, const {
                        'all': '全部游戏',
                        'downloaded': '已下载',
                        'undownloaded': '未下载',
                      }, (v) => setState(() => _downloadFilter = v)),
                      _dropdown('排序方式', _sortBy, const {
                        'name': '按名称排序',
                        'app_id': '按 AppID 排序',
                      }, (v) => setState(() => _sortBy = v)),
                      _dropdown('排序方向', _sortAsc ? 'asc' : 'desc', const {
                        'asc': '升序',
                        'desc': '降序',
                      }, (v) => setState(() => _sortAsc = v == 'asc')),
                      _dropdown('展示方式', _gridView ? 'grid' : 'list', const {
                        'grid': '大图显示',
                        'list': '列表显示',
                      }, (v) => setState(() => _gridView = v == 'grid')),
                    ],
                  ),
                ],
                if (status.message.isNotEmpty) ...[
                  const SizedBox(height: 8),
                  Text(status.message,
                      style:
                          const TextStyle(fontSize: 12, color: Colors.white70)),
                ],
                if (syncing || status.appCount > 0) ...[
                  const SizedBox(height: 8),
                  LinearProgressIndicator(
                      value: status.appCount > 0
                          ? status.progress / 100
                          : null),
                  const SizedBox(height: 4),
                  Text(
                    '$modeText进度：已检查候选应用 ${status.scannedAppCount}/${status.appCount > 0 ? status.appCount : '?'} · 已显示 ${status.items.length} 个游戏',
                    style:
                        const TextStyle(fontSize: 12, color: Colors.white70),
                  ),
                ],
              ],
            ),
          ),
        ),
        const SizedBox(height: 16),
        if (status.items.isEmpty)
          Card(
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Text(
                status.message.isNotEmpty
                    ? status.message
                    : '尚未同步游戏库。点击"增量同步"会在首次自动全量读取当前账号拥有的游戏。',
                style: const TextStyle(color: Colors.white70),
              ),
            ),
          )
        else if (games.isEmpty)
          Card(
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Text(_emptyText(),
                  style: const TextStyle(color: Colors.white70)),
            ),
          )
        else if (_gridView)
          LayoutBuilder(
            builder: (context, constraints) {
              final columns = (constraints.maxWidth / 320).floor().clamp(1, 4).toInt();
              return GridView.builder(
                shrinkWrap: true,
                physics: const NeverScrollableScrollPhysics(),
                gridDelegate: SliverGridDelegateWithFixedCrossAxisCount(
                  crossAxisCount: columns,
                  mainAxisExtent: 280,
                  crossAxisSpacing: 12,
                  mainAxisSpacing: 12,
                ),
                itemCount: games.length,
                itemBuilder: (context, i) => _gameCard(games[i], grid: true),
              );
            },
          )
        else
          Column(
            children: [
              for (final game in games)
                Padding(
                  padding: const EdgeInsets.only(bottom: 8),
                  child: _gameCard(game, grid: false),
                ),
            ],
          ),
      ],
    );
  }

  String _emptyText() {
    if (_search.text.trim().isNotEmpty) {
      return '当前搜索没有匹配的游戏。清空搜索框可查看已同步的全部游戏。';
    }
    if (_downloadFilter == 'downloaded') return '当前没有匹配的已下载游戏。';
    if (_downloadFilter == 'undownloaded') return '当前没有匹配的未下载游戏。';
    return '当前筛选没有匹配的游戏。';
  }

  Widget _dropdown(String label, String value, Map<String, String> options,
      ValueChanged<String> onChanged) {
    return SizedBox(
      width: 160,
      child: DropdownButtonFormField<String>(
        value: value,
        decoration: InputDecoration(
          labelText: label,
          border: const OutlineInputBorder(),
          isDense: true,
        ),
        items: [
          for (final entry in options.entries)
            DropdownMenuItem(value: entry.key, child: Text(entry.value)),
        ],
        onChanged: (v) {
          if (v != null) onChanged(v);
        },
      ),
    );
  }

  Widget _gameCard(LibraryGame game, {required bool grid}) {
    final job = _jobByAppId[game.appId];
    final downloading = job != null &&
        const {'queued', 'starting', 'running', 'waiting_input', 'paused'}
            .contains(job.state);
    final downloaded =
        game.isDownloaded || job?.downloaded == true || job?.state == 'done';

    final info = Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Expanded(
              child: Text(game.name,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: const TextStyle(
                      fontWeight: FontWeight.w600, fontSize: 14)),
            ),
            if (downloaded)
              const Padding(
                padding: EdgeInsets.only(left: 6),
                child: Chip(
                  label: Text('已下载', style: TextStyle(fontSize: 10)),
                  visualDensity: VisualDensity.compact,
                  padding: EdgeInsets.zero,
                ),
              ),
          ],
        ),
        Text(
          'AppID ${game.appId}${game.sizeBytes > 0 ? ' · 大小 ${formatBytes(game.sizeBytes)}' : ''}',
          style: const TextStyle(fontSize: 11, color: Colors.white70),
        ),
        if (job != null) ...[
          const SizedBox(height: 6),
          LinearProgressIndicator(value: (job.percent / 100).clamp(0.0, 1.0)),
          const SizedBox(height: 2),
          Text(
            '${stateText(job.state)} · ${job.percent.round()}%${job.progressText.isNotEmpty ? ' · ${job.progressText}' : ''}',
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: const TextStyle(fontSize: 11, color: Colors.white70),
          ),
        ],
        const SizedBox(height: 8),
        SizedBox(
          width: double.infinity,
          child: FilledButton(
            onPressed: downloading
                ? null
                : () {
                    state.seedDownload(DownloadSeed(
                      kind: 'app',
                      id: game.appId,
                      name: game.name,
                      installDir: game.installDir,
                      sizeBytes: game.sizeBytes,
                    ));
                    widget.onPickGame();
                  },
            child: Text(
              downloading
                  ? (job!.state == 'paused' ? '已暂停' : '下载中…')
                  : (downloaded ? '重新下载' : '选择并下载'),
              style: const TextStyle(fontSize: 13),
            ),
          ),
        ),
      ],
    );

    final cover = game.headerImage.isNotEmpty
        ? Image.network(
            game.headerImage,
            fit: BoxFit.cover,
            errorBuilder: (_, __, ___) =>
                const ColoredBox(color: Color(0xFF1B2838)),
          )
        : const ColoredBox(color: Color(0xFF1B2838));

    if (grid) {
      return Card(
        clipBehavior: Clip.antiAlias,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            AspectRatio(aspectRatio: 460 / 215, child: cover),
            Expanded(
              child: Padding(padding: const EdgeInsets.all(10), child: info),
            ),
          ],
        ),
      );
    }

    return Card(
      clipBehavior: Clip.antiAlias,
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(width: 120, height: 100, child: cover),
          Expanded(
            child: Padding(padding: const EdgeInsets.all(10), child: info),
          ),
        ],
      ),
    );
  }
}
