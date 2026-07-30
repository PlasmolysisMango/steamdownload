// 账号页:登录表单(用户名/密码/记住密码) + Guard/2FA 输入区 + 已保存账号列表。
// 布局遵循"登录区与任务状态区分离"的既有规范。
import 'package:flutter/material.dart';

import '../app_state.dart';
import '../models.dart';

class AccountsPage extends StatefulWidget {
  final AppState state;

  const AccountsPage({super.key, required this.state});

  @override
  State<AccountsPage> createState() => _AccountsPageState();
}

class _AccountsPageState extends State<AccountsPage> {
  final _username = TextEditingController();
  final _password = TextEditingController();
  final _answer = TextEditingController();
  bool _rememberPassword = false;
  String _lastPrompt = '';

  AppState get state => widget.state;

  @override
  void initState() {
    super.initState();
    _username.text = state.selectedAccount;
    state.addListener(_onState);
  }

  @override
  void dispose() {
    state.removeListener(_onState);
    _username.dispose();
    _password.dispose();
    _answer.dispose();
    super.dispose();
  }

  void _onState() {
    if (!mounted) return;
    // 提示变化(如验证码错误重试)时清空上次输入
    final prompt = state.loginState.prompt;
    if (state.loginState.state != 'waiting_input' || prompt != _lastPrompt) {
      _answer.clear();
    }
    _lastPrompt = prompt;
    setState(() {});
  }

  Map<String, AccountDetail> get _detailMap => {
        for (final d in state.accountDetails) d.username.toLowerCase(): d,
      };

  List<String> get _visibleAccounts {
    final names = <String>{
      ...state.accountDetails.map((d) => d.username),
      ...state.accounts,
    }.toList()
      ..sort();
    return names;
  }

  Future<void> _login() async {
    final username = _username.text.trim();
    if (username.isEmpty) {
      state.showToast('请输入 Steam 用户名');
      return;
    }
    final detail = _detailMap[username.toLowerCase()];
    if (!state.accounts.contains(username) &&
        detail?.hasSavedPassword != true &&
        _password.text.isEmpty) {
      state.showToast('首次登录该账号需要输入密码');
      return;
    }
    final ok = await state.login(username, _password.text, _rememberPassword);
    if (ok) _password.clear();
  }

  @override
  Widget build(BuildContext context) {
    final login = state.loginState;
    final busy = login.busy;

    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        _Card(
          title: '新增并登录账号',
          children: [
            const Text(
              '登录成功后会保存 refresh token。勾选记住密码后，token 失效时可一键或自动重新登录；密码仅本地加密保存。',
              style: TextStyle(fontSize: 12, color: Colors.white70),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _username,
              decoration: const InputDecoration(
                labelText: 'Steam 用户名',
                border: OutlineInputBorder(),
                isDense: true,
              ),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _password,
              obscureText: true,
              decoration: const InputDecoration(
                labelText: '密码',
                hintText: '用于本次登录；勾选后加密保存',
                border: OutlineInputBorder(),
                isDense: true,
              ),
            ),
            CheckboxListTile(
              value: _rememberPassword,
              onChanged: (v) => setState(() => _rememberPassword = v ?? false),
              title: const Text('记住密码，用于 token 失效后重新登录',
                  style: TextStyle(fontSize: 13)),
              controlAffinity: ListTileControlAffinity.leading,
              contentPadding: EdgeInsets.zero,
              dense: true,
            ),
            FilledButton(
              onPressed: busy ? null : _login,
              child: const Text('登录并保存授权'),
            ),
          ],
        ),
        if (busy || login.state == 'error') ...[
          const SizedBox(height: 16),
          _Card(
            title: '登录状态',
            children: [
              if (busy)
                Row(
                  children: [
                    const SizedBox(
                      width: 16,
                      height: 16,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    ),
                    const SizedBox(width: 8),
                    Text('登录中：${login.username.isNotEmpty ? login.username : _username.text}'),
                  ],
                ),
              if (login.state == 'waiting_input') ...[
                const SizedBox(height: 12),
                Text(login.prompt.isNotEmpty
                    ? login.prompt
                    : '请输入 Steam Guard / 2FA 验证码'),
                const SizedBox(height: 8),
                Row(
                  children: [
                    Expanded(
                      child: TextField(
                        controller: _answer,
                        obscureText: login.promptSecret,
                        decoration: const InputDecoration(
                          hintText: 'Steam Guard / 2FA',
                          border: OutlineInputBorder(),
                          isDense: true,
                        ),
                        onSubmitted: (_) =>
                            state.submitLoginInput(_answer.text),
                      ),
                    ),
                    const SizedBox(width: 8),
                    FilledButton(
                      onPressed: () => state.submitLoginInput(_answer.text),
                      child: const Text('提交验证'),
                    ),
                  ],
                ),
              ],
              if (login.state == 'error')
                Text('登录失败：${login.error}',
                    style: const TextStyle(color: Colors.orangeAccent)),
            ],
          ),
        ],
        const SizedBox(height: 16),
        _Card(
          title: '已保存账号',
          children: [
            if (state.loggedIn)
              Padding(
                padding: const EdgeInsets.only(bottom: 8),
                child: Chip(
                  label: Text('当前账号：${state.selectedAccount}'),
                  visualDensity: VisualDensity.compact,
                ),
              ),
            if (_visibleAccounts.isEmpty)
              const Text('暂无已保存账号。请先在上方完成登录和 Guard/2FA。',
                  style: TextStyle(color: Colors.white70)),
            for (final name in _visibleAccounts) _accountTile(name),
          ],
        ),
      ],
    );
  }

  Widget _accountTile(String name) {
    final detail = _detailMap[name.toLowerCase()];
    final loggedIn = state.accounts.contains(name) || detail?.loggedIn == true;
    final selected = state.selectedAccount == name;

    return Card(
      margin: const EdgeInsets.symmetric(vertical: 4),
      color: selected
          ? Theme.of(context).colorScheme.primaryContainer
          : null,
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(
                  child: InkWell(
                    onTap: loggedIn
                        ? () => state.setSelectedAccount(name)
                        : null,
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(name,
                            style: const TextStyle(
                                fontWeight: FontWeight.w600, fontSize: 15)),
                        Text(
                          '${loggedIn ? '已登录' : '需重登'} · ${detail?.hasSavedPassword == true ? '已记住密码' : '未保存密码'}',
                          style: const TextStyle(
                              fontSize: 12, color: Colors.white70),
                        ),
                      ],
                    ),
                  ),
                ),
                if (detail?.hasSavedPassword == true)
                  TextButton(
                    onPressed: () => state.relogin(name),
                    child: const Text('重新登录'),
                  ),
                TextButton(
                  onPressed: () => state.logout(name),
                  style: TextButton.styleFrom(foregroundColor: Colors.redAccent),
                  child: const Text('退出登录'),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

class _Card extends StatelessWidget {
  final String title;
  final List<Widget> children;

  const _Card({required this.title, required this.children});

  @override
  Widget build(BuildContext context) {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(title, style: Theme.of(context).textTheme.titleMedium),
            const SizedBox(height: 12),
            ...children,
          ],
        ),
      ),
    );
  }
}
