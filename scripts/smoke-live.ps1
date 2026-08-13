param(
    [string] $BaseUrl = "https://ai-care-backend-yeoh.onrender.com",
    [string] $UserName = "admin",
    [string] $Password = "Admin123!",
    [string] $DemoKey = "",
    [switch] $SeedDemo,
    [switch] $ResetDemo
)

$ErrorActionPreference = "Stop"
$BaseUrl = $BaseUrl.TrimEnd("/")

function Write-Pass($message) {
    Write-Host "[PASS] $message" -ForegroundColor Green
}

function Invoke-Checked {
    param(
        [string] $Method = "GET",
        [string] $Path,
        [hashtable] $Headers = @{},
        [object] $Body = $null,
        [int[]] $Expected = @(200)
    )

    $uri = "$BaseUrl$Path"
    $params = @{
        Uri = $uri
        Method = $Method
        Headers = $Headers
        UseBasicParsing = $true
    }

    if ($null -ne $Body) {
        $params.ContentType = "application/json"
        $params.Body = ($Body | ConvertTo-Json -Depth 8)
    }

    try {
        $response = Invoke-WebRequest @params
        if ($Expected -notcontains $response.StatusCode) {
            throw "Expected $($Expected -join ',') but got $($response.StatusCode) for $Method $Path"
        }

        Write-Pass "$Method $Path -> $($response.StatusCode)"
        return $response
    }
    catch {
        $status = $_.Exception.Response.StatusCode.value__
        if ($Expected -contains $status) {
            Write-Pass "$Method $Path -> $status"
            return $null
        }

        throw
    }
}

Invoke-Checked -Path "/health" | Out-Null
Invoke-Checked -Path "/health/db" | Out-Null
Invoke-Checked -Path "/health/storage" | Out-Null
Invoke-Checked -Path "/api/phase1/storage/status" -Expected @(401) | Out-Null

$login = Invoke-RestMethod -Uri "$BaseUrl/api/auth/login" -Method Post -ContentType "application/json" -Body (@{
    userName = $UserName
    password = $Password
} | ConvertTo-Json)
$headers = @{ Authorization = "Bearer $($login.token)" }

Invoke-Checked -Path "/api/auth/me" -Headers $headers | Out-Null

if ($ResetDemo) {
    if ([string]::IsNullOrWhiteSpace($DemoKey)) {
        throw "DemoKey is required for -ResetDemo."
    }

    $demoHeaders = $headers.Clone()
    $demoHeaders["X-Demo-Key"] = $DemoKey
    Invoke-Checked -Method "DELETE" -Path "/api/demo/reset" -Headers $demoHeaders | Out-Null
}

if ($SeedDemo) {
    if ([string]::IsNullOrWhiteSpace($DemoKey)) {
        throw "DemoKey is required for -SeedDemo."
    }

    $demoHeaders = $headers.Clone()
    $demoHeaders["X-Demo-Key"] = $DemoKey
    Invoke-Checked -Method "POST" -Path "/api/demo/seed" -Headers $demoHeaders -Expected @(201) | Out-Null
}

$paths = @(
    "/api/phase1/dashboard",
    "/api/phase1/service-users",
    "/api/phase1/care-workers",
    "/api/phase1/visits",
    "/api/phase1/care-plans",
    "/api/phase1/risk-assessments",
    "/api/phase1/family-members",
    "/api/phase1/documents",
    "/api/phase1/care-notes",
    "/api/phase1/incidents",
    "/api/phase1/messages",
    "/api/phase1/admin/users"
)

foreach ($path in $paths) {
    Invoke-Checked -Path $path -Headers $headers | Out-Null
}

Write-Host "Smoke test complete for $BaseUrl" -ForegroundColor Cyan
