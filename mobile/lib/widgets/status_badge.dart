import 'package:flutter/material.dart';

import '../theme.dart';

/// Küçük renkli rozet (web'deki durum rozetleriyle aynı renk mantığı).
class PillBadge extends StatelessWidget {
  const PillBadge({super.key, required this.label, required this.color});

  final String label;
  final Color color;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(
        // ignore: deprecated_member_use
        color: color.withOpacity(0.15),
        borderRadius: BorderRadius.circular(999),
        // ignore: deprecated_member_use
        border: Border.all(color: color.withOpacity(0.45)),
      ),
      child: Text(
        label,
        style: TextStyle(
          color: color,
          fontSize: 12,
          fontWeight: FontWeight.w600,
        ),
      ),
    );
  }
}

/// Fiş durumu rozeti: Uploaded/Processing/NeedsReview/Approved/Rejected.
class ReceiptStatusBadge extends StatelessWidget {
  const ReceiptStatusBadge({super.key, required this.status});

  final String status;

  static const Map<String, (String, Color)> _styles = {
    'Uploaded': ('Yüklendi', AppColors.info),
    'Processing': ('İşleniyor', AppColors.warning),
    'NeedsReview': ('Onay Bekliyor', AppColors.warning),
    'Approved': ('Onaylandı', AppColors.success),
    'Rejected': ('Reddedildi', AppColors.danger),
  };

  @override
  Widget build(BuildContext context) {
    final (label, color) = _styles[status] ?? (status, AppColors.navy300);
    return PillBadge(label: label, color: color);
  }
}
