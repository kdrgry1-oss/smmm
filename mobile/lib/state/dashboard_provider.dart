import 'package:flutter/foundation.dart';

import '../api/api_client.dart';
import '../models/tebligat.dart';

/// Ana ekran özet sayıları:
/// - mükellef sayısı  → GET /api/clients (liste uzunluğu)
/// - bekleyen fiş     → GET /api/receipts?status=NeedsReview (totalCount)
/// - okunmamış/acil   → GET /api/etebligat (unreadCount / urgentCount)
class DashboardProvider extends ChangeNotifier {
  DashboardProvider(this.api);

  final ApiClient api;

  bool loading = false;
  String? error;
  bool _loadedOnce = false;

  int clientCount = 0;
  int pendingReceiptCount = 0;
  int unreadTebligatCount = 0;
  int urgentTebligatCount = 0;

  bool get hasData => _loadedOnce;

  Future<void> load() async {
    loading = true;
    error = null;
    notifyListeners();

    try {
      final results = await Future.wait([
        api.get('/api/clients'),
        api.get('/api/receipts',
            query: {'status': 'NeedsReview', 'page': '1', 'pageSize': '1'}),
        api.get('/api/etebligat'),
      ]);

      clientCount = (results[0] as List<dynamic>).length;
      pendingReceiptCount =
          ((results[1] as Map<String, dynamic>)['totalCount'] as num?)
                  ?.toInt() ??
              0;

      final tebligat =
          TebligatList.fromJson(results[2] as Map<String, dynamic>);
      unreadTebligatCount = tebligat.unreadCount;
      urgentTebligatCount = tebligat.urgentCount;

      _loadedOnce = true;
    } on ApiException catch (e) {
      error = e.message;
    }

    loading = false;
    notifyListeners();
  }
}
