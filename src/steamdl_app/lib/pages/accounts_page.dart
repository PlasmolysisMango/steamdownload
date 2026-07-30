// 账号页：登录表单 + Guard/2FA 输入区 + 已保存账号列表。
import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../state_controllers.dart';
import '../common_widgets.dart';

class AccountsPage extends StatefulWidget {
  const AccountsPage({super.key});

  @override
  State<AccountsPage> createState() => _AccountsPageState();
}

class _AccountsPageState extends State<AccountsPage> {
  final _username = TextEditingController();
  final _password = TextEditingController();
  final _answer = TextEditingController();
  bool _rememberPassword = false;
  String _lastPrompt = '';

  @override
  void dispose() {
    _username.dispose();
    _password.dispose();
    _answer.dispose();
    super.dispose();
  }

  void _syncPrompt(AuthController auth) {
    final prompt = auth.loginState.prompt;
    if (auth.loginState.state != 'waiting_input' || prompt != _lastPrompt) {
      _answer.clear();
    }
    _lastPrompt = prompt;
    if (_username.text.isEmpty && auth.selectedAccount.isNotEmpty) {
      _username.text = auth.selectedAccount;
    }
  }

  Future<void> _login(AuthController auth, UiController ui) async {
    final username = _username.text.trim();
    if (username.isEmpty) {
      ui.showToast('请输入 Steam 用户名');
      return;
    }
    final detail = auth.detailMap[username.toLowerCase()];
    if (!auth.accounts.contains(username) &&
        detail?.hasSavedPassword != true &&
        _password.text.isEmpty) {
      ui.showToast('首次登录该账号需要输入密码');
      return;
    }
    final ok = await auth.login(username, _password.text, _rememberPassword);
    if (ok) _password.clear();
  }

  @override
  Widget build(BuildContext context) {
    final auth = context.watch<AuthController>();
    final ui = context.read<UiController>();
    _syncPrompt(auth);

    final login = auth.loginState;
    final busy = login.busy;
    final visibleAccounts = <String>{
      ...auth.accountDetails.map((d) => d.username),
      ...auth.accounts,
    }.toList()
      ..sort();

    return PageFrame(
      children: [
        AppCard(
          title: '新增并登录账号',
          subtitle: '登录成功后保存 refresh token。勾选记住密码后，token 失效时可一键或自动重新登录。',
          children: [
            TextField(
              controller: _username,
              decoration: const InputDecoration(labelText: 'Steam 用户名'),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _password,
              obscureText: true,
              decoration: const InputDecoration(
                labelText: '密码',
                hintText: '用于本次登录；勾选后加密保存',
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
                onPressed: busy ? null : () => _login(auth, ui),
                child: const Text('登录并保存授权')),
          ],
        ),
        if (busy || login.state == 'error') ...[
          const SizedBox(height: 16),
          AppCard(
            title: '登录状态',
            children: [
              if (busy)
                Row(
                  children: [
                    const SizedBox(
                        width: 16,
                        height: 16,
                        child: CircularProgressIndicator(strokeWidth: 2)),
                    const SizedBox(width: 8),
                    Expanded(
                        child: Text(
                            '登录中：${login.username.isNotEmpty ? login.username : _username.text}')),
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
                            hintText: 'Steam Guard / 2FA'),
                        onSubmitted: (_) => auth.submitLoginInput(_answer.text),
                      ),
                    ),
                    const SizedBox(width: 8),
                    FilledButton(
                        onPressed: () => auth.submitLoginInput(_answer.text),
                        child: const Text('提交验证')),
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
        AppCard(
          title: '已保存账号',
          subtitle:
              auth.loggedIn ? '当前账号：${auth.selectedAccount}' : '请选择或登录一个账号。',
          children: [
            if (visibleAccounts.isEmpty)
              const Text('暂无已保存账号。请先在上方完成登录和 Guard/2FA。',
                  style: TextStyle(color: Colors.white70)),
            for (final name in visibleAccounts)
              AccountTile(
                name: name,
                detail: auth.detailMap[name.toLowerCase()],
                loggedIn: auth.accounts.contains(name) ||
                    auth.detailMap[name.toLowerCase()]?.loggedIn == true,
                selected: auth.selectedAccount == name,
                onSelect: () => auth.setSelectedAccount(name),
                onRelogin: () => auth.relogin(name),
                onLogout: () => auth.logout(name),
              ),
          ],
        ),
      ],
    );
  }
}
