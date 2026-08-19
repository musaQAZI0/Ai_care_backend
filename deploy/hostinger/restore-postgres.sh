#!/usr/bin/env sh
set -eu

if [ "$#" -ne 2 ] || [ "$2" != "RESTORE_AICARE" ]; then
  echo "Usage: $0 <backup.dump> RESTORE_AICARE" >&2
  exit 2
fi

backup="$1"
[ -f "$backup" ] || { echo "Backup file not found: $backup" >&2; exit 2; }

cd "$(dirname "$0")"
cat "$backup" | docker compose exec -T postgres pg_restore \
  --username "${POSTGRES_USER}" \
  --dbname "${POSTGRES_DB}" \
  --clean \
  --if-exists \
  --no-owner \
  --no-privileges

echo "Restore completed. Run the application health checks before reopening traffic."
