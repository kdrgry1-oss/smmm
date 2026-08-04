/// e-Tebligat satırı (backend ETebligatItemResponse DTO'su ile birebir).
class TebligatItem {
  TebligatItem({
    required this.id,
    required this.clientId,
    required this.clientTitle,
    required this.sender,
    required this.subject,
    required this.deliveredAt,
    required this.legalDeadline,
    required this.deadlineType,
    required this.isRead,
    required this.readAt,
    required this.daysRemaining,
    required this.isUrgent,
    required this.createdAt,
  });

  final String id;
  final String clientId;
  final String? clientTitle;
  final String sender;
  final String subject;
  final DateTime deliveredAt;
  final DateTime? legalDeadline;

  /// "İtiraz" / "Ödeme" / "Bilgi".
  final String? deadlineType;
  final bool isRead;
  final DateTime? readAt;

  /// Son güne kalan gün; 0 ve altı = süre doldu, null = süresiz bilgilendirme.
  final int? daysRemaining;

  /// Kalan süre ≤ 3 gün (süresi geçmişler dahil) — kırmızı sayaç.
  final bool isUrgent;
  final DateTime createdAt;

  factory TebligatItem.fromJson(Map<String, dynamic> json) => TebligatItem(
        id: json['id'] as String,
        clientId: json['clientId'] as String,
        clientTitle: json['clientTitle'] as String?,
        sender: json['sender'] as String? ?? '',
        subject: json['subject'] as String? ?? '',
        deliveredAt: DateTime.parse(json['deliveredAt'] as String),
        legalDeadline: json['legalDeadline'] == null
            ? null
            : DateTime.parse(json['legalDeadline'] as String),
        deadlineType: json['deadlineType'] as String?,
        isRead: json['isRead'] as bool? ?? false,
        readAt: json['readAt'] == null
            ? null
            : DateTime.parse(json['readAt'] as String),
        daysRemaining: (json['daysRemaining'] as num?)?.toInt(),
        isUrgent: json['isUrgent'] as bool? ?? false,
        createdAt: DateTime.parse(json['createdAt'] as String),
      );
}

/// GET /api/etebligat yanıtı: özet sayaçlar + satırlar (aciller üstte gelir).
class TebligatList {
  TebligatList({
    required this.totalCount,
    required this.unreadCount,
    required this.urgentCount,
    required this.items,
  });

  final int totalCount;
  final int unreadCount;
  final int urgentCount;
  final List<TebligatItem> items;

  factory TebligatList.fromJson(Map<String, dynamic> json) => TebligatList(
        totalCount: (json['totalCount'] as num?)?.toInt() ?? 0,
        unreadCount: (json['unreadCount'] as num?)?.toInt() ?? 0,
        urgentCount: (json['urgentCount'] as num?)?.toInt() ?? 0,
        items: (json['items'] as List<dynamic>? ?? const [])
            .map((e) => TebligatItem.fromJson(e as Map<String, dynamic>))
            .toList(),
      );
}
