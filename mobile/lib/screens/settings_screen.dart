import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../config.dart';
import '../state/auth_provider.dart';
import '../theme.dart';

/// Ayarlar: API adresi (Android emülatörü notu ile) + çıkış.
class SettingsScreen extends StatefulWidget {
  const SettingsScreen({super.key});

  @override
  State<SettingsScreen> createState() => _SettingsScreenState();
}

class _SettingsScreenState extends State<SettingsScreen> {
  final _formKey = GlobalKey<FormState>();
  late final TextEditingController _apiUrlController;
  bool _saving = false;

  @override
  void initState() {
    super.initState();
    _apiUrlController =
        TextEditingController(text: context.read<AppConfig>().apiBaseUrl);
  }

  @override
  void dispose() {
    _apiUrlController.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;

    setState(() => _saving = true);
    final config = context.read<AppConfig>();
    await config.setApiBaseUrl(_apiUrlController.text);
    _apiUrlController.text = config.apiBaseUrl;
    setState(() => _saving = false);

    if (!mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(
      const SnackBar(content: Text('API adresi kaydedildi.')),
    );
  }

  Future<void> _resetUrl() async {
    final config = context.read<AppConfig>();
    await config.resetApiBaseUrl();
    _apiUrlController.text = config.apiBaseUrl;

    if (!mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(
      const SnackBar(content: Text('API adresi varsayılana döndürüldü.')),
    );
  }

  Future<void> _logout() async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        backgroundColor: AppColors.navy800,
        title: const Text('Çıkış yapılsın mı?'),
        content: const Text(
            'Oturumunuz kapatılacak; tekrar giriş yapmanız gerekecek.'),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(false),
            child: const Text('Vazgeç'),
          ),
          FilledButton(
            style: FilledButton.styleFrom(backgroundColor: AppColors.danger),
            onPressed: () => Navigator.of(dialogContext).pop(true),
            child: const Text('Çıkış Yap'),
          ),
        ],
      ),
    );

    if (confirmed == true && mounted) {
      await context.read<AuthProvider>().logout();
    }
  }

  @override
  Widget build(BuildContext context) {
    final auth = context.watch<AuthProvider>();

    return Scaffold(
      appBar: AppBar(title: const Text('Ayarlar')),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          if (auth.fullName != null && auth.fullName!.isNotEmpty)
            Container(
              padding: const EdgeInsets.all(14),
              margin: const EdgeInsets.only(bottom: 16),
              decoration: BoxDecoration(
                color: AppColors.navy800,
                borderRadius: BorderRadius.circular(14),
                border: Border.all(color: AppColors.navy700),
              ),
              child: Row(
                children: [
                  const CircleAvatar(
                    backgroundColor: AppColors.navy600,
                    child: Icon(Icons.person, color: Colors.white),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          auth.fullName!,
                          style: const TextStyle(
                              color: AppColors.navy100,
                              fontWeight: FontWeight.w600),
                        ),
                        if (auth.role != null)
                          Text(
                            auth.role == 'TenantOwner'
                                ? 'Ofis Sahibi'
                                : auth.role!,
                            style: const TextStyle(
                                color: AppColors.navy300, fontSize: 12.5),
                          ),
                      ],
                    ),
                  ),
                ],
              ),
            ),
          const Text(
            'API BAĞLANTISI',
            style: TextStyle(
                color: AppColors.navy400,
                fontSize: 12,
                fontWeight: FontWeight.w700,
                letterSpacing: 0.8),
          ),
          const SizedBox(height: 10),
          Form(
            key: _formKey,
            child: TextFormField(
              controller: _apiUrlController,
              keyboardType: TextInputType.url,
              autocorrect: false,
              decoration: const InputDecoration(
                labelText: 'API adresi',
                hintText: AppConfig.defaultApiBaseUrl,
                prefixIcon: Icon(Icons.link),
              ),
              validator: (value) {
                final v = value?.trim() ?? '';
                if (v.isEmpty) return 'API adresi zorunludur.';
                if (!AppConfig.isValidBaseUrl(v)) {
                  return 'Geçerli bir adres girin (örn: http://10.0.2.2:5000).';
                }
                return null;
              },
            ),
          ),
          const SizedBox(height: 8),
          const Text(
            'Not: Android emülatöründe bilgisayarınızda çalışan API için '
            'http://10.0.2.2:5000 kullanın. iOS simülatöründe '
            'http://localhost:5000, gerçek cihazda bilgisayarınızın yerel IP '
            'adresi (örn: http://192.168.1.20:5000) gerekir.',
            style: TextStyle(color: AppColors.navy400, fontSize: 12.5),
          ),
          const SizedBox(height: 14),
          Row(
            children: [
              Expanded(
                child: FilledButton(
                  onPressed: _saving ? null : _save,
                  child: Text(_saving ? 'Kaydediliyor...' : 'Kaydet'),
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: OutlinedButton(
                  onPressed: _saving ? null : _resetUrl,
                  child: const Text('Varsayılana Dön'),
                ),
              ),
            ],
          ),
          const SizedBox(height: 28),
          const Text(
            'UYGULAMA',
            style: TextStyle(
                color: AppColors.navy400,
                fontSize: 12,
                fontWeight: FontWeight.w700,
                letterSpacing: 0.8),
          ),
          const SizedBox(height: 10),
          Container(
            padding: const EdgeInsets.all(14),
            decoration: BoxDecoration(
              color: AppColors.navy800,
              borderRadius: BorderRadius.circular(14),
              border: Border.all(color: AppColors.navy700),
            ),
            child: const Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text('Mali Müşavir Mobil — v0.1.0 (Faz 6 MVP)',
                    style: TextStyle(color: AppColors.navy100, fontSize: 13.5)),
                SizedBox(height: 6),
                Text(
                  'Anlık bildirimler (yeni tebligat / fiş sonucu) bir sonraki '
                  'fazda eklenecektir: Google cihazlarda FCM, Huawei '
                  'cihazlarda HMS Push Kit.',
                  style: TextStyle(color: AppColors.navy400, fontSize: 12.5),
                ),
              ],
            ),
          ),
          const SizedBox(height: 28),
          OutlinedButton.icon(
            onPressed: _logout,
            style: OutlinedButton.styleFrom(
              foregroundColor: AppColors.danger,
              side: const BorderSide(color: AppColors.danger),
            ),
            icon: const Icon(Icons.logout),
            label: const Text('Çıkış Yap'),
          ),
          const SizedBox(height: 24),
        ],
      ),
    );
  }
}
