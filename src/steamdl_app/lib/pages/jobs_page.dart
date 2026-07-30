// 任务页：任务历史列表 + 详情 + 暂停/继续/取消/重试/删除。
import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../models.dart';
import '../state_controllers.dart';
import '../common_widgets.dart';

class JobsPage extends StatefulWidget {
  const JobsPage({super.key});

  @override
  State<JobsPage> createState() => _JobsPageState();
}

class _JobsPageState extends State<JobsPage> {
  final _input = TextEditingController();

  @override
  void dispose() {
    _input.dispose();
    super.dispose();
  }

  Future<void> _confirmCancel(JobsController jobs, Job job) async {
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
    if (ok == true) await jobs.action(job, 'cancel');
  }

  Future<void> _confirmDelete(JobsController jobs, {Job? job}) async {
    final isAll = job == null;
    if (isAll && jobs.jobs.isEmpty) return;
    final result = await confirmDeleteChoice(
      context,
      title: '确认删除',
      message: isAll
          ? '要删除全部 ${jobs.jobs.length} 个任务记录吗？'
          : '要删除任务“${job.displayTitle}”吗？',
    );
    if (result == null) return;
    final deleteFiles = result == 'files';
    if (job == null) {
      await jobs.deleteAll(deleteFiles: deleteFiles);
    } else {
      await jobs.deleteJob(job, deleteFiles: deleteFiles);
    }
  }

  void _showJobMenu(JobsController jobs, Job job) {
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
                _confirmDelete(jobs, job: job);
              },
            ),
            ListTile(
                leading: const Icon(Icons.close),
                title: const Text('取消'),
                onTap: () => Navigator.pop(context)),
          ],
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final jobs = context.watch<JobsController>();
    final active = jobs.activeJob;
    final wide = MediaQuery.of(context).size.width >= 900;

    final list = AppCard(
      title: '任务历史',
      subtitle: '长按任务可删除单个任务。',
      trailing: TextButton(
        onPressed: jobs.jobs.isEmpty ? null : () => _confirmDelete(jobs),
        style: TextButton.styleFrom(foregroundColor: Colors.redAccent),
        child: const Text('删除全部'),
      ),
      children: [
        if (jobs.jobs.isEmpty)
          const Padding(
            padding: EdgeInsets.symmetric(vertical: 16),
            child: Text('暂无任务', style: TextStyle(color: Colors.white70)),
          ),
        for (final job in jobs.jobs)
          JobTile(
            job: job,
            selected: active?.jobId == job.jobId,
            onTap: () => jobs.loadJobDetail(job.jobId),
            onLongPress: () => _showJobMenu(jobs, job),
          ),
      ],
    );

    final detail = _JobDetailCard(
      job: active,
      input: _input,
      onInput: active == null
          ? null
          : () async {
              await jobs.submitInput(active, _input.text);
              _input.clear();
            },
      onPause: active == null ? null : () => jobs.action(active, 'pause'),
      onResume: active == null ? null : () => jobs.action(active, 'resume'),
      onCancel: active == null ? null : () => _confirmCancel(jobs, active),
      onRetry: active == null ? null : () => jobs.action(active, 'retry'),
      onDelete: active == null ? null : () => _confirmDelete(jobs, job: active),
    );

    if (wide) {
      return Padding(
        padding: const EdgeInsets.all(16),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Expanded(child: SingleChildScrollView(child: list)),
            const SizedBox(width: 16),
            Expanded(child: SingleChildScrollView(child: detail)),
          ],
        ),
      );
    }

    return PageFrame(children: [list, const SizedBox(height: 16), detail]);
  }
}

class _JobDetailCard extends StatelessWidget {
  final Job? job;
  final TextEditingController input;
  final VoidCallback? onInput;
  final VoidCallback? onPause;
  final VoidCallback? onResume;
  final VoidCallback? onCancel;
  final VoidCallback? onRetry;
  final VoidCallback? onDelete;

  const _JobDetailCard({
    required this.job,
    required this.input,
    this.onInput,
    this.onPause,
    this.onResume,
    this.onCancel,
    this.onRetry,
    this.onDelete,
  });

  @override
  Widget build(BuildContext context) {
    final active = job;
    if (active == null) {
      return const EmptyState(
        icon: Icons.list_alt,
        title: '任务详情',
        message: '选择一个任务查看进度、日志摘要和可用操作。',
      );
    }

    return AppCard(
      title: active.displayTitle,
      trailing: StatusBadge(stateText(active.state)),
      children: [
        LinearProgressIndicator(value: (active.percent / 100).clamp(0.0, 1.0)),
        const SizedBox(height: 8),
        Text(
          active.progressText.isNotEmpty
              ? active.progressText
              : (active.error.isNotEmpty ? active.error : active.outputDir),
          style: const TextStyle(fontSize: 12, color: Colors.white70),
        ),
        if (active.state == 'waiting_input') ...[
          const SizedBox(height: 12),
          Text(active.prompt),
          const SizedBox(height: 8),
          Row(
            children: [
              Expanded(
                child: TextField(
                  controller: input,
                  obscureText: active.promptSecret,
                  onSubmitted: (_) => onInput?.call(),
                ),
              ),
              const SizedBox(width: 8),
              FilledButton(onPressed: onInput, child: const Text('提交')),
            ],
          ),
        ],
        const SizedBox(height: 4),
        const Text('详细日志请到“设置 - 日志”查看。',
            style: TextStyle(fontSize: 12, color: Colors.white70)),
        const SizedBox(height: 12),
        Wrap(
          spacing: 8,
          runSpacing: 8,
          children: [
            if (active.isActive)
              OutlinedButton(onPressed: onPause, child: const Text('暂停')),
            if (active.state == 'paused')
              FilledButton(onPressed: onResume, child: const Text('继续')),
            OutlinedButton(
              onPressed: onCancel,
              style:
                  OutlinedButton.styleFrom(foregroundColor: Colors.redAccent),
              child: const Text('取消'),
            ),
            OutlinedButton(onPressed: onRetry, child: const Text('重试')),
            OutlinedButton(onPressed: onDelete, child: const Text('删除')),
          ],
        ),
      ],
    );
  }
}
