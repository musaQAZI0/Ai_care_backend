[CmdletBinding()]
param(
    [string]$FrontendPath = (Join-Path (Split-Path $PSScriptRoot -Parent) '..\ai-care'),
    [string]$ContainerName = 'aicare-v1-final-postgres',
    [int]$PostgresPort = 0
)

$ErrorActionPreference = 'Stop'
$backendPath = Split-Path $PSScriptRoot -Parent
$frontendPath = [System.IO.Path]::GetFullPath($FrontendPath)
$testPassword = 'AiCareSyntheticRegression123!'

Set-Location $backendPath
docker info *> $null
if ($LASTEXITCODE -ne 0) { throw 'Docker Desktop is not ready. Start Docker Desktop and rerun this script.' }

$containerExists = docker ps -a --filter "name=^/$ContainerName$" --format '{{.Names}}'
if (-not $containerExists) {
    $publishedPort = if ($PostgresPort -gt 0) { "127.0.0.1:${PostgresPort}:5432" } else { '127.0.0.1::5432' }
    docker run --name $ContainerName `
        -e "POSTGRES_PASSWORD=$testPassword" `
        -p $publishedPort `
        -d postgres:16-alpine | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the synthetic PostgreSQL container.' }
}
else {
    docker start $ContainerName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not start the synthetic PostgreSQL container.' }
}
$container = (docker inspect $ContainerName | ConvertFrom-Json)[0]
$testPassword = (($container.Config.Env | Where-Object { $_.StartsWith('POSTGRES_PASSWORD=') }) -replace '^POSTGRES_PASSWORD=', '')
$binding = $container.HostConfig.PortBindings.'5432/tcp' | Select-Object -First 1
if (-not $testPassword -or -not $binding.HostPort) { throw 'The test container has no PostgreSQL password or published port.' }
$PostgresPort = [int]$binding.HostPort

Write-Host "Waiting for PostgreSQL on port $PostgresPort..."
$ready = $false
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    docker exec $ContainerName pg_isready -U postgres -d postgres *> $null
    if ($LASTEXITCODE -eq 0) { $ready = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $ready) { throw 'Synthetic PostgreSQL did not become ready.' }

$env:AICARE_REGRESSION_CONNECTION = "Host=127.0.0.1;Port=$PostgresPort;Database=postgres;Username=postgres;Password=$testPassword"
$resultsPath = Join-Path $backendPath 'TestResults\FinalV1'
New-Item -ItemType Directory -Force -Path $resultsPath | Out-Null
$filter = @(
    'FullyQualifiedName~IncidentGovernanceRegressionTests'
    'FullyQualifiedName~IncidentAtomicityRegressionTests'
    'FullyQualifiedName~SafeguardingWorkflowRegressionTests'
    'FullyQualifiedName~CareDocumentationRegressionTests'
    'FullyQualifiedName~CareDocumentationAtomicityRegressionTests'
    'FullyQualifiedName~CareDocumentationScopeRegressionTests'
    'FullyQualifiedName~CareNoteAccessRegressionTests'
    'FullyQualifiedName~DataGovernanceComplianceTests'
    'FullyQualifiedName~VisitTaskPlanScopeRegressionTests'
    'FullyQualifiedName~RotaPublicationRegressionTests'
    'FullyQualifiedName~FamilyAccessAtomicityRegressionTests'
    'FullyQualifiedName~FamilyAccessScopeRegressionTests'
    'FullyQualifiedName~FamilyPortalProductionRegressionTests'
    'FullyQualifiedName~FullProviderJourneyRegressionTests'
    'FullyQualifiedName~PermissionTests'
) -join '|'

dotnet test .\tests\AiCare.Tests\AiCare.Tests.csproj `
    --filter $filter `
    --results-directory $resultsPath `
    --logger 'console;verbosity=normal' `
    --logger 'trx;LogFileName=final-v1.trx'
if ($LASTEXITCODE -ne 0) { throw 'Combined V1 backend regression verification failed.' }

dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw 'Could not restore the repository-local dotnet-ef tool.' }
dotnet ef migrations has-pending-model-changes `
    --project .\src\AiCare.Infrastructure `
    --startup-project .\src\AiCare.Api
if ($LASTEXITCODE -ne 0) { throw 'The EF model does not match the committed migrations.' }

if (-not (Test-Path (Join-Path $frontendPath 'package.json'))) { throw "Frontend package.json was not found at $frontendPath." }
Set-Location $frontendPath
npm test -- `
    src/components/mvp/CareDocumentationGovernance.test.tsx `
    src/components/mvp/VisitTaskChecklist.test.tsx `
    src/components/mvp/IncidentGovernanceWorkspace.test.tsx
if ($LASTEXITCODE -ne 0) { throw 'Focused V1 frontend regressions failed.' }
npm run build
if ($LASTEXITCODE -ne 0) { throw 'The V1 frontend production build failed.' }

Write-Host "Final bounded V1 code verification passed. Backend evidence: $resultsPath\final-v1.trx" -ForegroundColor Green
Write-Host 'Browser, clinical and release-operation gates remain separate.' -ForegroundColor Green
