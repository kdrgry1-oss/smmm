import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:http/http.dart' as http;

import '../config.dart';

/// API hata sınıfı: kullanıcıya doğrudan gösterilebilir Türkçe mesaj taşır.
/// statusCode = 0 → ağ/zaman aşımı hatası.
class ApiException implements Exception {
  ApiException(this.statusCode, this.message);

  final int statusCode;
  final String message;

  @override
  String toString() => message;
}

/// HTTP istemcisi (web'deki lib/api.ts ile aynı davranış):
/// - Authorization: Bearer otomatik eklenir,
/// - 401 → token temizlenir, onUnauthorized tetiklenir (login'e dönüş),
/// - hata gövdesindeki { message } Türkçe mesajı ApiException olarak fırlatılır,
/// - multipart yükleme ilerleme yüzdesi verir.
class ApiClient {
  ApiClient(this.config);

  final AppConfig config;

  static const Duration _timeout = Duration(seconds: 20);
  static const Duration _uploadTimeout = Duration(minutes: 3);
  static const String networkErrorMessage =
      "Sunucuya ulaşılamıyor. İnternet bağlantınızı ve Ayarlar'daki API adresini kontrol edin.";
  static const String sessionExpiredMessage =
      'Oturumunuzun süresi doldu. Lütfen tekrar giriş yapın.';

  String? _token;

  /// Oturum süresi dolduğunda (401) çağrılır; AuthProvider login'e döndürür.
  void Function()? onUnauthorized;

  set token(String? value) => _token = value;
  bool get hasToken => _token != null && _token!.isNotEmpty;

  Uri _uri(String path, [Map<String, String>? query]) {
    final uri = Uri.parse('${config.apiBaseUrl}$path');
    if (query == null || query.isEmpty) return uri;
    return uri.replace(queryParameters: {...uri.queryParameters, ...query});
  }

  Map<String, String> _headers({bool jsonBody = false}) {
    final headers = <String, String>{
      'Accept': 'application/json',
      if (jsonBody) 'Content-Type': 'application/json; charset=utf-8',
      if (hasToken) 'Authorization': 'Bearer $_token',
    };
    return headers;
  }

  Future<dynamic> get(String path, {Map<String, String>? query}) {
    return _run(() => http.get(_uri(path, query), headers: _headers()));
  }

  Future<dynamic> post(
    String path, {
    Object? body,
    Map<String, String>? query,
    bool auth = true,
  }) {
    return _run(
      () => http.post(
        _uri(path, query),
        headers: _headers(jsonBody: true),
        body: body == null ? null : jsonEncode(body),
      ),
      auth: auth,
    );
  }

  Future<http.Response> _guard(Future<http.Response> Function() send,
      [Duration timeout = _timeout]) async {
    try {
      return await send().timeout(timeout);
    } on TimeoutException {
      throw ApiException(0, 'Sunucu yanıt vermedi. Lütfen tekrar deneyin.');
    } on SocketException {
      throw ApiException(0, networkErrorMessage);
    } on http.ClientException {
      throw ApiException(0, networkErrorMessage);
    } on HandshakeException {
      throw ApiException(0, networkErrorMessage);
    }
  }

  Future<dynamic> _run(Future<http.Response> Function() send,
      {bool auth = true}) async {
    final res = await _guard(send);
    return _handle(res, auth: auth);
  }

  /// Multipart dosya yükleme (alan adı: files). onProgress 0.0–1.0 arası ilerleme alır.
  Future<dynamic> uploadFiles(
    String path, {
    required List<String> filePaths,
    String field = 'files',
    void Function(double progress)? onProgress,
  }) async {
    final uri = _uri(path);

    final multipart = http.MultipartRequest('POST', uri);
    for (final filePath in filePaths) {
      try {
        multipart.files.add(await http.MultipartFile.fromPath(field, filePath));
      } on FileSystemException {
        throw ApiException(0, 'Dosya okunamadı. Lütfen görseli yeniden seçin.');
      }
    }

    final total = multipart.contentLength;
    final byteStream = multipart.finalize();

    var sent = 0;
    final progressStream = byteStream.transform<List<int>>(
      StreamTransformer.fromHandlers(
        handleData: (chunk, sink) {
          sent += chunk.length;
          if (total > 0 && onProgress != null) {
            onProgress(sent / total);
          }
          sink.add(chunk);
        },
      ),
    );

    final streamed = http.StreamedRequest('POST', uri);
    multipart.headers.forEach((key, value) => streamed.headers[key] = value);
    if (hasToken) streamed.headers['Authorization'] = 'Bearer $_token';
    streamed.headers['Accept'] = 'application/json';
    streamed.contentLength = total;

    progressStream.listen(
      streamed.sink.add,
      onDone: () => streamed.sink.close(),
      onError: streamed.sink.addError,
      cancelOnError: true,
    );

    final res = await _guard(() async {
      final streamedResponse = await streamed.send();
      return http.Response.fromStream(streamedResponse);
    }, _uploadTimeout);

    return _handle(res);
  }

  dynamic _handle(http.Response res, {bool auth = true}) {
    dynamic body;
    final text = utf8.decode(res.bodyBytes, allowMalformed: true);
    if (text.isNotEmpty) {
      try {
        body = jsonDecode(text);
      } catch (_) {
        body = null;
      }
    }

    if (res.statusCode == 401 && auth) {
      onUnauthorized?.call();
      throw ApiException(401, sessionExpiredMessage);
    }

    if (res.statusCode >= 200 && res.statusCode < 300) {
      return body;
    }

    throw ApiException(res.statusCode, _extractMessage(res.statusCode, body));
  }

  /// Hata gövdesinden Türkçe mesaj çıkarır: önce { message }, sonra
  /// ASP.NET doğrulama hataları ({ errors }), sonra { title }.
  String _extractMessage(int status, dynamic body) {
    if (body is Map<String, dynamic>) {
      final message = body['message'];
      if (message is String && message.trim().isNotEmpty) return message;

      final errors = body['errors'];
      if (errors is Map<String, dynamic>) {
        for (final value in errors.values) {
          if (value is List && value.isNotEmpty && value.first is String) {
            return value.first as String;
          }
        }
      }

      final title = body['title'];
      if (title is String && title.trim().isNotEmpty) return title;
    }

    if (status == 403) return 'Bu işlem için yetkiniz yok.';
    if (status == 404) return 'Kayıt bulunamadı.';
    return 'Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.';
  }
}
