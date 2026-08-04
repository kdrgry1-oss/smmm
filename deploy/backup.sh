#!/usr/bin/env bash
# Gece yedeği: PostgreSQL dump + belge deposu → şifreli arşiv → (varsa) offsite S3-uyumlu depo.
# Kurulum: crontab -e →  15 3 * * * /opt/malimusavir/deploy/backup.sh >> /var/log/mm-backup.log 2>&1
set -euo pipefail

DEPLOY_DIR="$(cd "$(dirname "$0")" && pwd)"
ENV_FILE="$DEPLOY_DIR/.env"
[ -f "$ENV_FILE" ] && set -a && . "$ENV_FILE" && set +a

STAMP=$(date +%Y%m%d-%H%M)
BACKUP_DIR="/var/backups/malimusavir"
KEEP_DAYS=14
mkdir -p "$BACKUP_DIR"

echo "[$(date)] Yedek başlıyor: $STAMP"

# 1) PostgreSQL dump (compose içindeki db konteynerinden)
docker compose -f "$DEPLOY_DIR/docker-compose.prod.yml" --env-file "$ENV_FILE" \
  exec -T db pg_dump -U "${DB_USER}" -d malimusavir --format=custom \
  > "$BACKUP_DIR/db-$STAMP.dump"

# 2) Belge deposu (api storage volume)
docker run --rm \
  -v "$(docker volume ls -q | grep apistorage | head -1)":/data:ro \
  -v "$BACKUP_DIR":/backup alpine \
  tar czf "/backup/storage-$STAMP.tar.gz" -C /data .

# 3) Tek arşiv + GPG simetrik şifreleme (parola .env'de)
tar cf "$BACKUP_DIR/mm-$STAMP.tar" -C "$BACKUP_DIR" "db-$STAMP.dump" "storage-$STAMP.tar.gz"
gpg --batch --yes --symmetric --cipher-algo AES256 \
    --passphrase "${BACKUP_GPG_PASSPHRASE}" \
    -o "$BACKUP_DIR/mm-$STAMP.tar.gpg" "$BACKUP_DIR/mm-$STAMP.tar"
rm -f "$BACKUP_DIR/mm-$STAMP.tar" "$BACKUP_DIR/db-$STAMP.dump" "$BACKUP_DIR/storage-$STAMP.tar.gz"

# 4) Offsite gönderim (rclone kuruluysa ve remote tanımlıysa)
if [ -n "${RCLONE_REMOTE:-}" ] && command -v rclone >/dev/null 2>&1; then
  rclone copy "$BACKUP_DIR/mm-$STAMP.tar.gpg" "${RCLONE_REMOTE}:malimusavir-yedek/" --transfers 2
  echo "[$(date)] Offsite gönderildi: ${RCLONE_REMOTE}:malimusavir-yedek/mm-$STAMP.tar.gpg"
else
  echo "[$(date)] UYARI: offsite remote tanımlı değil — yedek sadece yerelde."
fi

# 5) Eski yerel yedekleri temizle
find "$BACKUP_DIR" -name "mm-*.tar.gpg" -mtime +$KEEP_DAYS -delete

echo "[$(date)] Yedek tamam: mm-$STAMP.tar.gpg ($(du -h "$BACKUP_DIR/mm-$STAMP.tar.gpg" | cut -f1))"

# GERİ YÜKLEME (özet — ayrıntı README-DEPLOY.md):
#   gpg -d mm-STAMP.tar.gpg > mm.tar && tar xf mm.tar
#   docker compose ... exec -T db pg_restore -U $DB_USER -d malimusavir --clean db-STAMP.dump
#   storage tar'ını apistorage volume'una geri aç
