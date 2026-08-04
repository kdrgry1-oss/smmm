double? _asDouble(dynamic value) =>
    value == null ? null : (value as num).toDouble();

DateTime? _asDate(dynamic value) =>
    value == null ? null : DateTime.parse(value as String);

/// Fiş kaydı (backend ReceiptResponse DTO'su ile birebir).
/// status: Uploaded / Processing / NeedsReview / Approved / Rejected.
class Receipt {
  Receipt({
    required this.id,
    required this.clientId,
    required this.clientTitle,
    required this.fileName,
    required this.status,
    required this.vendorName,
    required this.vendorTaxId,
    required this.receiptDate,
    required this.totalAmount,
    required this.vatRate,
    required this.vatAmount,
    required this.netAmount,
    required this.suggestedAccountCode,
    required this.accountCode,
    required this.cariCode,
    required this.confidence,
    required this.expenseEligible,
    required this.ineligibleReason,
    required this.rejectReason,
    required this.autoApproveReady,
    required this.isOldDocument,
    required this.createdAt,
  });

  final String id;
  final String clientId;
  final String? clientTitle;
  final String fileName;
  final String status;
  final String? vendorName;
  final String? vendorTaxId;
  final DateTime? receiptDate;
  final double? totalAmount;
  final double? vatRate;
  final double? vatAmount;
  final double? netAmount;
  final String? suggestedAccountCode;
  final String? accountCode;
  final String? cariCode;

  /// Birleşik güven 0-1: min(OCR güveni, kod önerisi güveni). Onaylı fişte 1.0.
  final double? confidence;
  final bool? expenseEligible;
  final String? ineligibleReason;
  final String? rejectReason;

  /// Güven eşiği üstü + gider gösterilebilir → "hızlı onaya hazır" rozeti.
  final bool autoApproveReady;

  /// Belge tarihi 30 günden eskiyse uyarı işareti.
  final bool isOldDocument;
  final DateTime createdAt;

  bool get canDecide => status == 'NeedsReview';

  factory Receipt.fromJson(Map<String, dynamic> json) => Receipt(
        id: json['id'] as String,
        clientId: json['clientId'] as String,
        clientTitle: json['clientTitle'] as String?,
        fileName: json['fileName'] as String? ?? '',
        status: json['status'] as String? ?? '',
        vendorName: json['vendorName'] as String?,
        vendorTaxId: json['vendorTaxId'] as String?,
        receiptDate: _asDate(json['receiptDate']),
        totalAmount: _asDouble(json['totalAmount']),
        vatRate: _asDouble(json['vatRate']),
        vatAmount: _asDouble(json['vatAmount']),
        netAmount: _asDouble(json['netAmount']),
        suggestedAccountCode: json['suggestedAccountCode'] as String?,
        accountCode: json['accountCode'] as String?,
        cariCode: json['cariCode'] as String?,
        confidence: _asDouble(json['confidence']),
        expenseEligible: json['expenseEligible'] as bool?,
        ineligibleReason: json['ineligibleReason'] as String?,
        rejectReason: json['rejectReason'] as String?,
        autoApproveReady: json['autoApproveReady'] as bool? ?? false,
        isOldDocument: json['isOldDocument'] as bool? ?? false,
        createdAt: DateTime.parse(json['createdAt'] as String),
      );
}

/// GET /api/receipts sayfalı yanıtı.
class ReceiptPage {
  ReceiptPage({
    required this.page,
    required this.pageSize,
    required this.totalCount,
    required this.totalPages,
    required this.items,
  });

  final int page;
  final int pageSize;
  final int totalCount;
  final int totalPages;
  final List<Receipt> items;

  factory ReceiptPage.fromJson(Map<String, dynamic> json) => ReceiptPage(
        page: (json['page'] as num?)?.toInt() ?? 1,
        pageSize: (json['pageSize'] as num?)?.toInt() ?? 20,
        totalCount: (json['totalCount'] as num?)?.toInt() ?? 0,
        totalPages: (json['totalPages'] as num?)?.toInt() ?? 0,
        items: (json['items'] as List<dynamic>? ?? const [])
            .map((e) => Receipt.fromJson(e as Map<String, dynamic>))
            .toList(),
      );
}

/// Çoklu yüklemede tek dosyanın sonucu (dosya bazlı Türkçe hata mesajı).
class ReceiptUploadItem {
  ReceiptUploadItem({
    required this.fileName,
    required this.success,
    required this.message,
    required this.receipt,
  });

  final String fileName;
  final bool success;
  final String? message;
  final Receipt? receipt;

  factory ReceiptUploadItem.fromJson(Map<String, dynamic> json) =>
      ReceiptUploadItem(
        fileName: json['fileName'] as String? ?? '',
        success: json['success'] as bool? ?? false,
        message: json['message'] as String?,
        receipt: json['receipt'] == null
            ? null
            : Receipt.fromJson(json['receipt'] as Map<String, dynamic>),
      );
}

/// POST /api/clients/{id}/receipts yanıtı.
class ReceiptUploadResult {
  ReceiptUploadResult({required this.message, required this.items});

  final String message;
  final List<ReceiptUploadItem> items;

  factory ReceiptUploadResult.fromJson(Map<String, dynamic> json) =>
      ReceiptUploadResult(
        message: json['message'] as String? ?? '',
        items: (json['items'] as List<dynamic>? ?? const [])
            .map((e) => ReceiptUploadItem.fromJson(e as Map<String, dynamic>))
            .toList(),
      );
}
