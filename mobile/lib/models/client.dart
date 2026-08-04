/// GET /api/clients yanıtındaki mükellef (backend ClientResponse DTO'su ile birebir).
class Client {
  Client({
    required this.id,
    required this.title,
    required this.taxId,
    required this.taxOffice,
    required this.email,
    required this.phone,
    required this.address,
    required this.eInvoiceEnabled,
    required this.eArchiveEnabled,
    required this.integratorName,
    required this.isArchived,
    required this.createdAt,
  });

  final String id;
  final String title;
  final String taxId;
  final String? taxOffice;
  final String? email;
  final String? phone;
  final String? address;
  final bool eInvoiceEnabled;
  final bool eArchiveEnabled;
  final String? integratorName;
  final bool isArchived;
  final DateTime createdAt;

  factory Client.fromJson(Map<String, dynamic> json) => Client(
        id: json['id'] as String,
        title: json['title'] as String? ?? '',
        taxId: json['taxId'] as String? ?? '',
        taxOffice: json['taxOffice'] as String?,
        email: json['email'] as String?,
        phone: json['phone'] as String?,
        address: json['address'] as String?,
        eInvoiceEnabled: json['eInvoiceEnabled'] as bool? ?? false,
        eArchiveEnabled: json['eArchiveEnabled'] as bool? ?? false,
        integratorName: json['integratorName'] as String?,
        isArchived: json['isArchived'] as bool? ?? false,
        createdAt: DateTime.parse(json['createdAt'] as String),
      );
}
