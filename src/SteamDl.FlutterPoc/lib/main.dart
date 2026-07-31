import 'dart:convert';
import 'dart:io';

import 'package:flutter/material.dart';

void main() {
  runApp(const SteamDlFlutterPocApp());
}

class SteamDlFlutterPocApp extends StatelessWidget {
  const SteamDlFlutterPocApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'SteamDl Flutter PoC',
      theme: ThemeData(colorSchemeSeed: Colors.blue, useMaterial3: true),
      home: const SteamDlFlutterPocPage(),
    );
  }
}

class SteamDlFlutterPocPage extends StatefulWidget {
  const SteamDlFlutterPocPage({super.key});

  @override
  State<SteamDlFlutterPocPage> createState() => _SteamDlFlutterPocPageState();
}

class _SteamDlFlutterPocPageState extends State<SteamDlFlutterPocPage> {
  String _status = '未请求本地服务';
  bool _loading = false;

  Future<void> _checkService() async {
    setState(() {
      _loading = true;
      _status = '正在请求 http://127.0.0.1:8630/api/config ...';
    });

    try {
      final client = HttpClient();
      final request = await client.getUrl(Uri.parse('http://127.0.0.1:8630/api/config'));
      final response = await request.close();
      final body = await response.transform(utf8.decoder).join();
      client.close(force: true);
      final preview = body.length > 1200 ? '${body.substring(0, 1200)}...' : body;
      setState(() => _status = 'HTTP ${response.statusCode}\n\n$preview');
    } catch (e) {
      setState(() => _status = '请求失败：$e');
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('SteamDl Flutter PoC')),
      body: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            const Text('这是由 .NET-for-Android 宿主启动的 Flutter add-to-app 页面。'),
            const SizedBox(height: 16),
            FilledButton(
              onPressed: _loading ? null : _checkService,
              child: Text(_loading ? '请求中...' : '检查本地 .NET 服务'),
            ),
            const SizedBox(height: 16),
            Expanded(
              child: SingleChildScrollView(
                child: SelectableText(_status),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
