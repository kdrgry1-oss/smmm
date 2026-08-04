import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:intl/date_symbol_data_local.dart';
import 'package:intl/intl.dart';
import 'package:provider/provider.dart';

import 'api/api_client.dart';
import 'config.dart';
import 'screens/home_screen.dart';
import 'screens/login_screen.dart';
import 'state/auth_provider.dart';
import 'state/clients_provider.dart';
import 'state/dashboard_provider.dart';
import 'state/receipts_provider.dart';
import 'state/tebligat_provider.dart';
import 'state/upload_queue_provider.dart';
import 'theme.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();

  // tr_TR tarih/para biçimlendirme verisi.
  Intl.defaultLocale = 'tr_TR';
  await initializeDateFormatting('tr_TR');

  const storage = FlutterSecureStorage(
    aOptions: AndroidOptions(encryptedSharedPreferences: true),
  );

  final config = AppConfig(storage);
  await config.load();

  final api = ApiClient(config);
  final auth = AuthProvider(api: api, storage: storage);
  api.onUnauthorized = auth.handleUnauthorized;
  await auth.init();

  runApp(MaliMusavirApp(config: config, api: api, auth: auth));
}

class MaliMusavirApp extends StatelessWidget {
  const MaliMusavirApp({
    super.key,
    required this.config,
    required this.api,
    required this.auth,
  });

  final AppConfig config;
  final ApiClient api;
  final AuthProvider auth;

  @override
  Widget build(BuildContext context) {
    return MultiProvider(
      providers: [
        Provider<AppConfig>.value(value: config),
        Provider<ApiClient>.value(value: api),
        ChangeNotifierProvider<AuthProvider>.value(value: auth),
        ChangeNotifierProvider(create: (_) => DashboardProvider(api)),
        ChangeNotifierProvider(create: (_) => ClientsProvider(api)),
        ChangeNotifierProvider(create: (_) => ReceiptsProvider(api)),
        ChangeNotifierProvider(create: (_) => TebligatProvider(api)),
        ChangeNotifierProvider(create: (_) => UploadQueueProvider(api)),
      ],
      child: MaterialApp(
        title: 'Mali Müşavir',
        debugShowCheckedModeBanner: false,
        theme: buildAppTheme(),
        locale: const Locale('tr', 'TR'),
        supportedLocales: const [Locale('tr', 'TR')],
        localizationsDelegates: const [
          GlobalMaterialLocalizations.delegate,
          GlobalWidgetsLocalizations.delegate,
          GlobalCupertinoLocalizations.delegate,
        ],
        home: Consumer<AuthProvider>(
          builder: (context, auth, _) {
            if (!auth.initialized) {
              return const Scaffold(
                body: Center(child: CircularProgressIndicator()),
              );
            }
            // Oturum yoksa (veya 401 ile düştüyse) login; varsa ana kabuk.
            // HomeShell her girişte yeniden kurulur → sekmeler taze veri yükler.
            return auth.loggedIn
                ? const HomeShell(key: ValueKey('home'))
                : const LoginScreen(key: ValueKey('login'));
          },
        ),
      ),
    );
  }
}
