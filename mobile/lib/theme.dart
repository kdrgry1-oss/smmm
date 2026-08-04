import 'package:flutter/material.dart';

/// Web ile aynı koyu lacivert (navy) paleti + durum renkleri (yeşil/sarı/kırmızı).
class AppColors {
  static const navy950 = Color(0xFF0B1830);
  static const navy900 = Color(0xFF152642);
  static const navy800 = Color(0xFF1E3253);
  static const navy700 = Color(0xFF253D66);
  static const navy600 = Color(0xFF2C4B7E);
  static const navy500 = Color(0xFF3B6098);
  static const navy400 = Color(0xFF5C80B3);
  static const navy300 = Color(0xFF8FA8CE);
  static const navy200 = Color(0xFFBCCBE4);
  static const navy100 = Color(0xFFDCE5F2);

  /// Yeşil: onaylandı / yüksek güven / süre rahat.
  static const success = Color(0xFF4ADE80);

  /// Sarı: bekliyor / orta güven / süre yaklaşıyor.
  static const warning = Color(0xFFFBBF24);

  /// Kırmızı: reddedildi / düşük güven / acil süre.
  static const danger = Color(0xFFF87171);

  /// Mavi: bilgilendirme.
  static const info = Color(0xFF93C5FD);
}

ThemeData buildAppTheme() {
  final base = ThemeData(
    useMaterial3: true,
    brightness: Brightness.dark,
    scaffoldBackgroundColor: AppColors.navy950,
    colorScheme: const ColorScheme.dark(
      primary: AppColors.navy300,
      onPrimary: AppColors.navy950,
      secondary: AppColors.navy400,
      onSecondary: Colors.white,
      surface: AppColors.navy900,
      onSurface: AppColors.navy100,
      error: AppColors.danger,
      onError: AppColors.navy950,
    ),
  );

  OutlineInputBorder border(Color color, [double width = 1]) =>
      OutlineInputBorder(
        borderRadius: BorderRadius.circular(12),
        borderSide: BorderSide(color: color, width: width),
      );

  return base.copyWith(
    cardColor: AppColors.navy800,
    dividerColor: AppColors.navy700,
    appBarTheme: const AppBarTheme(
      backgroundColor: AppColors.navy900,
      foregroundColor: AppColors.navy100,
      elevation: 0,
      centerTitle: false,
    ),
    inputDecorationTheme: InputDecorationTheme(
      filled: true,
      fillColor: AppColors.navy800,
      border: border(AppColors.navy700),
      enabledBorder: border(AppColors.navy700),
      focusedBorder: border(AppColors.navy300, 1.5),
      errorBorder: border(AppColors.danger),
      focusedErrorBorder: border(AppColors.danger, 1.5),
      labelStyle: const TextStyle(color: AppColors.navy200),
      hintStyle: const TextStyle(color: AppColors.navy400),
      helperStyle: const TextStyle(color: AppColors.navy300),
    ),
    filledButtonTheme: FilledButtonThemeData(
      style: FilledButton.styleFrom(
        backgroundColor: AppColors.navy500,
        foregroundColor: Colors.white,
        minimumSize: const Size(64, 48),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
      ),
    ),
    outlinedButtonTheme: OutlinedButtonThemeData(
      style: OutlinedButton.styleFrom(
        foregroundColor: AppColors.navy200,
        side: const BorderSide(color: AppColors.navy600),
        minimumSize: const Size(64, 48),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
      ),
    ),
    textButtonTheme: TextButtonThemeData(
      style: TextButton.styleFrom(foregroundColor: AppColors.navy300),
    ),
    navigationBarTheme: const NavigationBarThemeData(
      backgroundColor: AppColors.navy900,
      indicatorColor: AppColors.navy600,
    ),
    floatingActionButtonTheme: const FloatingActionButtonThemeData(
      backgroundColor: AppColors.navy500,
      foregroundColor: Colors.white,
    ),
    snackBarTheme: const SnackBarThemeData(
      backgroundColor: AppColors.navy700,
      contentTextStyle: TextStyle(color: Colors.white),
      behavior: SnackBarBehavior.floating,
    ),
    chipTheme: base.chipTheme.copyWith(
      backgroundColor: AppColors.navy800,
      selectedColor: AppColors.navy500,
      labelStyle: const TextStyle(color: AppColors.navy100),
      side: const BorderSide(color: AppColors.navy700),
    ),
    bottomSheetTheme: const BottomSheetThemeData(
      backgroundColor: AppColors.navy900,
      modalBackgroundColor: AppColors.navy900,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
    ),
    progressIndicatorTheme: const ProgressIndicatorThemeData(
      color: AppColors.navy300,
      linearTrackColor: AppColors.navy700,
    ),
  );
}
