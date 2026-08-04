import 'package:flutter_secure_storage/flutter_secure_storage.dart';

/// Uygulama yapılandırması: API taban adresi güvenli depoda saklanır.
///
/// Varsayılan adres Android emülatörü içindir (10.0.2.2 = host makinenin
/// localhost'u). iOS simülatöründe http://localhost:5000, gerçek cihazda
/// bilgisayarın yerel IP adresi kullanılmalıdır (Ayarlar ekranından değiştirilebilir).
class AppConfig {
  AppConfig(this._storage);

  static const String defaultApiBaseUrl = 'http://10.0.2.2:5000';
  static const String _apiUrlKey = 'mm_api_base_url';

  final FlutterSecureStorage _storage;

  String _apiBaseUrl = defaultApiBaseUrl;

  /// Sonunda / olmayan taban adres, ör. "http://10.0.2.2:5000".
  String get apiBaseUrl => _apiBaseUrl;

  Future<void> load() async {
    try {
      final stored = await _storage.read(key: _apiUrlKey);
      if (stored != null && stored.trim().isNotEmpty) {
        _apiBaseUrl = normalize(stored);
      }
    } catch (_) {
      // Depo okunamazsa varsayılan adresle devam edilir.
      _apiBaseUrl = defaultApiBaseUrl;
    }
  }

  Future<void> setApiBaseUrl(String value) async {
    _apiBaseUrl = normalize(value);
    await _storage.write(key: _apiUrlKey, value: _apiBaseUrl);
  }

  Future<void> resetApiBaseUrl() async {
    _apiBaseUrl = defaultApiBaseUrl;
    await _storage.delete(key: _apiUrlKey);
  }

  /// Baş/son boşlukları ve sondaki / işaretlerini temizler.
  static String normalize(String value) {
    var v = value.trim();
    while (v.endsWith('/')) {
      v = v.substring(0, v.length - 1);
    }
    return v;
  }

  /// http/https şemalı ve host içeren bir adres mi?
  static bool isValidBaseUrl(String value) {
    final uri = Uri.tryParse(normalize(value));
    return uri != null &&
        (uri.scheme == 'http' || uri.scheme == 'https') &&
        uri.host.isNotEmpty;
  }
}
