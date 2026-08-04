import 'package:flutter/material.dart';

import '../theme.dart';
import 'status_badge.dart';

/// Güven rozeti (web ile aynı eşikler): ≥ %85 yeşil, %60–85 sarı, < %60 kırmızı.
class ConfidenceBadge extends StatelessWidget {
  const ConfidenceBadge({super.key, required this.confidence});

  /// 0.0–1.0 arası güven skoru; null ise rozet gizlenir.
  final double? confidence;

  @override
  Widget build(BuildContext context) {
    final value = confidence;
    if (value == null) return const SizedBox.shrink();

    final percent = (value * 100).round();
    final color = value >= 0.85
        ? AppColors.success
        : value >= 0.6
            ? AppColors.warning
            : AppColors.danger;

    return PillBadge(label: '%$percent güven', color: color);
  }
}
