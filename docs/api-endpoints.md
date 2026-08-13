# AiCare Backend API

Base URL:

```text
https://ai-care-backend-yeoh.onrender.com
```

Local URL example:

```text
http://127.0.0.1:5088
```

## Authentication

Production `/api/phase1/*` endpoints require a JWT bearer token.

```http
POST /api/auth/login
Content-Type: application/json

{
  "userName": "admin",
  "password": "Admin123!"
}
```

Use the returned token:

```text
Authorization: Bearer YOUR_TOKEN
```

## Public Health

```text
GET /health
GET /health/db
GET /health/storage
GET /status/config
```

## Core Endpoints

```text
GET    /api/auth/me

GET    /api/phase1/dashboard

GET    /api/phase1/service-users
GET    /api/phase1/service-users/{id}
POST   /api/phase1/service-users
PUT    /api/phase1/service-users/{id}
DELETE /api/phase1/service-users/{id}

GET    /api/phase1/care-workers
GET    /api/phase1/care-workers/{id}
POST   /api/phase1/care-workers
PUT    /api/phase1/care-workers/{id}
DELETE /api/phase1/care-workers/{id}

GET    /api/phase1/visits
GET    /api/phase1/visits/{id}
POST   /api/phase1/visits
PUT    /api/phase1/visits/{id}
PATCH  /api/phase1/visits/{id}/status
POST   /api/phase1/visits/{id}/check-in
POST   /api/phase1/visits/{id}/check-out
DELETE /api/phase1/visits/{id}

GET    /api/phase1/care-plans
GET    /api/phase1/care-plans/{id}
POST   /api/phase1/care-plans
PUT    /api/phase1/care-plans/{id}
DELETE /api/phase1/care-plans/{id}

GET    /api/phase1/risk-assessments
GET    /api/phase1/risk-assessments/{id}
POST   /api/phase1/risk-assessments
PUT    /api/phase1/risk-assessments/{id}
DELETE /api/phase1/risk-assessments/{id}

GET    /api/phase1/family-members
GET    /api/phase1/family-members/{id}
POST   /api/phase1/family-members
PUT    /api/phase1/family-members/{id}
DELETE /api/phase1/family-members/{id}

GET    /api/phase1/documents
GET    /api/phase1/documents/{id}
POST   /api/phase1/documents
POST   /api/phase1/documents/upload
GET    /api/phase1/documents/{id}/download-url
PUT    /api/phase1/documents/{id}
DELETE /api/phase1/documents/{id}

GET    /api/phase1/medications
GET    /api/phase1/medications/{id}
POST   /api/phase1/medications
PUT    /api/phase1/medications/{id}
DELETE /api/phase1/medications/{id}

GET    /api/phase1/mar
GET    /api/phase1/mar/{id}
POST   /api/phase1/mar
POST   /api/phase1/mar/{id}/administer
POST   /api/phase1/mar/{id}/skip
POST   /api/phase1/mar/{id}/refuse

GET    /api/phase1/care-notes
GET    /api/phase1/care-notes/{id}
POST   /api/phase1/care-notes
PUT    /api/phase1/care-notes/{id}
DELETE /api/phase1/care-notes/{id}

GET    /api/phase1/incidents
GET    /api/phase1/incidents/{id}
POST   /api/phase1/incidents
PUT    /api/phase1/incidents/{id}
DELETE /api/phase1/incidents/{id}

GET    /api/phase1/messages
POST   /api/phase1/messages

GET    /api/phase1/notifications
GET    /api/phase1/notifications?unreadOnly=true
GET    /api/phase1/notifications/unread-count
GET    /api/phase1/notifications/{id}
POST   /api/phase1/notifications/send
POST   /api/phase1/notifications/{id}/read
POST   /api/phase1/notifications/{id}/unread
POST   /api/phase1/notifications/read-all
DELETE /api/phase1/notifications/{id}

GET    /api/phase1/payroll-runs
GET    /api/phase1/payroll-runs?status=Generated
GET    /api/phase1/payroll-runs/{id}
GET    /api/phase1/payroll-runs/{id}/export
POST   /api/phase1/payroll-runs/generate
POST   /api/phase1/payroll-runs/{id}/approve
POST   /api/phase1/payroll-runs/{id}/reject

GET    /api/phase1/invoices
GET    /api/phase1/invoices?status=Generated
GET    /api/phase1/invoices/{id}
POST   /api/phase1/invoices/generate
GET    /api/phase1/invoices/{id}/lines
POST   /api/phase1/invoices/{id}/approve
POST   /api/phase1/invoices/{id}/record-payment
POST   /api/phase1/invoices/{id}/void

GET    /api/phase1/admin/users
POST   /api/phase1/admin/users
PATCH  /api/phase1/admin/users/{id}/role
GET    /api/phase1/audit-events

GET    /api/phase1/family/service-users/{id}/timeline
GET    /api/phase1/family/service-users/{id}/dashboard
POST   /api/phase1/family/service-users/{id}/preferences
GET    /api/phase1/family/service-users/{id}/monthly-report

GET    /api/phase1/storage/status
```

Family member logins must be created with `role: "FamilyMember"` and a valid `familyMemberId`. Family users can only access family portal endpoints for their linked service user.

## Security Notes

Render must have these set with strong values:

```text
JwtOptions__Issuer
JwtOptions__Audience
JwtOptions__SigningKey
```

`JwtOptions__SigningKey` must be at least 32 characters. API responses include `X-Request-ID` and security headers for easier debugging and safer browser behavior.

## Demo Data

Demo routes are disabled unless these env vars are set:

```text
Demo__Enabled=true
Demo__SeedKey=YOUR_SECRET_DEMO_KEY
```

Requests must include:

```text
X-Demo-Key: YOUR_SECRET_DEMO_KEY
Authorization: Bearer YOUR_TOKEN
```

```text
POST   /api/demo/seed
DELETE /api/demo/reset
```

## Supabase File Upload

Multipart form fields:

```text
file
serviceUserId
category
uploadedBy
```

PowerShell example:

```powershell
$headers = @{ Authorization = "Bearer $token" }
$form = @{
  serviceUserId = "SERVICE_USER_ID"
  category = "Demo document"
  uploadedBy = "admin"
  file = Get-Item ".\demo.txt"
}
Invoke-RestMethod -Uri "$base/api/phase1/documents/upload" -Method Post -Headers $headers -Form $form
```

If `Supabase__PublicFileBaseUrl` is set, download URL returns a public URL. If it is empty, the API creates a signed URL for private buckets.

## Smoke Test

```powershell
.\scripts\smoke-live.ps1 -BaseUrl "https://ai-care-backend-yeoh.onrender.com"
```

With demo seed:

```powershell
.\scripts\smoke-live.ps1 -SeedDemo -DemoKey "YOUR_SECRET_DEMO_KEY"
```
