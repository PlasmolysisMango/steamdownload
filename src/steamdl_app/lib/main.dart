// SteamDl Flutter 原生 UI 入口。
// Provider 分层状态 + Flutter UI，通过 127.0.0.1 HTTP 与 .NET sidecar 通信。
import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import 'api_client.dart';
import 'app.dart';
import 'engine.dart';
import 'state_controllers.dart';

void main() {
  WidgetsFlutterBinding.ensureInitialized();

  final api = ApiClient();
  final engine = EngineController(api);
  final ui = UiController();
  final auth = AuthController(api, ui);
  final library = LibraryController(api, auth, ui);
  final jobs = JobsController(api, ui);
  final settings = SettingsController(api, ui);
  final app = AppController(
    api: api,
    engine: engine,
    ui: ui,
    auth: auth,
    jobs: jobs,
    library: library,
    settings: settings,
  );

  runApp(
    MultiProvider(
      providers: [
        ChangeNotifierProvider(create: (_) => ui),
        ChangeNotifierProvider(create: (_) => auth),
        ChangeNotifierProvider(create: (_) => library),
        ChangeNotifierProvider(create: (_) => jobs),
        ChangeNotifierProvider(create: (_) => settings),
        ChangeNotifierProvider(create: (_) => app),
      ],
      child: const SteamDlApp(),
    ),
  );
}
