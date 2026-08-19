#!/usr/bin/env sh
set -eu

cd "$(dirname "$0")"
env_file="../../.env"
[ -f "$env_file" ] || { echo "Missing production environment file: $env_file" >&2; exit 2; }
mkdir -p backups
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
file="backups/aicare-${stamp}.dump"

# Read only the two non-secret identifiers needed for pg_dump arguments.
POSTGRES_USER="$(grep '^POSTGRES_USER=' "$env_file" | tail -1 | cut -d= -f2-)"
POSTGRES_DB="$(grep '^POSTGRES_DB=' "$env_file" | tail -1 | cut -d= -f2-)"
[ -n "$POSTGRES_USER" ] && [ -n "$POSTGRES_DB" ] || { echo "POSTGRES_USER/POSTGRES_DB missing from $env_file" >&2; exit 2; }

docker compose --env-file "$env_file" exec -T postgres pg_dump \
  --username "$POSTGRES_USER" \
  --dbname "$POSTGRES_DB" \
  --format=custom \
  --no-owner \
  --no-privileges > "$file"

# Retain 14 days of local backups by default.
find backups -type f -name 'aicare-*.dump' -mtime +14 -delete
printf 'Backup created: %s\n' "$file"
