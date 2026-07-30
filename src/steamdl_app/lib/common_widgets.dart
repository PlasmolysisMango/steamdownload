import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import 'app_theme.dart';
import 'models.dart';

class PageFrame extends StatelessWidget {
  final List<Widget> children;
  final EdgeInsetsGeometry padding;

  const PageFrame(
      {super.key,
      required this.children,
      this.padding = const EdgeInsets.all(16)});

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: padding,
      children: [
        Center(
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 1180),
            child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: children),
          ),
        ),
      ],
    );
  }
}

class AppCard extends StatelessWidget {
  final String title;
  final String? subtitle;
  final List<Widget> children;
  final Widget? trailing;

  const AppCard(
      {super.key,
      required this.title,
      this.subtitle,
      this.children = const [],
      this.trailing});

  @override
  Widget build(BuildContext context) {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(title,
                          style: Theme.of(context).textTheme.titleMedium),
                      if (subtitle != null) ...[
                        const SizedBox(height: 4),
                        Text(subtitle!,
                            style: Theme.of(context)
                                .textTheme
                                .bodySmall
                                ?.copyWith(
                                    color:
                                        Colors.white.withValues(alpha: 0.68))),
                      ],
                    ],
                  ),
                ),
                if (trailing != null) trailing!,
              ],
            ),
            if (children.isNotEmpty) ...[
              const SizedBox(height: 14),
              ...children
            ],
          ],
        ),
      ),
    );
  }
}

class EmptyState extends StatelessWidget {
  final IconData icon;
  final String title;
  final String message;
  final Widget? action;

  const EmptyState(
      {super.key,
      required this.icon,
      required this.title,
      required this.message,
      this.action});

  @override
  Widget build(BuildContext context) {
    return AppCard(
      title: title,
      children: [
        Center(
          child: Padding(
            padding: const EdgeInsets.symmetric(vertical: 18),
            child: Column(
              children: [
                Icon(icon,
                    size: 42, color: Colors.white.withValues(alpha: 0.55)),
                const SizedBox(height: 10),
                Text(message, textAlign: TextAlign.center),
                if (action != null) ...[const SizedBox(height: 12), action!],
              ],
            ),
          ),
        ),
      ],
    );
  }
}

class StatusBadge extends StatelessWidget {
  final String label;
  final Color? color;

  const StatusBadge(this.label, {super.key, this.color});

  @override
  Widget build(BuildContext context) {
    final c = color ?? Theme.of(context).colorScheme.primary;
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(
        color: c.withValues(alpha: 0.16),
        borderRadius: BorderRadius.circular(999),
        border: Border.all(color: c.withValues(alpha: 0.34)),
      ),
      child: Text(label,
          style:
              TextStyle(fontSize: 11, color: c, fontWeight: FontWeight.w700)),
    );
  }
}

class ToastOverlay extends StatelessWidget {
  final String message;
  final VoidCallback onDismiss;

  const ToastOverlay(
      {super.key, required this.message, required this.onDismiss});

  @override
  Widget build(BuildContext context) {
    if (message.isEmpty) return const SizedBox.shrink();
    return Positioned(
      top: 12,
      left: 16,
      right: 16,
      child: Center(
        child: GestureDetector(
          onTap: onDismiss,
          child: Material(
            color: Theme.of(context)
                .colorScheme
                .inverseSurface
                .withValues(alpha: 0.94),
            borderRadius: BorderRadius.circular(12),
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
              child: Text(message, style: const TextStyle(color: Colors.white)),
            ),
          ),
        ),
      ),
    );
  }
}

Future<String?> confirmDeleteChoice(BuildContext context,
    {required String title, required String message}) {
  return showDialog<String>(
    context: context,
    builder: (context) => AlertDialog(
      title: Text(title),
      content: Text(message),
      actions: [
        TextButton(
            onPressed: () => Navigator.pop(context), child: const Text('取消')),
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
}

class JobTile extends StatelessWidget {
  final Job job;
  final bool selected;
  final VoidCallback onTap;
  final VoidCallback? onLongPress;

  const JobTile(
      {super.key,
      required this.job,
      required this.selected,
      required this.onTap,
      this.onLongPress});

  @override
  Widget build(BuildContext context) {
    return Card(
      color: selected ? Theme.of(context).colorScheme.primaryContainer : null,
      child: ListTile(
        onTap: onTap,
        onLongPress: onLongPress,
        title: Text(job.displayTitle,
            maxLines: 1, overflow: TextOverflow.ellipsis),
        subtitle: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const SizedBox(height: 4),
            LinearProgressIndicator(value: (job.percent / 100).clamp(0.0, 1.0)),
            const SizedBox(height: 4),
            Text('${stateText(job.state)} · ${job.percent.round()}%'),
          ],
        ),
        trailing: const Icon(Icons.chevron_right),
      ),
    );
  }
}

class AccountTile extends StatelessWidget {
  final String name;
  final AccountDetail? detail;
  final bool loggedIn;
  final bool selected;
  final VoidCallback? onSelect;
  final VoidCallback? onRelogin;
  final VoidCallback? onLogout;

  const AccountTile(
      {super.key,
      required this.name,
      required this.detail,
      required this.loggedIn,
      required this.selected,
      this.onSelect,
      this.onRelogin,
      this.onLogout});

  @override
  Widget build(BuildContext context) {
    return Card(
      color: selected ? Theme.of(context).colorScheme.primaryContainer : null,
      child: ListTile(
        onTap: loggedIn ? onSelect : null,
        leading: CircleAvatar(
            child: Text(name.isEmpty ? '?' : name[0].toUpperCase())),
        title: Text(name, maxLines: 1, overflow: TextOverflow.ellipsis),
        subtitle: Text(
            '${loggedIn ? '已登录' : '需重登'} · ${detail?.hasSavedPassword == true ? '已记住密码' : '未保存密码'}'),
        trailing: Wrap(
          spacing: 6,
          children: [
            if (selected) const StatusBadge('当前'),
            if (detail?.hasSavedPassword == true)
              TextButton(onPressed: onRelogin, child: const Text('重登')),
            TextButton(
                onPressed: onLogout,
                style: TextButton.styleFrom(foregroundColor: Colors.redAccent),
                child: const Text('退出')),
          ],
        ),
      ),
    );
  }
}

class LogViewer extends StatelessWidget {
  final String value;
  final VoidCallback onRefresh;
  final VoidCallback onCopy;
  final List<DropdownMenuItem<String>> items;
  final String selected;
  final ValueChanged<String?> onChanged;
  final String? hint;

  const LogViewer(
      {super.key,
      required this.value,
      required this.onRefresh,
      required this.onCopy,
      required this.items,
      required this.selected,
      required this.onChanged,
      this.hint});

  @override
  Widget build(BuildContext context) {
    return AppCard(
      title: '日志',
      trailing: Wrap(
        spacing: 8,
        children: [
          TextButton(onPressed: onRefresh, child: const Text('刷新')),
          TextButton(
              onPressed: value.isEmpty ? null : onCopy,
              child: const Text('复制')),
        ],
      ),
      children: [
        DropdownButtonFormField<String>(
          value: selected,
          decoration: const InputDecoration(labelText: '选择日志'),
          items: items,
          onChanged: onChanged,
        ),
        if (hint != null && hint!.isNotEmpty) ...[
          const SizedBox(height: 8),
          SelectableText(hint!,
              style: const TextStyle(fontSize: 12, color: Colors.white70)),
        ],
        const SizedBox(height: 8),
        Container(
          width: double.infinity,
          constraints: const BoxConstraints(maxHeight: 360),
          padding: const EdgeInsets.all(10),
          decoration: BoxDecoration(
              color: Colors.black.withValues(alpha: 0.35),
              borderRadius: BorderRadius.circular(10)),
          child: SingleChildScrollView(
            child: SelectableText(
              value.isNotEmpty ? value : '暂无日志',
              style: const TextStyle(
                  fontFamily: 'Cascadia Mono',
                  fontFamilyFallback: AppTheme.monoFontFallback,
                  fontSize: 11.5,
                  height: 1.5),
            ),
          ),
        ),
      ],
    );
  }
}

Future<void> copyText(
    BuildContext context, String text, VoidCallback onDone) async {
  await Clipboard.setData(ClipboardData(text: text));
  onDone();
}
