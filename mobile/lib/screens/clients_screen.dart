import 'dart:async';

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../formatters.dart';
import '../models/client.dart';
import '../state/clients_provider.dart';
import '../theme.dart';
import '../widgets/empty_state.dart';
import '../widgets/error_banner.dart';
import '../widgets/status_badge.dart';
import 'receipt_upload_screen.dart';

/// Mükellef listesi: sunucu tarafı arama + çekmece (bottom sheet) detay.
class ClientsScreen extends StatefulWidget {
  const ClientsScreen({super.key});

  @override
  State<ClientsScreen> createState() => _ClientsScreenState();
}

class _ClientsScreenState extends State<ClientsScreen> {
  final _searchController = TextEditingController();
  Timer? _debounce;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) context.read<ClientsProvider>().load(q: '');
    });
  }

  @override
  void dispose() {
    _debounce?.cancel();
    _searchController.dispose();
    super.dispose();
  }

  void _onSearchChanged(String value) {
    _debounce?.cancel();
    _debounce = Timer(const Duration(milliseconds: 400), () {
      if (mounted) context.read<ClientsProvider>().load(q: value);
    });
  }

  void _showDetail(Client client) {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      builder: (sheetContext) => SafeArea(
        child: SingleChildScrollView(
          padding: EdgeInsets.only(
            left: 20,
            right: 20,
            top: 20,
            bottom: MediaQuery.of(sheetContext).viewInsets.bottom + 20,
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            children: [
              Text(
                client.title,
                style: const TextStyle(
                  color: AppColors.navy100,
                  fontSize: 18,
                  fontWeight: FontWeight.w700,
                ),
              ),
              const SizedBox(height: 10),
              Wrap(
                spacing: 8,
                runSpacing: 8,
                children: [
                  if (client.eInvoiceEnabled)
                    const PillBadge(label: 'e-Fatura', color: AppColors.success),
                  if (client.eArchiveEnabled)
                    const PillBadge(label: 'e-Arşiv', color: AppColors.info),
                  if (client.isArchived)
                    const PillBadge(
                        label: 'Arşivlenmiş', color: AppColors.warning),
                ],
              ),
              const SizedBox(height: 16),
              _DetailRow(label: 'VKN / TCKN', value: client.taxId),
              _DetailRow(label: 'Vergi Dairesi', value: client.taxOffice),
              _DetailRow(label: 'E-posta', value: client.email),
              _DetailRow(label: 'Telefon', value: client.phone),
              _DetailRow(label: 'Adres', value: client.address),
              _DetailRow(label: 'Entegratör', value: client.integratorName),
              _DetailRow(
                  label: 'Kayıt Tarihi', value: formatDate(client.createdAt)),
              const SizedBox(height: 20),
              FilledButton.icon(
                onPressed: () {
                  Navigator.of(sheetContext).pop();
                  Navigator.of(context).push(
                    MaterialPageRoute<void>(
                      builder: (_) =>
                          ReceiptUploadScreen(preselectedClient: client),
                    ),
                  );
                },
                icon: const Icon(Icons.photo_camera_outlined),
                label: const Text('Bu mükellef için fiş çek'),
              ),
            ],
          ),
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final provider = context.watch<ClientsProvider>();

    return Scaffold(
      appBar: AppBar(title: const Text('Mükellefler')),
      body: Column(
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 4),
            child: TextField(
              controller: _searchController,
              onChanged: _onSearchChanged,
              textInputAction: TextInputAction.search,
              decoration: InputDecoration(
                hintText: 'Unvan veya VKN ile ara...',
                prefixIcon: const Icon(Icons.search),
                suffixIcon: _searchController.text.isEmpty
                    ? null
                    : IconButton(
                        icon: const Icon(Icons.close),
                        tooltip: 'Aramayı temizle',
                        onPressed: () {
                          _searchController.clear();
                          _onSearchChanged('');
                          setState(() {});
                        },
                      ),
              ),
            ),
          ),
          Expanded(child: _buildList(provider)),
        ],
      ),
    );
  }

  Widget _buildList(ClientsProvider provider) {
    if (provider.loading && !provider.hasData) {
      return const Center(child: CircularProgressIndicator());
    }

    if (provider.error != null && !provider.hasData) {
      return ListView(
        children: [
          const SizedBox(height: 120),
          ErrorBanner(
            message: provider.error!,
            onRetry: () => context.read<ClientsProvider>().load(),
          ),
        ],
      );
    }

    if (provider.clients.isEmpty) {
      return EmptyState(
        icon: Icons.people_outline,
        title: provider.query.trim().isEmpty
            ? 'Henüz mükellef yok'
            : 'Sonuç bulunamadı',
        message: provider.query.trim().isEmpty
            ? 'Mükelleflerinizi web panelinden ekleyebilirsiniz.'
            : '"${provider.query.trim()}" aramasıyla eşleşen mükellef bulunamadı.',
      );
    }

    return RefreshIndicator(
      onRefresh: () => context.read<ClientsProvider>().load(),
      child: ListView.separated(
        physics: const AlwaysScrollableScrollPhysics(),
        padding: const EdgeInsets.fromLTRB(16, 8, 16, 96),
        itemCount: provider.clients.length,
        separatorBuilder: (_, __) => const SizedBox(height: 8),
        itemBuilder: (context, index) {
          final client = provider.clients[index];
          return Material(
            color: AppColors.navy800,
            borderRadius: BorderRadius.circular(14),
            child: InkWell(
              borderRadius: BorderRadius.circular(14),
              onTap: () => _showDetail(client),
              child: Padding(
                padding: const EdgeInsets.all(14),
                child: Row(
                  children: [
                    CircleAvatar(
                      backgroundColor: AppColors.navy600,
                      child: Text(
                        client.title.isEmpty
                            ? '?'
                            : client.title.characters.first.toUpperCase(),
                        style: const TextStyle(
                            color: Colors.white, fontWeight: FontWeight.w600),
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            client.title,
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                            style: const TextStyle(
                              color: AppColors.navy100,
                              fontWeight: FontWeight.w600,
                            ),
                          ),
                          const SizedBox(height: 3),
                          Text(
                            'VKN: ${client.taxId}'
                            '${client.taxOffice == null ? '' : ' · ${client.taxOffice}'}',
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                            style: const TextStyle(
                                color: AppColors.navy300, fontSize: 12.5),
                          ),
                        ],
                      ),
                    ),
                    const Icon(Icons.chevron_right, color: AppColors.navy400),
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

class _DetailRow extends StatelessWidget {
  const _DetailRow({required this.label, required this.value});

  final String label;
  final String? value;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 5),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(
            width: 120,
            child: Text(
              label,
              style: const TextStyle(color: AppColors.navy400, fontSize: 13),
            ),
          ),
          Expanded(
            child: Text(
              (value == null || value!.trim().isEmpty) ? '—' : value!,
              style: const TextStyle(color: AppColors.navy100, fontSize: 13.5),
            ),
          ),
        ],
      ),
    );
  }
}
