import 'package:flutter/foundation.dart';

import '../api/api_client.dart';
import '../models/receipt.dart';

/// Fiş listesi: durum filtresi + sayfalama + onay/red işlemleri.
class ReceiptsProvider extends ChangeNotifier {
  ReceiptsProvider(this.api);

  final ApiClient api;

  static const int pageSize = 20;

  List<Receipt> items = [];

  /// null = tümü; aksi halde backend enum adı (NeedsReview, Approved...).
  String? statusFilter;
  int page = 1;
  int totalCount = 0;
  int totalPages = 0;
  bool loading = false;
  bool loadingMore = false;
  String? error;
  bool _loadedOnce = false;

  bool get hasData => _loadedOnce;
  bool get hasMore => page < totalPages;

  Map<String, String> _query(int targetPage) => {
        if (statusFilter != null) 'status': statusFilter!,
        'page': '$targetPage',
        'pageSize': '$pageSize',
      };

  /// GET /api/receipts — ilk sayfayı (yeniden) yükler.
  Future<void> load() async {
    loading = true;
    error = null;
    notifyListeners();

    try {
      final json = await api.get('/api/receipts', query: _query(1));
      final result = ReceiptPage.fromJson(json as Map<String, dynamic>);
      items = result.items;
      page = result.page;
      totalCount = result.totalCount;
      totalPages = result.totalPages;
      _loadedOnce = true;
    } on ApiException catch (e) {
      error = e.message;
    }

    loading = false;
    notifyListeners();
  }

  Future<void> setFilter(String? status) async {
    statusFilter = status;
    await load();
  }

  /// Sonraki sayfayı listeye ekler.
  Future<void> loadMore() async {
    if (loadingMore || !hasMore) return;
    loadingMore = true;
    notifyListeners();

    try {
      final json = await api.get('/api/receipts', query: _query(page + 1));
      final result = ReceiptPage.fromJson(json as Map<String, dynamic>);
      items = [...items, ...result.items];
      page = result.page;
      totalCount = result.totalCount;
      totalPages = result.totalPages;
    } on ApiException catch (e) {
      error = e.message;
    }

    loadingMore = false;
    notifyListeners();
  }

  void _replace(Receipt updated) {
    final index = items.indexWhere((r) => r.id == updated.id);
    if (index >= 0) {
      items[index] = updated;
      notifyListeners();
    }
  }

  /// POST /api/receipts/{id}/approve — düzeltme alanları body'de gönderilir.
  /// Hatada ApiException (Türkçe mesaj) fırlar; başarıda güncel fiş döner.
  Future<Receipt> approve(String id, Map<String, dynamic> body) async {
    final json = await api.post('/api/receipts/$id/approve', body: body);
    final updated = Receipt.fromJson(json as Map<String, dynamic>);
    _replace(updated);
    return updated;
  }

  /// POST /api/receipts/{id}/reject — gerekçe zorunlu.
  Future<Receipt> reject(String id, String reason) async {
    final json =
        await api.post('/api/receipts/$id/reject', body: {'reason': reason});
    final updated = Receipt.fromJson(json as Map<String, dynamic>);
    _replace(updated);
    return updated;
  }
}
