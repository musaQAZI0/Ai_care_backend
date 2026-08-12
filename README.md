# AiCare Backend

ASP.NET Core backend foundation for the Digital Care Platform Phase 1 MVP.

## Current Status

- ASP.NET Core Web API scaffolded.
- Clean solution structure added.
- Phase 1 REST endpoints added.
- In-memory repository added for frontend integration.
- CORS enabled for the React/Vite frontend on `http://127.0.0.1:5173` and `http://localhost:5173`.
- PostgreSQL, EF Core, authentication, authorization, and audit persistence are still pending.

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
dotnet build AiCare.sln
```

## Phase 1 Endpoints

```text
GET    /
GET    /api/phase1/patients
GET    /api/phase1/patients/{id}
POST   /api/phase1/patients
GET    /api/phase1/clinicians
GET    /api/phase1/appointments
POST   /api/phase1/appointments
PATCH  /api/phase1/appointments/{id}/status
GET    /api/phase1/messages
POST   /api/phase1/messages
GET    /api/phase1/notifications
GET    /api/phase1/admin/users
PATCH  /api/phase1/admin/users/{id}/role
GET    /api/phase1/audit-events
```

