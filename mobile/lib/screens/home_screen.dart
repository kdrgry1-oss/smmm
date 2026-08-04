import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../state/auth_provider.dart';
import '../state/dashboard_provider.dart';
import '../theme.dart';
import '../widgets/error_banner.dart';
import 'clients_screen.dart';
import 'receipt_upload_screen.dart';
import 'receipts_screen.dart';
import 'settings_screen.dart';
import 'tebligat_screen.dart';

/// Ana kabuk: alt gezinme (Özet / Mükellefler / Fişler / Tebligat / Ayarlar)
/// + "Fiş Çek" FAB'ı.
class HomeShell extends StatefulWidget {
  const HomeShell({super.key});

  @override
  State<HomeShell> createState() => _HomeShellState();
}

class _HomeShellState extends State<HomeShell> {
  int _index = 0;

  void _goTo(int index) => setState(() => _index = index);

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      body: IndexedStack(
        index: _index,
        children: [
          DashboardTab(onNavigate: _goTo),
          const ClientsScreen(),
          const ReceiptsScreen(),
          const TebligatScreen(),
          const SettingsScreen(),
        ],
      ),
      floatingActionButton: _index <= 2
          ? FloatingActionButton.extended(
              heroTag: 'fab-fis-cek',
              onPressed: () => Navigator.of(context).push(
                MaterialPageRoute<void>(
                  builder: (_) => const ReceiptUploadScreen(),
                ),
              ),
              icon: const Icon(Icons.photo_camera_outlined),
              label: const Text('Fiş Çek'),
            )
          : null,
      bottomNavigationBar: NavigationBar(
        selectedIndex: _index,
        onDestinationSelected: _goTo,
        destinations: const [
          NavigationDestination(
              icon: Icon(Icons.dashboard_outlined),
              selectedIcon: Icon(Icons.dashboard),
              label: 'Özet'),
          NavigationDestination(
              icon: Icon(Icons.people_outline),
              selectedIcon: Icon(Icons.people),
              label: 'Mükellefler'),
          NavigationDestination(
              icon: Icon(Icons.receipt_long_outlined),
              selectedIcon: Icon(Icons.receipt_long),
              label: 'Fişler'),
          NavigationDestination(
              icon: Icon(Icons.notifications_outlined),
              selectedIcon: Icon(Icons.notifications),
              label: 'Tebligat'),
          NavigationDestination(
              icon: Icon(Icons.settings_outlined),
              selectedIcon: Icon(Icons.settings),
              label: 'Ayarlar'),
        ],
      ),
    );
  }
}

/// Özet sekmesi: özet kartlar (mükellef, bekleyen fiş, okunmamış tebligat,
/// süresi yaklaşan tebligat kırmızı) + hızlı fiş çekme.
class DashboardTab extends StatefulWidget {
  const DashboardTab({super.key, required this.onNavigate});

  final void Function(int tabIndex) onNavigate;

  @override
  State<DashboardTab> createState() => _DashboardTabState();
}

class _DashboardTabState extends State<DashboardTab> {
  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) context.read<DashboardProvider>().load();
    });
  }

  @override
  Widget build(BuildContext context) {
    final fullName = context.watch<AuthProvider>().fullName;
    final dashboard = context.watch<DashboardProvider>();

    return Scaffold(
      appBar: AppBar(
        title: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const Text('Mali Müşavir', style: TextStyle(fontSize: 18)),
            if (fullName != null && fullName.isNotEmpty)
              Text(
                'Hoş geldiniz, $fullName',
                style:
                    const TextStyle(fontSize: 12.5, color: AppColors.navy300),
              ),
          ],
        ),
        toolbarHeight: 64,
      ),
      body: RefreshIndicator(
        onRefresh: () => context.read<DashboardProvider>().load(),
        child: _buildBody(dashboard),
      ),
    );
  }

  Widget _buildBody(DashboardProvider dashboard) {
    if (dashboard.loading && !dashboard.hasData) {
      return ListView(
        children: const [
          SizedBox(height: 200),
          Center(child: CircularProgressIndicator()),
          SizedBox(height: 12),
          Center(
            child: Text('Özet yükleniyor...',
                style: TextStyle(color: AppColors.navy300)),
          ),
        ],
      );
    }

    if (dashboard.error != null && !dashboard.hasData) {
      return ListView(
        children: [
          const SizedBox(height: 160),
          ErrorBanner(
            message: dashboard.error!,
            onRetry: () => context.read<DashboardProvider>().load(),
          ),
        ],
      );
    }

    return ListView(
      physics: const AlwaysScrollableScrollPhysics(),
      padding: const EdgeInsets.all(16),
      children: [
        if (dashboard.error != null)
          Padding(
            padding: const EdgeInsets.only(bottom: 8),
            child: ErrorBanner(
              message: dashboard.error!,
              onRetry: () => context.read<DashboardProvider>().load(),
            ),
          ),
        Row(
          children: [
            Expanded(
              child: _SummaryCard(
                icon: Icons.people_outline,
                label: 'Mükellef',
                value: dashboard.clientCount,
                color: AppColors.navy300,
                onTap: () => widget.onNavigate(1),
              ),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: _SummaryCard(
                icon: Icons.receipt_long_outlined,
                label: 'Bekleyen Fiş',
                value: dashboard.pendingReceiptCount,
                color: AppColors.warning,
                onTap: () => widget.onNavigate(2),
              ),
            ),
          ],
        ),
        const SizedBox(height: 12),
        Row(
          children: [
            Expanded(
              child: _SummaryCard(
                icon: Icons.mark_email_unread_outlined,
                label: 'Okunmamış Tebligat',
                value: dashboard.unreadTebligatCount,
                color: AppColors.info,
                onTap: () => widget.onNavigate(3),
              ),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: _SummaryCard(
                icon: Icons.warning_amber_outlined,
                label: 'Süresi Yaklaşan',
                value: dashboard.urgentTebligatCount,
                color: AppColors.danger,
                highlighted: dashboard.urgentTebligatCount > 0,
                onTap: () => widget.onNavigate(3),
              ),
            ),
          ],
        ),
        const SizedBox(height: 24),
        Container(
          padding: const EdgeInsets.all(16),
          decoration: BoxDecoration(
            color: AppColors.navy800,
            borderRadius: BorderRadius.circular(16),
            border: Border.all(color: AppColors.navy700),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              const Text(
                'Sahada fiş mi topladınız?',
                style: TextStyle(
                  color: AppColors.navy100,
                  fontSize: 16,
                  fontWeight: FontWeight.w600,
                ),
              ),
              const SizedBox(height: 6),
              const Text(
                'Kamerayla çekin veya galeriden seçin; OCR satıcı, tutar ve '
                'KDV bilgisini çıkarıp hesap kodu önerir.',
                style: TextStyle(color: AppColors.navy300, fontSize: 13.5),
              ),
              const SizedBox(height: 14),
              FilledButton.icon(
                onPressed: () => Navigator.of(context).push(
                  MaterialPageRoute<void>(
                    builder: (_) => const ReceiptUploadScreen(),
                  ),
                ),
                icon: const Icon(Icons.photo_camera_outlined),
                label: const Text('Fiş Çek / Yükle'),
              ),
            ],
          ),
        ),
        const SizedBox(height: 80),
      ],
    );
  }
}

class _SummaryCard extends StatelessWidget {
  const _SummaryCard({
    required this.icon,
    required this.label,
    required this.value,
    required this.color,
    required this.onTap,
    this.highlighted = false,
  });

  final IconData icon;
  final String label;
  final int value;
  final Color color;
  final VoidCallback onTap;
  final bool highlighted;

  @override
  Widget build(BuildContext context) {
    return InkWell(
      onTap: onTap,
      borderRadius: BorderRadius.circular(16),
      child: Container(
        padding: const EdgeInsets.all(14),
        decoration: BoxDecoration(
          color: highlighted
              // ignore: deprecated_member_use
              ? color.withOpacity(0.12)
              : AppColors.navy800,
          borderRadius: BorderRadius.circular(16),
          border: Border.all(
            // ignore: deprecated_member_use
            color: highlighted ? color.withOpacity(0.5) : AppColors.navy700,
          ),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Icon(icon, color: color, size: 22),
            const SizedBox(height: 10),
            Text(
              '$value',
              style: TextStyle(
                color: highlighted ? color : AppColors.navy100,
                fontSize: 26,
                fontWeight: FontWeight.w700,
              ),
            ),
            const SizedBox(height: 2),
            Text(
              label,
              style: const TextStyle(color: AppColors.navy300, fontSize: 12.5),
            ),
          ],
        ),
      ),
    );
  }
}
