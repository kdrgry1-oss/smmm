import 'package:flutter/foundation.dart';

import '../api/api_client.dart';
import '../models/client.dart';

/// Mükellef listesi: arama (sunucu tarafı q) + yenileme.
class ClientsProvider extends ChangeNotifier {
  ClientsProvider(this.api);

  final ApiClient api;

  List<Client> clients = [];
  bool loading = false;
  String? error;
  String query = '';
  bool _loadedOnce = false;

  bool get hasData => _loadedOnce;

  /// GET /api/clients?q=... — q verilirse arama terimi güncellenir.
  Future<void> load({String? q}) async {
    if (q != null) query = q;
    loading = true;
    error = null;
    notifyListeners();

    try {
      final json = await api.get(
        '/api/clients',
        query: query.trim().isEmpty ? null : {'q': query.trim()},
      );
      clients = (json as List<dynamic>)
          .map((e) => Client.fromJson(e as Map<String, dynamic>))
          .toList();
      _loadedOnce = true;
    } on ApiException catch (e) {
      error = e.message;
    }

    loading = false;
    notifyListeners();
  }

  /// Fiş yükleme ekranındaki mükellef seçimi için ilk yükleme.
  Future<void> ensureLoaded() async {
    if (_loadedOnce || loading) return;
    await load();
  }
}
