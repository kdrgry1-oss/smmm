import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../formatters.dart';
import '../models/receipt.dart';
import '../state/receipts_provider.dart';
import '../theme.dart';
import '../widgets/confidence_badge.dart';
import '../widgets/empty_state.dart';
import '../widgets/error_banner.dart';
import '../widgets/status_badge.dart';
import 'receipt_approve_screen.dart';

/// Fiş listesi: durum filtreleri + sayfalama + detay (onay/red).
class ReceiptsScreen extends StatefulWidget {
  const ReceiptsScreen({super.key});

  @override
  State<ReceiptsScreen> createState() => _ReceiptsScreenState();
}

class _ReceiptsScreenState extends State<ReceiptsScreen> {
  static const List<(String?, String)> _filters = [
    (null, 'Tümü'),
    ('NeedsReview', 'Onay Bekliyor'),
    ('Approved', 'Onaylandı'),
    ('Rejected', 'Reddedildi'),
    ('Processing', 'İşleniyor'),
    ('Uploaded', 'Yüklendi'),
  ];

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) {
      // Her oturum açılışında taze veri (HomeShell girişte yeniden kurulur).
      if (mounted) context.read<ReceiptsProvider>().load();
    });
  }

  void _showDetail(Receipt receipt) {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      builder: (sheetContext) => _ReceiptDetailSheet(
        receiptId: receipt.id,
        onApprove: (r) {
          Navigator.of(sheetContext).pop();
          Navigator.of(context).push(
            MaterialPageRoute<void>(
              fullscreenDialog: true,
              builder: (_) => ReceiptApproveScreen(receipt: r),
            ),
          );
        },
        onReject: (r) async {
          Navigator.of(sheetContext).pop();
          await _rejectFlow(r);
        },
      ),
    );
  }

  Future<void> _rejectFlow(Receipt receipt) async {
    final reasonController = TextEditingController();
    final formKey = GlobalKey<FormState>();

    final reason = await showDialog<String>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        backgroundColor: AppColors.navy800,
        title: const Text('Fişi Reddet'),
        content: Form(
          key: formKey,
          child: TextFormField(
            controller: reasonController,
            autofocus: true,
            maxLength: 300,
            decoration: const InputDecoration(
              labelText: 'Red gerekçesi',
              hintText: 'Örn: Görsel okunaksız, tekrar çekilmeli',
            ),
            validator: (value) => (value == null || value.trim().isEmpty)
                ? 'Red gerekçesi zorunludur.'
                : null,
          ),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(),
            child: const Text('Vazgeç'),
          ),
          FilledButton(
            style: FilledButton.styleFrom(backgroundColor: AppColors.danger),
            onPressed: () {
              if (formKey.currentState?.validate() ?? false) {
                Navigator.of(dialogContext).pop(reasonController.text.trim());
              }
            },
            child: const Text('Reddet'),
          ),
        ],
      ),
    );

    if (reason == null || !mounted) return;

    try {
      await context.read<ReceiptsProvider>().reject(receipt.id, reason);
      if (!mounted) return;
      ScaffoldMessenger.of(context)
          .showSnackBar(const SnackBar(content: Text('Fiş reddedildi.')));
    } on ApiException catch (e) {
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(e.message), backgroundColor: AppColors.danger),
      );
    }
  }

  @override
  Widget build(BuildContext context) {
    final provider = context.watch<ReceiptsProvider>();

    return Scaffold(
      appBar: AppBar(
        title: Text(
          'Fişler${provider.hasData ? ' (${provider.totalCount})' : ''}',
        ),
      ),
      body: Column(
        children: [
          SizedBox(
            height: 52,
            child: ListView.separated(
              scrollDirection: Axis.horizontal,
              padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
              itemCount: _filters.length,
              separatorBuilder: (_, __) => const SizedBox(width: 8),
              itemBuilder: (context, index) {
                final (value, label) = _filters[index];
                final selected = provider.statusFilter == value;
                return FilterChip(
                  label: Text(label),
                  selected: selected,
                  onSelected: (_) =>
                      context.read<ReceiptsProvider>().setFilter(value),
                );
              },
            ),
          ),
          Expanded(child: _buildList(provider)),
        ],
      ),
    );
  }

  Widget _buildList(ReceiptsProvider provider) {
    if (provider.loading && !provider.hasData) {
      return const Center(child: CircularProgressIndicator());
    }

    if (provider.error != null && provider.items.isEmpty) {
      return ListView(
        children: [
          const SizedBox(height: 120),
          ErrorBanner(
            message: provider.error!,
            onRetry: () => context.read<ReceiptsProvider>().load(),
          ),
        ],
      );
    }

    if (provider.items.isEmpty) {
      return EmptyState(
        icon: Icons.receipt_long_outlined,
        title: 'Fiş bulunamadı',
        message: provider.statusFilter == null
            ? 'Henüz fiş yüklenmemiş. Sağ alttaki "Fiş Çek" ile başlayın.'
            : 'Bu durumda fiş yok. Farklı bir filtre deneyin.',
      );
    }

    return RefreshIndicator(
      onRefresh: () => context.read<ReceiptsProvider>().load(),
      child: ListView.separated(
        physics: const AlwaysScrollableScrollPhysics(),
        padding: const EdgeInsets.fromLTRB(16, 4, 16, 96),
        itemCount: provider.items.length + (provider.hasMore ? 1 : 0),
        separatorBuilder: (_, __) => const SizedBox(height: 8),
        itemBuilder: (context, index) {
          if (index == provider.items.length) {
            return Center(
              child: provider.loadingMore
                  ? const Padding(
                      padding: EdgeInsets.all(12),
                      child: CircularProgressIndicator(),
                    )
                  : TextButton(
                      onPressed: () =>
                          context.read<ReceiptsProvider>().loadMore(),
                      child: const Text('Daha Fazla Yükle'),
                    ),
            );
          }

          final receipt = provider.items[index];
          return Material(
            color: AppColors.navy800,
            borderRadius: BorderRadius.circular(14),
            child: InkWell(
              borderRadius: BorderRadius.circular(14),
              onTap: () => _showDetail(receipt),
              child: Padding(
                padding: const EdgeInsets.all(14),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        Expanded(
                          child: Text(
                            receipt.vendorName ?? receipt.fileName,
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                            style: const TextStyle(
                              color: AppColors.navy100,
                              fontWeight: FontWeight.w600,
                            ),
                          ),
                        ),
                        const SizedBox(width: 8),
                        ReceiptStatusBadge(status: receipt.status),
                      ],
                    ),
                    const SizedBox(height: 6),
                    Text(
                      '${receipt.clientTitle ?? '—'} · ${formatDate(receipt.receiptDate)}',
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: const TextStyle(
                          color: AppColors.navy300, fontSize: 12.5),
                    ),
                    const SizedBox(height: 8),
                    Row(
                      children: [
                        Text(
                          formatTl(receipt.totalAmount),
                          style: const TextStyle(
                            color: AppColors.navy100,
                            fontSize: 15,
                            fontWeight: FontWeight.w700,
                          ),
                        ),
                        const SizedBox(width: 10),
                        ConfidenceBadge(confidence: receipt.confidence),
                        if (receipt.autoApproveReady) ...[
                          const SizedBox(width: 6),
                          const PillBadge(
                              label: 'Hızlı onaya hazır',
                              color: AppColors.success),
                        ],
                      ],
                    ),
                  ],
                ),
              ),
            ),
          );
        },
      ),
    );
  }
}

/// Fiş detayı çekmecesi. Satır provider'dan izlenir; onay/red sonrası güncellenir.
class _ReceiptDetailSheet extends StatelessWidget {
  const _ReceiptDetailSheet({
    required this.receiptId,
    required this.onApprove,
    required this.onReject,
  });

  final String receiptId;
  final void Function(Receipt receipt) onApprove;
  final void Function(Receipt receipt) onReject;

  @override
  Widget build(BuildContext context) {
    final provider = context.watch<ReceiptsProvider>();
    Receipt? receipt;
    for (final r in provider.items) {
      if (r.id == receiptId) {
        receipt = r;
        break;
      }
    }

    if (receipt == null) {
      return const SafeArea(
        child: Padding(
          padding: EdgeInsets.all(24),
          child: Text('Fiş bulunamadı.',
              style: TextStyle(color: AppColors.navy300)),
        ),
      );
    }

    return SafeArea(
      child: SingleChildScrollView(
        padding: EdgeInsets.only(
          left: 20,
          right: 20,
          top: 20,
          bottom: MediaQuery.of(context).viewInsets.bottom + 20,
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(
                    receipt.vendorName ?? receipt.fileName,
                    style: const TextStyle(
                      color: AppColors.navy100,
                      fontSize: 17,
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                ),
                ReceiptStatusBadge(status: receipt.status),
              ],
            ),
            const SizedBox(height: 6),
            Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                ConfidenceBadge(confidence: receipt.confidence),
                if (receipt.autoApproveReady)
                  const PillBadge(
                      label: 'Hızlı onaya hazır', color: AppColors.success),
                if (receipt.isOldDocument)
                  const PillBadge(
                      label: 'Belge 30 günden eski', color: AppColors.warning),
                if (receipt.expenseEligible == false)
                  const PillBadge(
                      label: 'Gider gösterilemeyebilir',
                      color: AppColors.danger),
              ],
            ),
            const SizedBox(height: 16),
            _row('Mükellef', receipt.clientTitle ?? '—'),
            _row('Dosya', receipt.fileName),
            _row('Satıcı VKN', receipt.vendorTaxId ?? '—'),
            _row('Belge Tarihi', formatDate(receipt.receiptDate)),
            _row('Toplam', formatTl(receipt.totalAmount)),
            _row('KDV Oranı', formatVatRate(receipt.vatRate)),
            _row('KDV Tutarı', formatTl(receipt.vatAmount)),
            _row('Matrah', formatTl(receipt.netAmount)),
            _row('Önerilen Hesap', receipt.suggestedAccountCode ?? '—'),
            _row('Hesap Kodu', receipt.accountCode ?? '—'),
            _row('Cari Kodu', receipt.cariCode ?? '—'),
            _row('Yüklenme', formatDateTime(receipt.createdAt)),
            if (receipt.expenseEligible == false)
              _row('Uygunsuzluk',
                  receipt.ineligibleReason ?? 'İnceleme gerekli.'),
            if (receipt.status == 'Rejected')
              _row('Red Gerekçesi', receipt.rejectReason ?? '—'),
            const SizedBox(height: 18),
            if (receipt.canDecide)
              Row(
                children: [
                  Expanded(
                    child: FilledButton.icon(
                      onPressed: () => onApprove(receipt),
                      style: FilledButton.styleFrom(
                          backgroundColor: const Color(0xFF15803D)),
                      icon: const Icon(Icons.check),
                      label: const Text('Onayla / Düzelt'),
                    ),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: OutlinedButton.icon(
                      onPressed: () => onReject(receipt),
                      style: OutlinedButton.styleFrom(
                        foregroundColor: AppColors.danger,
                        side: const BorderSide(color: AppColors.danger),
                      ),
                      icon: const Icon(Icons.close),
                      label: const Text('Reddet'),
                    ),
                  ),
                ],
              ),
          ],
        ),
      ),
    );
  }

  Widget _row(String label, String value) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 4),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(
            width: 130,
            child: Text(label,
                style:
                    const TextStyle(color: AppColors.navy400, fontSize: 13)),
          ),
          Expanded(
            child: Text(value,
                style: const TextStyle(
                    color: AppColors.navy100, fontSize: 13.5)),
          ),
        ],
      ),
    );
  }
}
