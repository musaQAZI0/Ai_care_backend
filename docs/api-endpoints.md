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

GET    /api/phase1/storage/status
```

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
