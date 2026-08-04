import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../api/api_client.dart';
import '../state/auth_provider.dart';
import '../theme.dart';
import '../widgets/error_banner.dart';

/// Giriş ekranı: e-posta + şifre → JWT (güvenli depoda saklanır).
class LoginScreen extends StatefulWidget {
  const LoginScreen({super.key});

  @override
  State<LoginScreen> createState() => _LoginScreenState();
}

class _LoginScreenState extends State<LoginScreen> {
  final _formKey = GlobalKey<FormState>();
  final _emailController = TextEditingController();
  final _passwordController = TextEditingController();

  bool _obscure = true;
  bool _submitting = false;
  String? _error;

  static final RegExp _emailRegex = RegExp(r'^[^@\s]+@[^@\s]+\.[^@\s]+$');

  @override
  void dispose() {
    _emailController.dispose();
    _passwordController.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (!(_formKey.currentState?.validate() ?? false)) return;

    setState(() {
      _submitting = true;
      _error = null;
    });

    try {
      await context
          .read<AuthProvider>()
          .login(_emailController.text, _passwordController.text);
      // Başarıda kök widget loggedIn'i izlediği için otomatik ana ekrana geçilir.
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.message);
    } finally {
      if (mounted) setState(() => _submitting = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final sessionExpired = context.watch<AuthProvider>().sessionExpired;

    return Scaffold(
      body: SafeArea(
        child: Center(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(24),
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 420),
              child: Form(
                key: _formKey,
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    const Icon(Icons.account_balance,
                        size: 56, color: AppColors.navy300),
                    const SizedBox(height: 16),
                    const Text(
                      'Mali Müşavir',
                      textAlign: TextAlign.center,
                      style: TextStyle(
                        fontSize: 26,
                        fontWeight: FontWeight.w700,
                        color: AppColors.navy100,
                      ),
                    ),
                    const SizedBox(height: 6),
                    const Text(
                      'SMMM ofisiniz için saha uygulaması',
                      textAlign: TextAlign.center,
                      style: TextStyle(color: AppColors.navy300, fontSize: 14),
                    ),
                    const SizedBox(height: 28),
                    if (sessionExpired && _error == null)
                      const Padding(
                        padding: EdgeInsets.only(bottom: 12),
                        child: ErrorBanner(
                          message:
                              'Oturumunuzun süresi doldu. Lütfen tekrar giriş yapın.',
                        ),
                      ),
                    if (_error != null)
                      Padding(
                        padding: const EdgeInsets.only(bottom: 12),
                        child: ErrorBanner(message: _error!),
                      ),
                    TextFormField(
                      controller: _emailController,
                      keyboardType: TextInputType.emailAddress,
                      autocorrect: false,
                      textInputAction: TextInputAction.next,
                      decoration: const InputDecoration(
                        labelText: 'E-posta',
                        prefixIcon: Icon(Icons.mail_outline),
                      ),
                      validator: (value) {
                        final v = value?.trim() ?? '';
                        if (v.isEmpty) return 'E-posta adresi zorunludur.';
                        if (!_emailRegex.hasMatch(v)) {
                          return 'Geçerli bir e-posta adresi girin.';
                        }
                        return null;
                      },
                    ),
                    const SizedBox(height: 14),
                    TextFormField(
                      controller: _passwordController,
                      obscureText: _obscure,
                      textInputAction: TextInputAction.done,
                      onFieldSubmitted: (_) => _submitting ? null : _submit(),
                      decoration: InputDecoration(
                        labelText: 'Şifre',
                        prefixIcon: const Icon(Icons.lock_outline),
                        suffixIcon: IconButton(
                          icon: Icon(_obscure
                              ? Icons.visibility_outlined
                              : Icons.visibility_off_outlined),
                          onPressed: () =>
                              setState(() => _obscure = !_obscure),
                          tooltip:
                              _obscure ? 'Şifreyi göster' : 'Şifreyi gizle',
                        ),
                      ),
                      validator: (value) => (value == null || value.isEmpty)
                          ? 'Şifre zorunludur.'
                          : null,
                    ),
                    const SizedBox(height: 22),
                    FilledButton(
                      onPressed: _submitting ? null : _submit,
                      child: _submitting
                          ? const SizedBox(
                              width: 22,
                              height: 22,
                              child: CircularProgressIndicator(
                                  strokeWidth: 2.5, color: Colors.white),
                            )
                          : const Text('Giriş Yap'),
                    ),
                    const SizedBox(height: 16),
                    const Text(
                      'Hesabınız yoksa web panelinden kayıt olabilirsiniz.',
                      textAlign: TextAlign.center,
                      style: TextStyle(color: AppColors.navy400, fontSize: 12.5),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
