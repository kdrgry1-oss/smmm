import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:image_picker/image_picker.dart';
import 'package:provider/provider.dart';

import '../formatters.dart';
import '../models/client.dart';
import '../models/receipt.dart';
import '../state/clients_provider.dart';
import '../state/dashboard_provider.dart';
import '../state/receipts_provider.dart';
import '../state/upload_queue_provider.dart';
import '../theme.dart';
import '../widgets/confidence_badge.dart';
import '../widgets/empty_state.dart';
import '../widgets/error_banner.dart';
import '../widgets/status_badge.dart';

/// FİŞ ÇEKME — ana özellik:
/// kamera ile tek fotoğraf VEYA galeriden çoklu seçim → mükellef seç →
/// kuyruklu yükleme (ilerleme + hata durumunda yeniden dene) →
/// sonuç: OCR özeti kartı (satıcı, tutar, KDV, önerilen hesap kodu, güven rozeti).
class ReceiptUploadScreen extends StatefulWidget {
  const ReceiptUploadScreen({super.key, this.preselectedClient});

  final Client? preselectedClient;

  @override
  State<ReceiptUploadScreen> createState() => _ReceiptUploadScreenState();
}

class _ReceiptUploadScreenState extends State<ReceiptUploadScreen> {
  final ImagePicker _picker = ImagePicker();
  Client? _selectedClient;
  String? _clientError;

  late final ReceiptsProvider _receiptsProvider;
  late final DashboardProvider _dashboardProvider;

  @override
  void initState() {
    super.initState();
    _selectedClient = widget.preselectedClient;
    _receiptsProvider = context.read<ReceiptsProvider>();
    _dashboardProvider = context.read<DashboardProvider>();
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) context.read<ClientsProvider>().ensureLoaded();
    });
  }

  @override
  void dispose() {
    // Ekrandan çıkarken fiş listesi ve özet tazelensin (yeni fişler görünsün).
    if (_receiptsProvider.hasData) _receiptsProvider.load();
    _dashboardProvider.load();
    super.dispose();
  }

  bool _validateClient() {
    if (_selectedClient == null) {
      setState(() => _clientError = 'Önce fişin ait olduğu mükellefi seçin.');
      return false;
    }
    setState(() => _clientError = null);
    return true;
  }

  Future<void> _pickFromCamera() async {
    if (!_validateClient()) return;
    try {
      final photo = await _picker.pickImage(
        source: ImageSource.camera,
        // Faz 12 — görsel küçültme: OCR için yeterli minimum boyut (uzun kenar 1600 px,
        // JPEG %75; sunucu/web katmanlarıyla aynı parametreler). image_picker bu işlemi
        // native tarafta yapar ve HEIC yerine HER ZAMAN JPEG döner (HEIC sorunu da çözülür).
        maxWidth: 1600,
        maxHeight: 1600,
        imageQuality: 75,
        // KVKK: EXIF/konum metadata'sı istenmez (sunucu tarafı ayrıca temizler).
        requestFullMetadata: false,
      );
      if (photo == null || !mounted) return;
      context
          .read<UploadQueueProvider>()
          .enqueue(client: _selectedClient!, files: [photo]);
    } on PlatformException {
      _showError('Kameraya erişilemedi. Uygulama izinlerini kontrol edin.');
    }
  }

  Future<void> _pickFromGallery() async {
    if (!_validateClient()) return;
    try {
      final photos = await _picker.pickMultiImage(
        // Faz 12 — kamera ile aynı küçültme parametreleri (galeri seçiminde de JPEG döner).
        maxWidth: 1600,
        maxHeight: 1600,
        imageQuality: 75,
        requestFullMetadata: false,
      );
      if (photos.isEmpty || !mounted) return;
      context
          .read<UploadQueueProvider>()
          .enqueue(client: _selectedClient!, files: photos);
    } on PlatformException {
      _showError('Galeriye erişilemedi. Uygulama izinlerini kontrol edin.');
    }
  }

  void _showError(String message) {
    if (!mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text(message),
        backgroundColor: AppColors.danger,
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final clientsProvider = context.watch<ClientsProvider>();
    final queue = context.watch<UploadQueueProvider>();

    return Scaffold(
      appBar: AppBar(
        title: const Text('Fiş Çek / Yükle'),
        actions: [
          if (queue.hasFinished)
            TextButton(
              onPressed: () =>
                  context.read<UploadQueueProvider>().clearFinished(),
              child: const Text('Tamamlananları Temizle'),
            ),
        ],
      ),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          _buildClientPicker(clientsProvider),
          if (_clientError != null)
            Padding(
              padding: const EdgeInsets.only(top: 6),
              child: Text(
                _clientError!,
                style:
                    const TextStyle(color: AppColors.danger, fontSize: 12.5),
              ),
            ),
          const SizedBox(height: 14),
          Row(
            children: [
              Expanded(
                child: FilledButton.icon(
                  onPressed: _pickFromCamera,
                  icon: const Icon(Icons.photo_camera_outlined),
                  label: const Text('Kamera'),
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: OutlinedButton.icon(
                  onPressed: _pickFromGallery,
                  icon: const Icon(Icons.photo_library_outlined),
                  label: const Text('Galeriden Seç'),
                ),
              ),
            ],
          ),
          const SizedBox(height: 8),
          const Text(
            'Galeriden birden fazla fiş seçebilirsiniz; her dosya sırayla '
            'yüklenir ve OCR sonucu aşağıda görünür.',
            style: TextStyle(color: AppColors.navy400, fontSize: 12.5),
          ),
          const SizedBox(height: 18),
          if (queue.jobs.isEmpty)
            const Padding(
              padding: EdgeInsets.only(top: 24),
              child: EmptyState(
                icon: Icons.receipt_long_outlined,
                title: 'Kuyruk boş',
                message:
                    'Çektiğiniz veya seçtiğiniz fişler burada yüklenme '
                    'durumlarıyla listelenir.',
              ),
            )
          else
            ...queue.jobs.map((job) => _UploadJobCard(job: job)),
          const SizedBox(height: 24),
        ],
      ),
    );
  }

  Widget _buildClientPicker(ClientsProvider provider) {
    if (provider.loading && !provider.hasData) {
      return const Row(
        children: [
          SizedBox(
            width: 18,
            height: 18,
            child: CircularProgressIndicator(strokeWidth: 2),
          ),
          SizedBox(width: 10),
          Text('Mükellefler yükleniyor...',
              style: TextStyle(color: AppColors.navy300)),
        ],
      );
    }

    if (provider.error != null && !provider.hasData) {
      return ErrorBanner(
        message: provider.error!,
        onRetry: () => context.read<ClientsProvider>().load(),
      );
    }

    if (provider.clients.isEmpty) {
      return const ErrorBanner(
        message:
            'Kayıtlı mükellef bulunamadı. Önce web panelinden mükellef ekleyin.',
      );
    }

    // Önceden seçilen mükellef güncel listede yoksa seçim korunur (dropdown'a eklenir).
    final clients = [...provider.clients];
    if (_selectedClient != null &&
        !clients.any((c) => c.id == _selectedClient!.id)) {
      clients.insert(0, _selectedClient!);
    }

    return DropdownButtonFormField<String>(
      value: _selectedClient?.id,
      isExpanded: true,
      decoration: const InputDecoration(
        labelText: 'Mükellef',
        prefixIcon: Icon(Icons.person_outline),
      ),
      dropdownColor: AppColors.navy800,
      items: clients
          .map((c) => DropdownMenuItem<String>(
                value: c.id,
                child: Text(c.title, overflow: TextOverflow.ellipsis),
              ))
          .toList(),
      onChanged: (id) {
        setState(() {
          _selectedClient = clients.firstWhere((c) => c.id == id);
          _clientError = null;
        });
      },
    );
  }
}

class _UploadJobCard extends StatelessWidget {
  const _UploadJobCard({required this.job});

  final UploadJob job;

  @override
  Widget build(BuildContext context) {
    return Container(
      margin: const EdgeInsets.only(bottom: 12),
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: AppColors.navy800,
        borderRadius: BorderRadius.circular(14),
        border: Border.all(
          color: job.status == UploadJobStatus.error
              // ignore: deprecated_member_use
              ? AppColors.danger.withOpacity(0.5)
              : AppColors.navy700,
        ),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              ClipRRect(
                borderRadius: BorderRadius.circular(8),
                child: Image.file(
                  File(job.filePath),
                  width: 48,
                  height: 48,
                  fit: BoxFit.cover,
                  errorBuilder: (_, __, ___) => Container(
                    width: 48,
                    height: 48,
                    color: AppColors.navy700,
                    child: const Icon(Icons.image_not_supported_outlined,
                        color: AppColors.navy400),
                  ),
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      job.fileName,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: const TextStyle(
                          color: AppColors.navy100,
                          fontWeight: FontWeight.w600),
                    ),
                    const SizedBox(height: 2),
                    Text(
                      job.clientTitle,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: const TextStyle(
                          color: AppColors.navy300, fontSize: 12.5),
                    ),
                  ],
                ),
              ),
              const SizedBox(width: 8),
              _statusChip(),
              if (job.status != UploadJobStatus.uploading)
                IconButton(
                  icon: const Icon(Icons.close,
                      size: 18, color: AppColors.navy400),
                  tooltip: 'Kuyruktan çıkar',
                  onPressed: () =>
                      context.read<UploadQueueProvider>().remove(job),
                ),
            ],
          ),
          if (job.status == UploadJobStatus.uploading) ...[
            const SizedBox(height: 10),
            LinearProgressIndicator(value: job.progress),
            const SizedBox(height: 4),
            Text(
              'Yükleniyor... %${(job.progress * 100).round()}',
              style: const TextStyle(color: AppColors.navy300, fontSize: 12),
            ),
          ],
          if (job.status == UploadJobStatus.error) ...[
            const SizedBox(height: 10),
            Text(
              job.errorMessage ?? 'Yükleme başarısız oldu.',
              style: const TextStyle(color: AppColors.danger, fontSize: 13),
            ),
            const SizedBox(height: 8),
            OutlinedButton.icon(
              onPressed: () => context.read<UploadQueueProvider>().retry(job),
              style: OutlinedButton.styleFrom(
                foregroundColor: AppColors.danger,
                side: const BorderSide(color: AppColors.danger),
                minimumSize: const Size(64, 40),
              ),
              icon: const Icon(Icons.refresh, size: 18),
              label: const Text('Yeniden Dene'),
            ),
          ],
          if (job.status == UploadJobStatus.success && job.receipt != null)
            _OcrResultCard(receipt: job.receipt!),
        ],
      ),
    );
  }

  Widget _statusChip() {
    switch (job.status) {
      case UploadJobStatus.waiting:
        return const PillBadge(label: 'Sırada', color: AppColors.navy300);
      case UploadJobStatus.uploading:
        return const PillBadge(label: 'Yükleniyor', color: AppColors.info);
      case UploadJobStatus.success:
        return const PillBadge(label: 'Yüklendi', color: AppColors.success);
      case UploadJobStatus.error:
        return const PillBadge(label: 'Hata', color: AppColors.danger);
    }
  }
}

/// OCR özeti kartı: satıcı, tutar, KDV, önerilen hesap kodu ve güven rozeti.
class _OcrResultCard extends StatelessWidget {
  const _OcrResultCard({required this.receipt});

  final Receipt receipt;

  @override
  Widget build(BuildContext context) {
    return Container(
      margin: const EdgeInsets.only(top: 10),
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: AppColors.navy900,
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: AppColors.navy700),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              const Text(
                'OCR Özeti',
                style: TextStyle(
                    color: AppColors.navy200,
                    fontSize: 12.5,
                    fontWeight: FontWeight.w700),
              ),
              const Spacer(),
              ConfidenceBadge(confidence: receipt.confidence),
              const SizedBox(width: 6),
              ReceiptStatusBadge(status: receipt.status),
            ],
          ),
          const SizedBox(height: 10),
          _row('Satıcı', receipt.vendorName ?? '—'),
          _row('Tarih', formatDate(receipt.receiptDate)),
          _row('Toplam', formatTl(receipt.totalAmount)),
          _row(
            'KDV',
            '${formatVatRate(receipt.vatRate)} · ${formatTl(receipt.vatAmount)}',
          ),
          _row('Matrah', formatTl(receipt.netAmount)),
          _row('Önerilen Hesap',
              receipt.suggestedAccountCode ?? receipt.accountCode ?? '—'),
          if (receipt.expenseEligible == false)
            Padding(
              padding: const EdgeInsets.only(top: 8),
              child: Text(
                'Gider gösterilemeyebilir: '
                '${receipt.ineligibleReason ?? 'İnceleme gerekli.'}',
                style: const TextStyle(color: AppColors.warning, fontSize: 12.5),
              ),
            ),
          const Padding(
            padding: EdgeInsets.only(top: 8),
            child: Text(
              'Onay/düzeltme işlemini Fişler sekmesinden yapabilirsiniz.',
              style: TextStyle(color: AppColors.navy400, fontSize: 12),
            ),
          ),
        ],
      ),
    );
  }

  Widget _row(String label, String value) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 2.5),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(
            width: 110,
            child: Text(label,
                style:
                    const TextStyle(color: AppColors.navy400, fontSize: 12.5)),
          ),
          Expanded(
            child: Text(value,
                style: const TextStyle(
                    color: AppColors.navy100, fontSize: 13)),
          ),
        ],
      ),
    );
  }
}
