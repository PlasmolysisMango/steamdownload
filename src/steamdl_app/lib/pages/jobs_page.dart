// 任务页:任务历史列表 + 详情(进度/等待输入/暂停继续取消重试) +
// 长按删除单任务/删除全部,删除确认支持"仅记录/记录+文件"。
import 'package:flutter/material.dart';

import '../app_state.dart';
import '../models.dart';

class JobsPage extends StatefulWidget {
  final AppState state;

  const JobsPage({super.key, required this.state});

  @override
  State<JobsPage> createState() => _JobsPageState();
}

class _JobsPageState extends State<JobsPage> {
  final _input = TextEditingController();

  AppState get state => widget.state;

  @override
  void initState() {
    super.initState();
    state.addListener(_onState);
  }

  @override
  void dispose() {
    state.removeListener(_onState);
    _input.dispose();
    super.dispose();
  }

  void _onState() {
    if (mounted) setState(() {});
  }

  Future<void> _confirmCancel(Job job) async {
    final ok = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('取消任务'),
        content: const Text('确定要取消当前下载任务吗？'),
        actions: [
          TextButton(
              onPressed: () => Navigator.pop(context, false),
              child: const Text('返回')),
          FilledButton(
              onPressed: () => Navigator.pop(context, true),
              child: const Text('确定取消')),
        ],
      ),
    );
    if (ok == true) await state.jobAction(job, 'cancel');
  }

  Future<void> _confirmDelete({Job? job}) async {
    final isAll = job == null;
    if (isAll && state.jobs.isEmpty) return;
    final title = job?.displayTitle ?? '';
    final result = await showDialog<String>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('确认删除'),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(isAll
                ? '要删除全部 ${state.jobs.length} 个任务记录吗？'
                : '要删除任务"$title"吗？'),
            const SizedBox(height: 8),
            const Text(
              '请选择删除方式。删除文件会同时删除任务对应的下载目录。',
              style: TextStyle(fontSize: 12, color: Colors.white70),
            ),
          ],
        ),
        actions: [
          TextButton(
              onPressed: () => Navigator.pop(context),
              child: const Text('取消')),
          OutlinedButton(
              onPressed: () => Navigator.pop(context, 'record'),
              child: const Text('仅删除记录')),
          FilledButton(
            onPressed: () => Navigator.pop(context, 'files'),
            style: FilledButton.styleFrom(backgroundColor: Colors.red.shade700),
            child: const Text('删除记录和文件'),
          ),
        ],
      ),
    );
    if (result == null) return;
    final deleteFiles = result == 'files';
    if (job == null) {
      await state.deleteAllJobs(deleteFiles: deleteFiles);
    } else {
      await state.deleteJob(job, deleteFiles: deleteFiles);
    }
  }

  void _showJobMenu(Job job) {
    showModalBottomSheet<void>(
      context: context,
      builder: (context) => SafeArea(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            ListTile(
              title: Text(job.displayTitle,
                  maxLines: 1, overflow: TextOverflow.ellipsis),
              subtitle: Text(stateText(job.state)),
            ),
            ListTile(
              leading: const Icon(Icons.delete, color: Colors.redAccent),
              title: const Text('删除任务'),
              onTap: () {
                Navigator.pop(context);
                _confirmDelete(job: job);
              },
            ),
            ListTile(
              leading: const Icon(Icons.close),
              title: const Text('取消'),
              onTap: () => Navigator.pop(context),
            ),
          ],
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final active = state.activeJob;

    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        Card(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Expanded(
                      child: Text('任务历史',
                          style: Theme.of(context).textTheme.titleMedium),
                    ),
                    TextButton(
                      onPressed: state.jobs.isEmpty
                          ? null
                          : () => _confirmDelete(),
                      style: TextButton.styleFrom(
                          foregroundColor: Colors.redAccent),
                      child: const Text('删除全部'),
                    ),
                  ],
                ),
                const Text('长按任务可删除单个任务。',
                    style: TextStyle(fontSize: 12, color: Colors.white70)),
                const SizedBox(height: 8),
                if (state.jobs.isEmpty)
                  const Padding(
                    padding: EdgeInsets.symmetric(vertical: 16),
                    child: Text('暂无任务',
                        style: TextStyle(color: Colors.white70)),
                  ),
                for (final job in state.jobs)
                  Material(
                    color: active?.jobId == job.jobId
                        ? Theme.of(context).colorScheme.primaryContainer
                        : Colors.transparent,
                    borderRadius: BorderRadius.circular(8),
                    child: InkWell(
                      borderRadius: BorderRadius.circular(8),
                      onTap: () => state.loadJobDetail(job.jobId),
                      onLongPress: () => _showJobMenu(job),
                      child: Padding(
                        padding: const EdgeInsets.symmetric(
                            horizontal: 10, vertical: 8),
                        child: Row(
                          children: [
                            Expanded(
                              child: Text(job.displayTitle,
                                  maxLines: 1,
                                  overflow: TextOverflow.ellipsis),
                            ),
                            Text(
                              '${stateText(job.state)} · ${job.percent.round()}%',
                              style: const TextStyle(
                                  fontSize: 12, color: Colors.white70),
                            ),
                          ],
                        ),
                      ),
                    ),
                  ),
              ],
            ),
          ),
        ),
        const SizedBox(height: 16),
        Card(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: active == null
                ? const Text('选择一个任务查看详情',
                    style: TextStyle(color: Colors.white70))
                : Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Row(
                        children: [
                          Expanded(
                            child: Text(active.displayTitle,
                                style:
                                    Theme.of(context).textTheme.titleMedium),
                          ),
                          Chip(
                            label: Text(stateText(active.state),
                                style: const TextStyle(fontSize: 11)),
                            visualDensity: VisualDensity.compact,
                          ),
                        ],
                      ),
                      const SizedBox(height: 8),
                      LinearProgressIndicator(
                          value: (active.percent / 100).clamp(0.0, 1.0)),
                      const SizedBox(height: 8),
                      Text(
                        active.progressText.isNotEmpty
                            ? active.progressText
                            : (active.error.isNotEmpty
                                ? active.error
                                : active.outputDir),
                        style: const TextStyle(
                            fontSize: 12, color: Colors.white70),
                      ),
                      if (active.state == 'waiting_input') ...[
                        const SizedBox(height: 12),
                        Text(active.prompt),
                        const SizedBox(height: 8),
                        Row(
                          children: [
                            Expanded(
                              child: TextField(
                                controller: _input,
                                obscureText: active.promptSecret,
                                decoration: const InputDecoration(
                                  border: OutlineInputBorder(),
                                  isDense: true,
                                ),
                                onSubmitted: (_) => _sendInput(active),
                              ),
                            ),
                            const SizedBox(width: 8),
                            FilledButton(
                                onPressed: () => _sendInput(active),
                                child: const Text('提交')),
                          ],
                        ),
                      ],
                      const SizedBox(height: 4),
                      const Text('详细日志请到"设置 - 日志"查看。',
                          style: TextStyle(
                              fontSize: 12, color: Colors.white70)),
                      const SizedBox(height: 12),
                      Wrap(
                        spacing: 8,
                        children: [
                          if (active.isActive)
                            OutlinedButton(
                              onPressed: () => state.jobAction(active, 'pause'),
                              child: const Text('暂停'),
                            ),
                          if (active.state == 'paused')
                            FilledButton(
                              onPressed: () =>
                                  state.jobAction(active, 'resume'),
                              child: const Text('继续'),
                            ),
                          OutlinedButton(
                            onPressed: () => _confirmCancel(active),
                            style: OutlinedButton.styleFrom(
                                foregroundColor: Colors.redAccent),
                            child: const Text('取消'),
                          ),
                          OutlinedButton(
                            onPressed: () => state.jobAction(active, 'retry'),
                            child: const Text('重试'),
                          ),
                        ],
                      ),
                    ],
                  ),
          ),
        ),
      ],
    );
  }

  Future<void> _sendInput(Job job) async {
    try {
      await state.api.jobInput(job.jobId, _input.text);
      _input.clear();
      await state.loadJobDetail(job.jobId);
    } catch (e) {
      state.showToast(e.toString());
    }
  }
}
