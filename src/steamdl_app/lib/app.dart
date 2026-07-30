import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import 'app_theme.dart';
import 'engine.dart';
import 'pages/accounts_page.dart';
import 'pages/download_page.dart';
import 'pages/jobs_page.dart';
import 'pages/library_page.dart';
import 'pages/settings_page.dart';
import 'state_controllers.dart';
import 'common_widgets.dart';

class SteamDlApp extends StatefulWidget {
  const SteamDlApp({super.key});

  @override
  State<SteamDlApp> createState() => _SteamDlAppState();
}

class _SteamDlAppState extends State<SteamDlApp> {
  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) {
      final app = context.read<AppController>();
      app.start();
      Future<void>.delayed(
          const Duration(seconds: 2), requestPlatformPermissions);
    });
  }

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'SteamDl',
      debugShowCheckedModeBanner: false,
      theme: AppTheme.dark(),
      home: const HomeShell(),
    );
  }
}

class HomeShell extends StatefulWidget {
  const HomeShell({super.key});

  @override
  State<HomeShell> createState() => _HomeShellState();
}

class _HomeShellState extends State<HomeShell> {
  int _index = 0;

  static const _tabs = [
    (icon: Icons.person, label: '账号'),
    (icon: Icons.download, label: '下载'),
    (icon: Icons.grid_view, label: '游戏库'),
    (icon: Icons.list_alt, label: '任务'),
    (icon: Icons.settings, label: '设置'),
  ];

  void _openTab(int index) {
    final auth = context.read<AuthController>();
    final ui = context.read<UiController>();
    if (!auth.loggedIn && (index == 1 || index == 2)) {
      ui.showToast('请先在账号页面完成登录');
      setState(() => _index = 0);
      return;
    }
    setState(() => _index = index);
  }

  @override
  Widget build(BuildContext context) {
    final app = context.watch<AppController>();
    final auth = context.watch<AuthController>();
    final ui = context.watch<UiController>();
    final wide = MediaQuery.of(context).size.width >= 760;

    if (app.engineStarting) {
      return const Scaffold(
        body: Center(
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              CircularProgressIndicator(),
              SizedBox(height: 16),
              Text('正在启动下载引擎…'),
            ],
          ),
        ),
      );
    }

    if (app.engineError.isNotEmpty && !app.serviceOnline) {
      return Scaffold(
        body: Center(
          child: Padding(
            padding: const EdgeInsets.all(24),
            child: Column(
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                const Icon(Icons.error_outline, size: 48, color: Colors.orange),
                const SizedBox(height: 16),
                Text('引擎启动失败：${app.engineError}', textAlign: TextAlign.center),
                const SizedBox(height: 16),
                FilledButton(onPressed: app.start, child: const Text('重试')),
              ],
            ),
          ),
        ),
      );
    }

    final pages = [
      const AccountsPage(),
      auth.loggedIn
          ? DownloadPage(onJobCreated: () => _openTab(3))
          : _LoginGate(onGoAccounts: () => _openTab(0)),
      auth.loggedIn
          ? LibraryPage(onPickGame: () => _openTab(1))
          : _LoginGate(onGoAccounts: () => _openTab(0)),
      const JobsPage(),
      const SettingsPage(),
    ];

    final body = Stack(
      children: [
        DecoratedBox(
          decoration: const BoxDecoration(
            gradient: LinearGradient(
              begin: Alignment.topCenter,
              end: Alignment.bottomCenter,
              colors: [Color(0xFF132233), Color(0xFF101923)],
            ),
          ),
          child: IndexedStack(index: _index, children: pages),
        ),
        ToastOverlay(message: ui.toast, onDismiss: ui.clearToast),
        if (!app.serviceOnline && !app.engineStarting)
          Positioned(
            bottom: 8,
            left: 16,
            right: 16,
            child: Center(
              child: Material(
                color: Theme.of(context).colorScheme.errorContainer,
                borderRadius: BorderRadius.circular(8),
                child: Padding(
                  padding:
                      const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
                  child: Text(
                    '引擎服务离线，正在自动重连…',
                    style: TextStyle(
                        color: Theme.of(context).colorScheme.onErrorContainer,
                        fontSize: 12),
                  ),
                ),
              ),
            ),
          ),
      ],
    );

    if (wide) {
      return Scaffold(
        body: Row(
          children: [
            NavigationRail(
              selectedIndex: _index,
              onDestinationSelected: _openTab,
              labelType: NavigationRailLabelType.all,
              leading: Padding(
                padding: const EdgeInsets.symmetric(vertical: 16),
                child: Column(
                  children: [
                    const CircleAvatar(
                      backgroundColor: AppTheme.seed,
                      foregroundColor: Colors.black,
                      child: Text('SD',
                          style: TextStyle(fontWeight: FontWeight.w800)),
                    ),
                    const SizedBox(height: 8),
                    SizedBox(
                      width: 86,
                      child: Text(
                        auth.loggedIn ? auth.selectedAccount : '未登录',
                        textAlign: TextAlign.center,
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                        style: const TextStyle(fontSize: 11),
                      ),
                    ),
                  ],
                ),
              ),
              destinations: [
                for (final tab in _tabs)
                  NavigationRailDestination(
                      icon: Icon(tab.icon), label: Text(tab.label)),
              ],
            ),
            const VerticalDivider(width: 1),
            Expanded(child: body),
          ],
        ),
      );
    }

    return Scaffold(
      body: SafeArea(child: body),
      bottomNavigationBar: NavigationBar(
        selectedIndex: _index,
        onDestinationSelected: _openTab,
        destinations: [
          for (final tab in _tabs)
            NavigationDestination(icon: Icon(tab.icon), label: tab.label),
        ],
      ),
    );
  }
}

class _LoginGate extends StatelessWidget {
  final VoidCallback onGoAccounts;

  const _LoginGate({required this.onGoAccounts});

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            const Icon(Icons.lock_outline, size: 48),
            const SizedBox(height: 12),
            Text('需要先登录账号', style: Theme.of(context).textTheme.titleLarge),
            const SizedBox(height: 8),
            const Text(
              'SteamDl 会先完成账号登录并保存 refresh token，再允许解析链接、访问游戏库和创建下载任务。',
              textAlign: TextAlign.center,
            ),
            const SizedBox(height: 16),
            FilledButton(onPressed: onGoAccounts, child: const Text('去账号页面')),
          ],
        ),
      ),
    );
  }
}
