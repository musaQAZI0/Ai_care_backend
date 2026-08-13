# AiCare Backend

ASP.NET Core backend for the AiCare social care platform demo.

## Current Status

- ASP.NET Core Web API with PostgreSQL/EF Core persistence.
- JWT authentication and role-based authorization.
- Supabase document storage support.
- Health, storage, and safe config status endpoints.
- Core social care CRUD, documents, eMAR, notifications, payroll/invoices, audit logs, and demo seed/reset.
- Render deployment files included.

## Solution Structure

```text
backend/
  src/
    AiCare.Api/
    AiCare.Application/
    AiCare.Domain/
    AiCare.Infrastructure/
  tests/
    AiCare.Tests/
```

## Run

```powershell
$env:DOTNET_CLI_HOME='C:\Users\LENOVO\Desktop\Ai_Care\ai-care\.dotnet-cli'
dotnet run --project .\backend\src\AiCare.Api\AiCare.Api.csproj --urls http://127.0.0.1:5088
```

## Build

```powershell
$env:DOTNET_CLI_HOME='C:\Users\LENOVO\Desktop\Ai_Care\ai-care\.dotnet-cli'
dotnet build AiCare.Backend.sln
```

## Key Endpoints

```text
GET    /
GET    /health
GET    /health/db
GET    /health/storage
GET    /status/config

POST   /api/auth/login
GET    /api/auth/me

GET    /api/phase1/service-users
GET    /api/phase1/care-workers
GET    /api/phase1/visits
GET    /api/phase1/documents
GET    /api/phase1/medications
GET    /api/phase1/mar
GET    /api/phase1/notifications
GET    /api/phase1/payroll-runs
GET    /api/phase1/invoices
GET    /api/phase1/audit-events
```

Full endpoint notes are in `docs/api-endpoints.md`.
