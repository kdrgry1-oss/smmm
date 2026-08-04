import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../formatters.dart';
import '../models/tebligat.dart';
import '../state/tebligat_provider.dart';
import '../theme.dart';
import '../widgets/deadline_counter.dart';
import '../widgets/empty_state.dart';
import '../widgets/error_banner.dart';
import '../widgets/status_badge.dart';

/// e-Tebligat tek pano: kalan süre renkli sayaç, aciller üstte (backend sıralar),
/// okundu işaretleme + manuel senkron.
class TebligatScreen extends StatefulWidget {
  const TebligatScreen({super.key});

  @override
  State<TebligatScreen> createState() => _TebligatScreenState();
}

class _TebligatScreenState extends State<TebligatScreen> {
  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) {
      // Her oturum açılışında taze veri (HomeShell girişte yeniden kurulur).
      if (mounted) context.read<TebligatProvider>().load();
    });
  }

  Future<void> _sync() async {
    final provider = context.read<TebligatProvider>();
    try {
      final message = await provider.sync();
      if (!mounted) return;
      ScaffoldMessenger.of(context)
          .showSnackBar(SnackBar(content: Text(message)));
    } on ApiException catch (e) {
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(e.message), backgroundColor: AppColors.danger),
      );
    }
  }

  Future<void> _openDetail(TebligatItem item) async {
    // Detay açılırken okunmamış tebligat okundu işaretlenir.
    if (!item.isRead) {
      try {
        await context.read<TebligatProvider>().markRead(item);
      } on ApiException catch (e) {
        if (!mounted) return;
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text(e.message), backgroundColor: AppColors.danger),
        );
      }
    }
    if (!mounted) return;

    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      builder: (sheetContext) => SafeArea(
        child: SingleChildScrollView(
          padding: const EdgeInsets.all(20),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            children: [
              Text(
                item.subject,
                style: const TextStyle(
                  color: AppColors.navy100,
                  fontSize: 17,
                  fontWeight: FontWeight.w700,
                ),
              ),
              const SizedBox(height: 10),
              Wrap(
                spacing: 8,
                runSpacing: 8,
                children: [
                  DeadlineCounter(
                    daysRemaining: item.daysRemaining,
                    deadlineType: item.deadlineType,
                  ),
                  const PillBadge(label: 'Okundu', color: AppColors.success),
                ],
              ),
              const SizedBox(height: 16),
              _row('Mükellef', item.clientTitle ?? '—'),
              _row('Gönderen', item.sender),
              _row('Tebliğ Tarihi', formatDateTime(item.deliveredAt)),
              _row('Yasal Son Gün', formatDate(item.legalDeadline)),
              _row('Süre Türü', item.deadlineType ?? '—'),
              const SizedBox(height: 12),
              const Text(
                'Tebligat içeriğinin aslına UETS üzerinden erişilir; bu pano '
                'takip ve süre uyarısı içindir.',
                style: TextStyle(color: AppColors.navy400, fontSize: 12.5),
              ),
            ],
          ),
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final provider = context.watch<TebligatProvider>();

    return Scaffold(
      appBar: AppBar(
        title: const Text('e-Tebligat'),
        actions: [
          IconButton(
            onPressed: provider.syncing ? null : _sync,
            tooltip: 'UETS ile senkronize et',
            icon: provider.syncing
                ? const SizedBox(
                    width: 18,
                    height: 18,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : const Icon(Icons.sync),
          ),
        ],
      ),
      body: Column(
        children: [
          if (provider.hasData)
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 12, 16, 4),
              child: Row(
                children: [
                  _Counter(
                      label: 'Toplam',
                      value: provider.totalCount,
                      color: AppColors.navy300),
                  const SizedBox(width: 8),
                  _Counter(
                      label: 'Okunmamış',
                      value: provider.unreadCount,
                      color: AppColors.info),
                  const SizedBox(width: 8),
                  _Counter(
                      label: 'Acil',
                      value: provider.urgentCount,
                      color: AppColors.danger),
                ],
              ),
            ),
          SizedBox(
            height: 52,
            child: ListView(
              scrollDirection: Axis.horizontal,
              padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
              children: [
                FilterChip(
                  label: const Text('Tümü'),
                  selected: !provider.onlyUnread && !provider.onlyUrgent,
                  onSelected: (_) => context
                      .read<TebligatProvider>()
                      .setFilters(unread: false, urgent: false),
                ),
                const SizedBox(width: 8),
                FilterChip(
                  label: const Text('Okunmamış'),
                  selected: provider.onlyUnread,
                  onSelected: (selected) => context
                      .read<TebligatProvider>()
                      .setFilters(unread: selected),
                ),
                const SizedBox(width: 8),
                FilterChip(
                  label: const Text('Acil (≤ 3 gün)'),
                  selected: provider.onlyUrgent,
                  onSelected: (selected) => context
                      .read<TebligatProvider>()
                      .setFilters(urgent: selected),
                ),
              ],
            ),
          ),
          Expanded(child: _buildList(provider)),
        ],
      ),
    );
  }

  Widget _buildList(TebligatProvider provider) {
    if (provider.loading && !provider.hasData) {
      return const Center(child: CircularProgressIndicator());
    }

    if (provider.error != null && !provider.hasData) {
      return ListView(
        children: [
          const SizedBox(height: 120),
          ErrorBanner(
            message: provider.error!,
            onRetry: () => context.read<TebligatProvider>().load(),
          ),
        ],
      );
    }

    if (provider.items.isEmpty) {
      return EmptyState(
        icon: Icons.notifications_none,
        title: 'Tebligat yok',
        message: (provider.onlyUnread || provider.onlyUrgent)
            ? 'Bu filtreyle eşleşen tebligat bulunamadı.'
            : 'Henüz tebligat alınmamış. Sağ üstteki senkron düğmesiyle '
                'UETS taraması başlatabilirsiniz.',
        actionLabel:
            (provider.onlyUnread || provider.onlyUrgent) ? null : 'Senkronize Et',
        onAction:
            (provider.onlyUnread || provider.onlyUrgent) ? null : _sync,
      );
    }

    return RefreshIndicator(
      onRefresh: () => context.read<TebligatProvider>().load(),
      child: ListView.separated(
        physics: const AlwaysScrollableScrollPhysics(),
        padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
        itemCount: provider.items.length,
        separatorBuilder: (_, __) => const SizedBox(height: 8),
        itemBuilder: (context, index) {
          final item = provider.items[index];
          return Material(
            color: AppColors.navy800,
            borderRadius: BorderRadius.circular(14),
            child: InkWell(
              borderRadius: BorderRadius.circular(14),
              onTap: () => _openDetail(item),
              child: Container(
                padding: const EdgeInsets.all(14),
                decoration: item.isUrgent
                    ? BoxDecoration(
                        borderRadius: BorderRadius.circular(14),
                        border: Border.all(
                          // ignore: deprecated_member_use
                          color: AppColors.danger.withOpacity(0.5),
                        ),
                      )
                    : null,
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      children: [
                        if (!item.isRead)
                          Container(
                            width: 8,
                            height: 8,
                            margin: const EdgeInsets.only(right: 8),
                            decoration: const BoxDecoration(
                              color: AppColors.info,
                              shape: BoxShape.circle,
                            ),
                          ),
                        Expanded(
                          child: Text(
                            item.subject,
                            maxLines: 2,
                            overflow: TextOverflow.ellipsis,
                            style: TextStyle(
                              color: AppColors.navy100,
                              fontWeight: item.isRead
                                  ? FontWeight.w500
                                  : FontWeight.w700,
                            ),
                          ),
                        ),
                      ],
                    ),
                    const SizedBox(height: 6),
                    Text(
                      '${item.clientTitle ?? '—'} · ${item.sender}',
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: const TextStyle(
                          color: AppColors.navy300, fontSize: 12.5),
                    ),
                    const SizedBox(height: 8),
                    Row(
                      children: [
                        DeadlineCounter(
                          daysRemaining: item.daysRemaining,
                          deadlineType: item.deadlineType,
                        ),
                        const Spacer(),
                        Text(
                          formatDate(item.deliveredAt),
                          style: const TextStyle(
                              color: AppColors.navy400, fontSize: 12),
                        ),
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

  Widget _row(String label, String value) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 4),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(
            width: 120,
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

class _Counter extends StatelessWidget {
  const _Counter({
    required this.label,
    required this.value,
    required this.color,
  });

  final String label;
  final int value;
  final Color color;

  @override
  Widget build(BuildContext context) {
    return Expanded(
      child: Container(
        padding: const EdgeInsets.symmetric(vertical: 10),
        decoration: BoxDecoration(
          color: AppColors.navy800,
          borderRadius: BorderRadius.circular(12),
          border: Border.all(color: AppColors.navy700),
        ),
        child: Column(
          children: [
            Text(
              '$value',
              style: TextStyle(
                  color: color, fontSize: 18, fontWeight: FontWeight.w700),
            ),
            const SizedBox(height: 2),
            Text(label,
                style:
                    const TextStyle(color: AppColors.navy300, fontSize: 11.5)),
          ],
        ),
      ),
    );
  }
}
