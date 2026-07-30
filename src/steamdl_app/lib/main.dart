// SteamDl Flutter 原生 UI 入口。
// 结构:5 个页签(账号/下载/游戏库/任务/设置),未登录时拦截下载与游戏库。
import 'package:flutter/material.dart';

import 'api_client.dart';
import 'app_state.dart';
import 'app_theme.dart';
import 'engine.dart';
import 'pages/accounts_page.dart';
import 'pages/download_page.dart';
import 'pages/jobs_page.dart';
import 'pages/library_page.dart';
import 'pages/settings_page.dart';

void main() {
  WidgetsFlutterBinding.ensureInitialized();
  final api = ApiClient();
  final state = AppState(api, EngineController(api));
  runApp(SteamDlApp(state: state));
}

class SteamDlApp extends StatefulWidget {
  final AppState state;

  const SteamDlApp({super.key, required this.state});

  @override
  State<SteamDlApp> createState() => _SteamDlAppState();
}

class _SteamDlAppState extends State<SteamDlApp> {
  @override
  void initState() {
    super.initState();
    widget.state.start();
    Future<void>.delayed(
        const Duration(seconds: 2), requestPlatformPermissions);
  }

  @override
  void dispose() {
    widget.state.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'SteamDl',
      debugShowCheckedModeBanner: false,
      theme: AppTheme.dark(),
      home: HomeShell(state: widget.state),
    );
  }
}

class HomeShell extends StatefulWidget {
  final AppState state;

  const HomeShell({super.key, required this.state});

  @override
  State<HomeShell> createState() => _HomeShellState();
}

class _HomeShellState extends State<HomeShell> {
  int _index = 0;

  AppState get state => widget.state;

  static const _tabs = [
    (icon: Icons.person, label: '账号'),
    (icon: Icons.download, label: '下载'),
    (icon: Icons.grid_view, label: '游戏库'),
    (icon: Icons.list_alt, label: '任务'),
    (icon: Icons.settings, label: '设置'),
  ];

  @override
  void initState() {
    super.initState();
    state.addListener(_onState);
  }

  @override
  void dispose() {
    state.removeListener(_onState);
    super.dispose();
  }

  void _onState() {
    if (mounted) setState(() {});
  }

  void _openTab(int index) {
    // 未登录时拦截下载(1)/游戏库(2)
    if (!state.loggedIn && (index == 1 || index == 2)) {
      state.showToast('请先在账号页面完成登录');
      setState(() => _index = 0);
      return;
    }
    setState(() => _index = index);
  }

  /// 从游戏库/解析结果跳到下载页
  void openDownloadTab() {
    setState(() => _index = 1);
  }

  /// 创建任务后跳到任务页
  void openJobsTab() {
    setState(() => _index = 3);
  }

  @override
  Widget build(BuildContext context) {
    final wide = MediaQuery.of(context).size.width >= 720;

    final pages = [
      AccountsPage(state: state),
      state.loggedIn
          ? DownloadPage(
              state: state,
              onJobCreated: openJobsTab,
            )
          : _LoginGate(onGoAccounts: () => _openTab(0)),
      state.loggedIn
          ? LibraryPage(state: state, onPickGame: openDownloadTab)
          : _LoginGate(onGoAccounts: () => _openTab(0)),
      JobsPage(state: state),
      SettingsPage(state: state),
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
        if (state.toast.isNotEmpty)
          Positioned(
            top: 12,
            left: 16,
            right: 16,
            child: Center(
              child: GestureDetector(
                onTap: state.clearToast,
                child: Material(
                  color: Theme.of(context)
                      .colorScheme
                      .inverseSurface
                      .withValues(alpha: 0.94),
                  borderRadius: BorderRadius.circular(10),
                  child: Padding(
                    padding:
                        const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
                    child: Text(state.toast,
                        style: const TextStyle(color: Colors.white)),
                  ),
                ),
              ),
            ),
          ),
        if (!state.serviceOnline && !state.engineStarting)
          Positioned(
            bottom: 8,
            left: 16,
            right: 16,
            child: Center(
              child: Material(
                color: Theme.of(context).colorScheme.errorContainer,
                borderRadius: BorderRadius.circular(8),
                child: Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
                  child: Text('引擎服务离线，正在自动重连…',
                      style: TextStyle(
                          color: Theme.of(context).colorScheme.onErrorContainer,
                          fontSize: 12)),
                ),
              ),
            ),
          ),
      ],
    );

    if (state.engineStarting) {
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

    if (state.engineError.isNotEmpty && !state.serviceOnline) {
      return Scaffold(
        body: Center(
          child: Padding(
            padding: const EdgeInsets.all(24),
            child: Column(
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                const Icon(Icons.error_outline, size: 48, color: Colors.orange),
                const SizedBox(height: 16),
                Text('引擎启动失败：${state.engineError}',
                    textAlign: TextAlign.center),
                const SizedBox(height: 16),
                FilledButton(
                  onPressed: () => state.start(),
                  child: const Text('重试'),
                ),
              ],
            ),
          ),
        ),
      );
    }

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
                    Text(
                      state.loggedIn ? state.selectedAccount : '未登录',
                      style: const TextStyle(fontSize: 11),
                    ),
                  ],
                ),
              ),
              destinations: [
                for (final tab in _tabs)
                  NavigationRailDestination(
                    icon: Icon(tab.icon),
                    label: Text(tab.label),
                  ),
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
            Text('需要先登录账号',
                style: Theme.of(context).textTheme.titleLarge),
            const SizedBox(height: 8),
            const Text(
              'SteamDl 会先完成账号登录并保存 refresh token，'
              '再允许解析链接、访问游戏库和创建下载任务。',
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
