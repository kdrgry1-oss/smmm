import 'package:flutter/foundation.dart';
import 'package:image_picker/image_picker.dart';

import '../api/api_client.dart';
import '../models/client.dart';
import '../models/receipt.dart';

enum UploadJobStatus { waiting, uploading, success, error }

/// Kuyruktaki tek fiş yükleme işi. Her dosya ayrı istekle gönderilir:
/// böylece ilerleme ve "yeniden dene" dosya bazında çalışır.
class UploadJob {
  UploadJob({
    required this.filePath,
    required this.fileName,
    required this.clientId,
    required this.clientTitle,
  });

  final String filePath;
  final String fileName;
  final String clientId;
  final String clientTitle;

  UploadJobStatus status = UploadJobStatus.waiting;

  /// 0.0–1.0 arası yükleme ilerlemesi.
  double progress = 0;
  String? errorMessage;

  /// Başarılı yüklemede OCR sonucu (satıcı, tutar, KDV, önerilen hesap, güven).
  Receipt? receipt;
}

/// Fiş yükleme kuyruğu: işler sırayla (FIFO) gönderilir, hata alan iş
/// kuyrukta kalır ve "Yeniden Dene" ile tekrar sıraya girer.
class UploadQueueProvider extends ChangeNotifier {
  UploadQueueProvider(this.api);

  final ApiClient api;

  /// En yeni iş listenin başında gösterilir.
  final List<UploadJob> jobs = [];
  bool _processing = false;

  int get activeCount => jobs
      .where((j) =>
          j.status == UploadJobStatus.waiting ||
          j.status == UploadJobStatus.uploading)
      .length;

  bool get hasFinished =>
      jobs.any((j) => j.status == UploadJobStatus.success);

  /// Seçilen görselleri kuyruğa ekler ve işlemeyi başlatır.
  void enqueue({required Client client, required List<XFile> files}) {
    for (final file in files) {
      jobs.insert(
        0,
        UploadJob(
          filePath: file.path,
          fileName: file.name,
          clientId: client.id,
          clientTitle: client.title,
        ),
      );
    }
    notifyListeners();
    _process();
  }

  void retry(UploadJob job) {
    if (job.status != UploadJobStatus.error) return;
    job.status = UploadJobStatus.waiting;
    job.errorMessage = null;
    job.progress = 0;
    notifyListeners();
    _process();
  }

  void remove(UploadJob job) {
    if (job.status == UploadJobStatus.uploading) return;
    jobs.remove(job);
    notifyListeners();
  }

  void clearFinished() {
    jobs.removeWhere((j) => j.status == UploadJobStatus.success);
    notifyListeners();
  }

  Future<void> _process() async {
    if (_processing) return;
    _processing = true;
    try {
      while (true) {
        UploadJob? next;
        // FIFO: en eski bekleyen iş (liste sonuna doğru) önce gönderilir.
        for (final job in jobs.reversed) {
          if (job.status == UploadJobStatus.waiting) {
            next = job;
            break;
          }
        }
        if (next == null) break;
        await _upload(next);
      }
    } finally {
      _processing = false;
    }
  }

  Future<void> _upload(UploadJob job) async {
    job.status = UploadJobStatus.uploading;
    job.progress = 0;
    notifyListeners();

    try {
      final json = await api.uploadFiles(
        '/api/clients/${job.clientId}/receipts',
        filePaths: [job.filePath],
        onProgress: (p) {
          job.progress = p;
          notifyListeners();
        },
      );

      final result = ReceiptUploadResult.fromJson(json as Map<String, dynamic>);
      final item = result.items.isEmpty ? null : result.items.first;

      if (item != null && item.success && item.receipt != null) {
        job.receipt = item.receipt;
        job.status = UploadJobStatus.success;
        job.progress = 1;
      } else {
        job.status = UploadJobStatus.error;
        job.errorMessage =
            item?.message ?? 'Yükleme başarısız oldu. Lütfen tekrar deneyin.';
      }
    } on ApiException catch (e) {
      job.status = UploadJobStatus.error;
      job.errorMessage = e.message;
    }

    notifyListeners();
  }
}
