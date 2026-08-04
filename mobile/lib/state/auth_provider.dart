import 'package:flutter/foundation.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import '../api/api_client.dart';
import '../models/auth_response.dart';

/// Oturum durumu: JWT güvenli depoda saklanır; 401'de oturum düşürülüp
/// login ekranına dönülür (main.dart kök widget'ı loggedIn'i izler).
class AuthProvider extends ChangeNotifier {
  AuthProvider({required this.api, required this.storage});

  final ApiClient api;
  final FlutterSecureStorage storage;

  static const String _tokenKey = 'mm_token';
  static const String _nameKey = 'mm_full_name';
  static const String _roleKey = 'mm_role';

  bool _initialized = false;
  bool _loggedIn = false;
  bool _sessionExpired = false;
  String? _fullName;
  String? _role;

  bool get initialized => _initialized;
  bool get loggedIn => _loggedIn;

  /// Oturum 401 ile düştüyse login ekranında bilgi bandı gösterilir.
  bool get sessionExpired => _sessionExpired;
  String? get fullName => _fullName;
  String? get role => _role;

  /// Uygulama açılışında saklanan oturumu yükler.
  Future<void> init() async {
    try {
      final token = await storage.read(key: _tokenKey);
      _fullName = await storage.read(key: _nameKey);
      _role = await storage.read(key: _roleKey);
      if (token != null && token.isNotEmpty) {
        api.token = token;
        _loggedIn = true;
      }
    } catch (_) {
      // Depo okunamazsa oturumsuz başlanır.
      _loggedIn = false;
    }
    _initialized = true;
    notifyListeners();
  }

  /// POST /api/auth/login — başarısız girişte ApiException (Türkçe mesaj) fırlar.
  Future<void> login(String email, String password) async {
    final json = await api.post(
      '/api/auth/login',
      body: {'email': email.trim(), 'password': password},
      auth: false,
    );
    final auth = AuthResponse.fromJson(json as Map<String, dynamic>);

    api.token = auth.token;
    _fullName = auth.fullName;
    _role = auth.role;
    _loggedIn = true;
    _sessionExpired = false;

    await storage.write(key: _tokenKey, value: auth.token);
    await storage.write(key: _nameKey, value: auth.fullName);
    await storage.write(key: _roleKey, value: auth.role);

    notifyListeners();
  }

  Future<void> logout() async {
    api.token = null;
    _loggedIn = false;
    _fullName = null;
    _role = null;
    notifyListeners();

    try {
      await storage.delete(key: _tokenKey);
      await storage.delete(key: _nameKey);
      await storage.delete(key: _roleKey);
    } catch (_) {
      // Depo temizlenemese bile bellek içi oturum kapandı.
    }
  }

  /// ApiClient 401 aldığında çağrılır (oturum süresi doldu).
  void handleUnauthorized() {
    if (!_loggedIn) return;
    _sessionExpired = true;
    logout();
  }
}
