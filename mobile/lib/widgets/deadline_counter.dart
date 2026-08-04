import 'package:flutter/material.dart';

import '../theme.dart';
import 'status_badge.dart';

/// e-Tebligat kalan süre sayacı (web ile aynı renk mantığı):
/// süre doldu / ≤ 3 gün → kırmızı, ≤ 10 gün → sarı, diğer → yeşil, süresiz → gri.
class DeadlineCounter extends StatelessWidget {
  const DeadlineCounter({
    super.key,
    required this.daysRemaining,
    this.deadlineType,
  });

  /// null = süresiz bilgilendirme; 0 ve altı = süre doldu.
  final int? daysRemaining;

  /// "İtiraz" / "Ödeme" / "Bilgi" — doluysa etikete eklenir.
  final String? deadlineType;

  @override
  Widget build(BuildContext context) {
    final days = daysRemaining;
    final suffix = (deadlineType == null || deadlineType!.isEmpty)
        ? ''
        : ' · $deadlineType';

    if (days == null) {
      return PillBadge(label: 'Süresiz$suffix', color: AppColors.navy300);
    }
    if (days <= 0) {
      return PillBadge(label: 'Süre doldu$suffix', color: AppColors.danger);
    }
    if (days <= 3) {
      return PillBadge(label: 'Son $days gün$suffix', color: AppColors.danger);
    }
    if (days <= 10) {
      return PillBadge(label: '$days gün kaldı$suffix', color: AppColors.warning);
    }
    return PillBadge(label: '$days gün kaldı$suffix', color: AppColors.success);
  }
}
