#!/usr/bin/env sh
set -eu

if [ "$#" -ne 2 ] || [ "$2" != "RESTORE_AICARE" ]; then
  echo "Usage: $0 <backup.dump> RESTORE_AICARE" >&2
  exit 2
fi

backup="$1"
[ -f "$backup" ] || { echo "Backup file not found: $backup" >&2; exit 2; }
backup="$(cd "$(dirname "$backup")" && pwd)/$(basename "$backup")"

cd "$(dirname "$0")"
env_file="../../.env"
[ -f "$env_file" ] || { echo "Missing production environment file: $env_file" >&2; exit 2; }
POSTGRES_USER="$(grep '^POSTGRES_USER=' "$env_file" | tail -1 | cut -d= -f2-)"
POSTGRES_DB="$(grep '^POSTGRES_DB=' "$env_file" | tail -1 | cut -d= -f2-)"
[ -n "$POSTGRES_USER" ] && [ -n "$POSTGRES_DB" ] || { echo "POSTGRES_USER/POSTGRES_DB missing from $env_file" >&2; exit 2; }

cat "$backup" | docker compose --env-file "$env_file" exec -T postgres pg_restore \
  --username "$POSTGRES_USER" \
  --dbname "$POSTGRES_DB" \
  --clean \
  --if-exists \
  --no-owner \
  --no-privileges

echo "Restore completed. Run /health/live and /health/ready before reopening traffic."
