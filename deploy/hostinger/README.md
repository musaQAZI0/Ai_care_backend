# AiCare Hostinger VPS production runbook

This package is designed for a Hostinger VPS with Docker Compose. It keeps PostgreSQL private on the Docker network, serves the React app and API behind Nginx, uses Supabase for private care-document storage, and supports authenticated SMTP such as Hostinger Email.

## 1. Prerequisites

- Hostinger VPS with Docker Engine and Docker Compose plugin
- DNS A records for the app domain and API subdomain pointing to the VPS
- Hostinger Email mailbox (for example `no-reply@care.example.com`) or another authenticated SMTP account
- Existing Supabase private `care-documents` bucket and service-role credentials
- Firewall allowing only SSH, HTTP 80 and HTTPS 443 from the public internet

## 2. Build the images

Backend repository:

```sh
docker build -t aicare-backend:local .
docker build --target migrations -t aicare-migrator:local .
```

Frontend repository:

```sh
docker build \
  --build-arg VITE_API_BASE_URL=https://api.care.example.com \
  -t aicare-frontend:local .
```

Do not build the frontend with a private/internal API URL; `VITE_API_BASE_URL` is compiled into the browser bundle.

## 3. Configure secrets

From the backend repository root:

```sh
cp .env.hostinger.example .env
chmod 600 .env
```

Replace every `CHANGE_ME` value. Use the real HTTPS frontend domain for `Cors__AllowedOrigins__0` and `FamilyPortal__FrontendBaseUrl`. Keep `Supabase__PublicFileBaseUrl` empty.

Never commit `.env`.

## 4. Start PostgreSQL and apply migrations explicitly

From `deploy/hostinger`:

```sh
docker compose --env-file ../../.env up -d postgres
docker compose --env-file ../../.env --profile migration run --rm migrate
```

Migrations are intentionally not applied automatically by normal API startup. Review the migration set and take a backup before applying schema changes to an existing production database.

## 5. Issue the first TLS certificate

The normal Nginx configuration expects a certificate to exist, so use the bootstrap override for the first issuance.

```sh
docker compose --env-file ../../.env \
  -f docker-compose.yml -f docker-compose.bootstrap.yml \
  up -d nginx
```

Then request one certificate containing both names:

```sh
docker compose --env-file ../../.env --profile certbot run --rm certbot \
  certonly --webroot -w /var/www/certbot \
  -d care.example.com -d api.care.example.com \
  --email admin@care.example.com --agree-tos --no-eff-email
```

Replace the example names/email with the real values. After issuance:

```sh
docker compose --env-file ../../.env \
  -f docker-compose.yml -f docker-compose.bootstrap.yml down

docker compose --env-file ../../.env up -d
```

The production Nginx configuration redirects HTTP to HTTPS and permits TLS 1.2/1.3 only.

## 6. Health verification

Before UAT, verify:

```sh
curl -fsS https://api.care.example.com/health/live
curl -fsS https://api.care.example.com/health/ready
```

`/health/live` proves the process is alive. `/health/ready` is the release gate for database and external readiness checks.

Also inspect:

```sh
docker compose --env-file ../../.env ps
docker compose --env-file ../../.env logs --tail=200 backend
docker compose --env-file ../../.env logs --tail=200 nginx
```

Do not expose PostgreSQL port 5432 publicly.

## 7. Backups

Create a database backup:

```sh
./backup-postgres.sh
```

The script keeps 14 days of local dumps by default. Copy backups to a second encrypted location as part of the production operations policy.

A restore is deliberately guarded:

```sh
./restore-postgres.sh backups/aicare-YYYYMMDDTHHMMSSZ.dump RESTORE_AICARE
```

Run restore drills on a non-production database first. After a restore, verify both health endpoints and the critical UAT flows before reopening traffic.

## 8. SMTP validation

Production startup fails closed unless SMTP is enabled and configured. Send a real Family Portal invitation to an approved test mailbox and confirm:

- sender/domain authentication is valid (SPF, DKIM and DMARC at DNS level)
- the activation link uses the production HTTPS domain
- no care/medical detail is exposed in the email body
- the invitation can be activated once only
- expired/replayed invitations are rejected

## 9. Launch UAT checklist

Run on the exact production release build.

### Administrator
- sign in and sign out
- create, update and retrieve a service user
- create/review/approve/sign/activate a care plan
- upload, obtain authorized download access and delete a test document
- configure/verify Family Portal access and permissions
- send a Family invitation and confirm delivery
- create a secure conversation and send/reply with an attachment

### Care Manager
- verify allowed person/care-plan/messaging operations
- verify restricted administration actions are denied

### Care Worker
- verify assigned care-workflow access
- verify management/administration actions are denied

### Family Member
- activate from invitation
- sign in to Family Portal
- view only authorized person data
- sign a care plan only when `SignCarePlan` is granted
- view documents only when `ViewDocuments` is granted
- message the care team only when `MessageCareTeam` is active
- verify suspend/revoke immediately removes access to existing conversations/data

### Negative/security checks
- wrong password and lockout behavior
- invalid/expired JWT
- revoked refresh token/logout
- wrong-tenant access returns no protected data
- invalid/oversized/malicious document upload is rejected
- unauthorized document/care-plan/message access is denied
- Family permissions removed while logged in are enforced on the next request
- API/network failure surfaces a safe user-facing error

## 10. Release gate

Do not call the release production-complete until all of these are true:

- backend CI green, including PostgreSQL regression tests and Docker build
- frontend CI green, including unit tests and Docker build
- Playwright critical journeys green
- database migration completed successfully
- `/health/live` and `/health/ready` green on the VPS
- SMTP invitation delivered and activated
- Supabase private upload/download/delete verified from the VPS
- all four role UAT checklists pass
- negative tenant/permission tests pass
- a backup is created and a restore drill has been documented
