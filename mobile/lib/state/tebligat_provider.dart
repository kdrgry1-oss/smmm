import 'package:flutter/foundation.dart';

import '../api/api_client.dart';
import '../models/tebligat.dart';

/// e-Tebligat tek pano: özet sayaçlar + filtreli liste + okundu işaretleme + senkron.
/// Sıralama backend'den gelir: en yakın son gün üstte (aciller başta), süresizler sonda.
class TebligatProvider extends ChangeNotifier {
  TebligatProvider(this.api);

  final ApiClient api;

  List<TebligatItem> items = [];
  int totalCount = 0;
  int unreadCount = 0;
  int urgentCount = 0;

  bool onlyUnread = false;
  bool onlyUrgent = false;

  bool loading = false;
  bool syncing = false;
  String? error;
  bool _loadedOnce = false;

  bool get hasData => _loadedOnce;

  /// GET /api/etebligat?onlyUnread=&onlyUrgent=
  Future<void> load() async {
    loading = true;
    error = null;
    notifyListeners();

    try {
      final json = await api.get('/api/etebligat', query: {
        if (onlyUnread) 'onlyUnread': 'true',
        if (onlyUrgent) 'onlyUrgent': 'true',
      });
      final result = TebligatList.fromJson(json as Map<String, dynamic>);
      items = result.items;
      totalCount = result.totalCount;
      unreadCount = result.unreadCount;
      urgentCount = result.urgentCount;
      _loadedOnce = true;
    } on ApiException catch (e) {
      error = e.message;
    }

    loading = false;
    notifyListeners();
  }

  Future<void> setFilters({bool? unread, bool? urgent}) async {
    if (unread != null) onlyUnread = unread;
    if (urgent != null) onlyUrgent = urgent;
    await load();
  }

  /// POST /api/etebligat/{id}/mark-read — güncel satırla listeyi yamalar.
  Future<void> markRead(TebligatItem item) async {
    if (item.isRead) return;

    final json = await api.post('/api/etebligat/${item.id}/mark-read');
    final updated = TebligatItem.fromJson(json as Map<String, dynamic>);

    final index = items.indexWhere((m) => m.id == updated.id);
    if (index >= 0) {
      // "Yalnız okunmamışlar" filtresi açıksa okunan satır listeden düşer.
      if (onlyUnread) {
        items.removeAt(index);
      } else {
        items[index] = updated;
      }
    }
    if (unreadCount > 0) unreadCount -= 1;
    notifyListeners();
  }

  /// POST /api/etebligat/sync — tüm aktif mükellefler taranır; Türkçe özet döner.
  Future<String> sync() async {
    syncing = true;
    notifyListeners();

    try {
      final json = await api.post('/api/etebligat/sync');
      final message =
          (json as Map<String, dynamic>)['message'] as String? ?? 'Senkronizasyon tamamlandı.';
      await load();
      return message;
    } finally {
      syncing = false;
      notifyListeners();
    }
  }
}
