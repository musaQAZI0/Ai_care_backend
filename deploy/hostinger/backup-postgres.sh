#!/usr/bin/env sh
set -eu

cd "$(dirname "$0")"
mkdir -p backups
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
file="backups/aicare-${stamp}.dump"

docker compose exec -T postgres pg_dump \
  --username "${POSTGRES_USER}" \
  --dbname "${POSTGRES_DB}" \
  --format=custom \
  --no-owner \
  --no-privileges > "$file"

# Retain 14 days of local backups by default.
find backups -type f -name 'aicare-*.dump' -mtime +14 -delete
printf 'Backup created: %s\n' "$file"
