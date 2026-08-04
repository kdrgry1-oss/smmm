import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../formatters.dart';
import '../models/receipt.dart';
import '../state/receipts_provider.dart';
import '../theme.dart';
import '../widgets/confidence_badge.dart';
import '../widgets/error_banner.dart';

/// Fiş onayı: OCR değerleri önceden doldurulur; yalnızca değiştirilen alanlar
/// düzeltme olarak gönderilir (backend'de dolu alanlar OCR değerini ezer).
class ReceiptApproveScreen extends StatefulWidget {
  const ReceiptApproveScreen({super.key, required this.receipt});

  final Receipt receipt;

  @override
  State<ReceiptApproveScreen> createState() => _ReceiptApproveScreenState();
}

class _ReceiptApproveScreenState extends State<ReceiptApproveScreen> {
  final _formKey = GlobalKey<FormState>();

  late final TextEditingController _vendorName;
  late final TextEditingController _vendorTaxId;
  late final TextEditingController _totalAmount;
  late final TextEditingController _vatRate;
  late final TextEditingController _vatAmount;
  late final TextEditingController _netAmount;
  late final TextEditingController _accountCode;
  late final TextEditingController _cariCode;

  DateTime? _receiptDate;
  bool _overrideEligibility = false;
  bool _submitting = false;
  String? _error;

  static final RegExp _taxIdRegex = RegExp(r'^\d{10}(\d)?$');
  static final RegExp _accountCodeRegex = RegExp(r'^\d{3}(\.\d{1,4}){0,3}$');

  String _amountText(double? value) =>
      value == null ? '' : value.toStringAsFixed(2).replaceAll('.', ',');

  @override
  void initState() {
    super.initState();
    final r = widget.receipt;
    _vendorName = TextEditingController(text: r.vendorName ?? '');
    _vendorTaxId = TextEditingController(text: r.vendorTaxId ?? '');
    _totalAmount = TextEditingController(text: _amountText(r.totalAmount));
    _vatRate = TextEditingController(text: _amountText(r.vatRate));
    _vatAmount = TextEditingController(text: _amountText(r.vatAmount));
    _netAmount = TextEditingController(text: _amountText(r.netAmount));
    _accountCode = TextEditingController(
        text: r.accountCode ?? r.suggestedAccountCode ?? '');
    _cariCode = TextEditingController(text: r.cariCode ?? '');
    _receiptDate = r.receiptDate;
  }

  @override
  void dispose() {
    _vendorName.dispose();
    _vendorTaxId.dispose();
    _totalAmount.dispose();
    _vatRate.dispose();
    _vatAmount.dispose();
    _netAmount.dispose();
    _accountCode.dispose();
    _cariCode.dispose();
    super.dispose();
  }

  Future<void> _pickDate() async {
    final now = DateTime.now();
    final picked = await showDatePicker(
      context: context,
      initialDate: _receiptDate ?? now,
      firstDate: DateTime(now.year - 5),
      lastDate: now,
      locale: const Locale('tr', 'TR'),
      helpText: 'Belge tarihi seçin',
      cancelText: 'Vazgeç',
      confirmText: 'Seç',
    );
    if (picked != null) setState(() => _receiptDate = picked);
  }

  String? _validateOptionalAmount(String? value, String label,
      {double? min, double? max}) {
    if (value == null || value.trim().isEmpty) return null;
    final parsed = parseTrNumber(value);
    if (parsed == null) return '$label için geçerli bir sayı girin (örn: 1234,56).';
    if (min != null && parsed < min) return '$label $min değerinden küçük olamaz.';
    if (max != null && parsed > max) return '$label $max değerinden büyük olamaz.';
    return null;
  }

  Map<String, dynamic> _buildBody() {
    final body = <String, dynamic>{
      'overrideEligibility': _overrideEligibility,
    };

    void putText(String key, TextEditingController controller) {
      final v = controller.text.trim();
      if (v.isNotEmpty) body[key] = v;
    }

    void putNumber(String key, TextEditingController controller) {
      final v = parseTrNumber(controller.text);
      if (v != null) body[key] = v;
    }

    putText('vendorName', _vendorName);
    putText('vendorTaxId', _vendorTaxId);
    if (_receiptDate != null) body['receiptDate'] = toApiDate(_receiptDate!);
    putNumber('totalAmount', _totalAmount);
    putNumber('vatRate', _vatRate);
    putNumber('vatAmount', _vatAmount);
    putNumber('netAmount', _netAmount);
    putText('accountCode', _accountCode);
    putText('cariCode', _cariCode);

    return body;
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;

    setState(() {
      _submitting = true;
      _error = null;
    });

    try {
      await context
          .read<ReceiptsProvider>()
          .approve(widget.receipt.id, _buildBody());
      if (!mounted) return;
      Navigator.of(context).pop();
      ScaffoldMessenger.of(context)
          .showSnackBar(const SnackBar(content: Text('Fiş onaylandı.')));
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.message);
    } finally {
      if (mounted) setState(() => _submitting = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final receipt = widget.receipt;

    return Scaffold(
      appBar: AppBar(title: const Text('Fişi Onayla')),
      body: Form(
        key: _formKey,
        child: ListView(
          padding: const EdgeInsets.all(16),
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    receipt.clientTitle ?? receipt.fileName,
                    style: const TextStyle(
                      color: AppColors.navy100,
                      fontSize: 15,
                      fontWeight: FontWeight.w600,
                    ),
                  ),
                ),
                ConfidenceBadge(confidence: receipt.confidence),
              ],
            ),
            const SizedBox(height: 4),
            const Text(
              'OCR değerlerini kontrol edin; hatalı alanları düzeltip onaylayın.',
              style: TextStyle(color: AppColors.navy300, fontSize: 13),
            ),
            const SizedBox(height: 16),
            if (_error != null)
              Padding(
                padding: const EdgeInsets.only(bottom: 10),
                child: ErrorBanner(message: _error!),
              ),
            TextFormField(
              controller: _vendorName,
              maxLength: 300,
              decoration: const InputDecoration(
                  labelText: 'Satıcı adı', counterText: ''),
            ),
            const SizedBox(height: 12),
            TextFormField(
              controller: _vendorTaxId,
              keyboardType: TextInputType.number,
              decoration: const InputDecoration(
                labelText: 'Satıcı VKN / TCKN',
                helperText: '10 veya 11 haneli; boş bırakılabilir.',
              ),
              validator: (value) {
                final v = value?.trim() ?? '';
                if (v.isEmpty) return null;
                if (!_taxIdRegex.hasMatch(v)) {
                  return 'Vergi/TC kimlik numarası 10 veya 11 haneli olmalıdır.';
                }
                return null;
              },
            ),
            const SizedBox(height: 12),
            InkWell(
              onTap: _pickDate,
              borderRadius: BorderRadius.circular(12),
              child: InputDecorator(
                decoration: const InputDecoration(
                  labelText: 'Belge tarihi',
                  suffixIcon: Icon(Icons.calendar_today_outlined, size: 18),
                ),
                child: Text(
                  _receiptDate == null
                      ? 'Tarih seçin'
                      : formatDate(_receiptDate),
                  style: TextStyle(
                    color: _receiptDate == null
                        ? AppColors.navy400
                        : AppColors.navy100,
                  ),
                ),
              ),
            ),
            const SizedBox(height: 12),
            Row(
              children: [
                Expanded(
                  child: TextFormField(
                    controller: _totalAmount,
                    keyboardType:
                        const TextInputType.numberWithOptions(decimal: true),
                    decoration: const InputDecoration(
                        labelText: 'Toplam (₺)', hintText: '1234,56'),
                    validator: (v) => _validateOptionalAmount(v, 'Toplam tutar',
                        min: 0.01),
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: TextFormField(
                    controller: _vatRate,
                    keyboardType:
                        const TextInputType.numberWithOptions(decimal: true),
                    decoration: const InputDecoration(
                        labelText: 'KDV Oranı (%)', hintText: '20'),
                    validator: (v) =>
                        _validateOptionalAmount(v, 'KDV oranı', min: 0, max: 100),
                  ),
                ),
              ],
            ),
            const SizedBox(height: 12),
            Row(
              children: [
                Expanded(
                  child: TextFormField(
                    controller: _vatAmount,
                    keyboardType:
                        const TextInputType.numberWithOptions(decimal: true),
                    decoration: const InputDecoration(
                        labelText: 'KDV Tutarı (₺)', hintText: '205,93'),
                    validator: (v) =>
                        _validateOptionalAmount(v, 'KDV tutarı', min: 0),
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: TextFormField(
                    controller: _netAmount,
                    keyboardType:
                        const TextInputType.numberWithOptions(decimal: true),
                    decoration: const InputDecoration(
                        labelText: 'Matrah (₺)', hintText: '1029,63'),
                    validator: (v) =>
                        _validateOptionalAmount(v, 'Matrah', min: 0),
                  ),
                ),
              ],
            ),
            const SizedBox(height: 12),
            TextFormField(
              controller: _accountCode,
              decoration: const InputDecoration(
                labelText: 'Hesap kodu',
                helperText: 'Örn: 770.01 — boşsa mevcut öneri kullanılır.',
              ),
              validator: (value) {
                final v = value?.trim() ?? '';
                if (v.isEmpty) return null;
                if (!_accountCodeRegex.hasMatch(v)) {
                  return 'Geçerli bir hesap kodu girin (ör. 770.01).';
                }
                return null;
              },
            ),
            const SizedBox(height: 12),
            TextFormField(
              controller: _cariCode,
              decoration: const InputDecoration(
                labelText: 'Cari kodu',
                helperText: 'Örn: 320.001 — boş bırakılabilir.',
              ),
              validator: (value) {
                final v = value?.trim() ?? '';
                if (v.isEmpty) return null;
                if (!_accountCodeRegex.hasMatch(v)) {
                  return 'Geçerli bir cari kodu girin (ör. 320.001).';
                }
                return null;
              },
            ),
            if (receipt.expenseEligible == false) ...[
              const SizedBox(height: 8),
              SwitchListTile(
                value: _overrideEligibility,
                onChanged: (v) => setState(() => _overrideEligibility = v),
                title: const Text('Yine de gider göster',
                    style: TextStyle(color: AppColors.navy100, fontSize: 14.5)),
                subtitle: Text(
                  'Bu belge gider gösterilemez olarak işaretlendi'
                  '${receipt.ineligibleReason == null ? '' : ' (${receipt.ineligibleReason})'}. '
                  'Sorumluluğu üstlenerek onaylayabilirsiniz.',
                  style:
                      const TextStyle(color: AppColors.warning, fontSize: 12.5),
                ),
                activeColor: AppColors.warning,
                contentPadding: EdgeInsets.zero,
              ),
            ],
            const SizedBox(height: 20),
            FilledButton.icon(
              onPressed: _submitting ? null : _submit,
              style:
                  FilledButton.styleFrom(backgroundColor: const Color(0xFF15803D)),
              icon: _submitting
                  ? const SizedBox(
                      width: 18,
                      height: 18,
                      child: CircularProgressIndicator(
                          strokeWidth: 2.5, color: Colors.white),
                    )
                  : const Icon(Icons.check),
              label: Text(_submitting ? 'Onaylanıyor...' : 'Onayla'),
            ),
            const SizedBox(height: 12),
            OutlinedButton(
              onPressed:
                  _submitting ? null : () => Navigator.of(context).pop(),
              child: const Text('Vazgeç'),
            ),
            const SizedBox(height: 24),
          ],
        ),
      ),
    );
  }
}
