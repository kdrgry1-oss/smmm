import 'package:intl/intl.dart';

final NumberFormat _currency =
    NumberFormat.currency(locale: 'tr_TR', symbol: '₺', decimalDigits: 2);
final NumberFormat _percent = NumberFormat.decimalPattern('tr_TR');

/// 1234.5 → "₺1.234,50"; null → "—".
String formatTl(num? value) => value == null ? '—' : _currency.format(value);

/// 20 → "%20"; null → "—".
String formatVatRate(num? rate) =>
    rate == null ? '—' : '%${_percent.format(rate)}';

/// "dd.MM.yyyy"; null → "—".
String formatDate(DateTime? date) =>
    date == null ? '—' : DateFormat('dd.MM.yyyy', 'tr_TR').format(date.toLocal());

/// "dd.MM.yyyy HH:mm"; null → "—".
String formatDateTime(DateTime? date) => date == null
    ? '—'
    : DateFormat('dd.MM.yyyy HH:mm', 'tr_TR').format(date.toLocal());

/// Türkçe ondalık girdisini sayıya çevirir ("1234,56" → 1234.56).
/// Geçersizse null döner.
double? parseTrNumber(String? text) {
  if (text == null || text.trim().isEmpty) return null;
  return double.tryParse(text.trim().replaceAll(',', '.'));
}

/// API'ye tarih gönderimi: "yyyy-MM-dd".
String toApiDate(DateTime date) => DateFormat('yyyy-MM-dd').format(date);
