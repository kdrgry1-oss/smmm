/// POST /api/auth/login yanıtı (backend AuthResponse DTO'su ile birebir).
class AuthResponse {
  AuthResponse({
    required this.token,
    required this.role,
    required this.tenantId,
    required this.fullName,
  });

  final String token;
  final String role;
  final String? tenantId;
  final String fullName;

  factory AuthResponse.fromJson(Map<String, dynamic> json) => AuthResponse(
        token: json['token'] as String,
        role: json['role'] as String? ?? '',
        tenantId: json['tenantId'] as String?,
        fullName: json['fullName'] as String? ?? '',
      );
}
