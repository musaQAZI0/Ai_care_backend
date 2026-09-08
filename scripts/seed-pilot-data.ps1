param(
    [string]$ApiBaseUrl = "http://localhost:5000",
    [string]$ProviderName = "AiCare Controlled Pilot Provider",
    [string]$PrimaryBranchName = "North Branch",
    [string]$SecondaryBranchName = "South Branch",
    [int]$BranchCount = 2,
    [int]$StaffCount = 8,
    [int]$ServiceUserCount = 20,
    [string]$AdminName = "Pilot Administrator",
    [string]$AdminEmail = "pilot.admin@aicare.local",
    [string]$AdminPassword = "PilotAdmin123!",
    [switch]$ActivateTenant
)

$ErrorActionPreference = "Stop"

function Invoke-Api {
    param(
        [Parameter(Mandatory = $true)][string]$Method,
        [Parameter(Mandatory = $true)][string]$Path,
        [object]$Body = $null,
        [string]$Token = "",
        [hashtable]$Headers = @{}
    )

    $uri = "$($ApiBaseUrl.TrimEnd('/'))$Path"
    $requestHeaders = @{}
    foreach ($key in $Headers.Keys) { $requestHeaders[$key] = $Headers[$key] }
    if ($Token) { $requestHeaders["Authorization"] = "Bearer $Token" }

    $params = @{
        Method = $Method
        Uri = $uri
        Headers = $requestHeaders
        ContentType = "application/json"
    }

    if ($null -ne $Body) {
        $params.Body = ($Body | ConvertTo-Json -Depth 12)
    }

    try {
        Invoke-RestMethod @params
    } catch {
        $response = $_.Exception.Response
        if ($response -and $response.StatusCode) {
            $statusCode = [int]$response.StatusCode
            if ($statusCode -eq 409 -and $Path -eq "/api/auth/signup-tenant") {
                return $null
            }
        }
        throw
    }
}

function New-DateOnlyString([int]$Index) {
    $year = 1938 + ($Index % 45)
    $month = 1 + ($Index % 12)
    $day = 1 + ($Index % 24)
    Get-Date -Year $year -Month $month -Day $day -Format "yyyy-MM-dd"
}

if ($BranchCount -lt 1 -or $BranchCount -gt 2) { throw "BranchCount must be 1 or 2 for the controlled pilot." }
if ($StaffCount -lt 5 -or $StaffCount -gt 10) { throw "StaffCount must be between 5 and 10." }
if ($ServiceUserCount -ne 20) { throw "ServiceUserCount must be 20 for the controlled pilot." }

Write-Host "Creating or reusing pilot provider account..."
$signup = Invoke-Api -Method Post -Path "/api/auth/signup-tenant" -Body @{
    organizationName = $ProviderName
    branchName = $PrimaryBranchName
    region = "North"
    adminName = $AdminName
    adminEmail = $AdminEmail
    adminUserName = $AdminEmail
    password = $AdminPassword
}

if ($null -eq $signup) {
    Write-Host "Provider account already exists. Logging in with supplied admin credentials..."
}

$login = Invoke-Api -Method Post -Path "/api/auth/login" -Body @{
    userName = $AdminEmail
    password = $AdminPassword
    mfaCode = $null
    deviceName = "Pilot seed script"
}

if ($login.mfaEnrollmentRequired) {
    throw "Admin login requires MFA enrollment. Complete MFA first, then rerun this script."
}

$token = $login.token
$me = Invoke-Api -Method Get -Path "/api/auth/me" -Token $token
Write-Host "Using organization $($me.organizationId) as $($me.role)."

$branches = @()
$primaryBranchId = $me.branchId
if ($primaryBranchId) {
    $branches += [pscustomobject]@{ id = $primaryBranchId; name = $PrimaryBranchName }
}

if ($BranchCount -eq 2) {
    $existingBranches = Invoke-Api -Method Get -Path "/api/phase1/organization/branches" -Token $token
    $secondary = @($existingBranches | Where-Object { $_.name -eq $SecondaryBranchName } | Select-Object -First 1)
    if ($secondary.Count -eq 0) {
        $secondary = Invoke-Api -Method Post -Path "/api/phase1/organization/branches" -Token $token -Body @{
            name = $SecondaryBranchName
            region = "South"
            organizationId = $me.organizationId
        }
    } else {
        $secondary = $secondary[0]
    }
    $branches += [pscustomobject]@{ id = $secondary.id; name = $secondary.name }
}

Write-Host "Creating $StaffCount care workers..."
$workers = @()
$specializations = @("Personal care", "Medication support", "Dementia care", "Mobility support", "Nutrition support", "Reablement", "End of life care", "Learning disability support", "Night support", "Complex care")
for ($i = 1; $i -le $StaffCount; $i++) {
    $worker = Invoke-Api -Method Post -Path "/api/phase1/care-workers" -Token $token -Body @{
        fullName = ("Pilot Care Worker {0:00}" -f $i)
        specialization = $specializations[($i - 1) % $specializations.Count]
        availability = "Weekdays 07:00-15:00; alternate weekends"
    }
    $workers += $worker
}

Write-Host "Creating $ServiceUserCount service users with care records..."
$people = @()
$firstNames = @("Aisha", "Bilal", "Carol", "David", "Elaine", "Farah", "George", "Hannah", "Imran", "Julia", "Khalid", "Linda", "Martin", "Nadia", "Owen", "Priya", "Qasim", "Ruth", "Samina", "Thomas")
$lastNames = @("Ahmed", "Brown", "Clark", "Davies", "Evans", "Farooq", "Green", "Hussain", "Iqbal", "Jones", "Khan", "Lewis", "Morgan", "Nadeem", "O'Neill", "Patel", "Qureshi", "Roberts", "Shah", "Taylor")
$careNeeds = @("Morning personal care and breakfast support", "Medication prompts and welfare checks", "Mobility support and meal preparation", "Dementia reassurance visits", "Nutrition monitoring and hydration prompts")

for ($i = 1; $i -le $ServiceUserCount; $i++) {
    $worker = $workers[($i - 1) % $workers.Count]
    $personName = "$($firstNames[$i - 1]) $($lastNames[$i - 1])"
    $person = Invoke-Api -Method Post -Path "/api/phase1/service-users" -Token $token -Body @{
        fullName = $personName
        dateOfBirth = New-DateOnlyString $i
        phoneNumber = ("+44770090{0:000}" -f $i)
        careNeeds = $careNeeds[($i - 1) % $careNeeds.Count]
        emergencyContact = ("Emergency Contact {0:00} +44771100{0:000}" -f $i)
        preferredCareWorker = $worker.fullName
        address = ("{0} Pilot Street, Caretown" -f (10 + $i))
        allergies = $(if ($i % 5 -eq 0) { "Penicillin" } else { "None known" })
        medicalConditions = $(if ($i % 4 -eq 0) { "Diabetes; reduced mobility" } else { "Long-term care support needs" })
        fundingSource = $(if ($i % 3 -eq 0) { "Local authority" } elseif ($i % 3 -eq 1) { "Private" } else { "NHS continuing healthcare" })
        gender = $(if ($i % 2 -eq 0) { "Male" } else { "Female" })
        photoUrl = ""
    }
    $people += $person

    $reviewDue = (Get-Date).AddDays(60 + $i).ToString("o")
    Invoke-Api -Method Post -Path "/api/phase1/care-plans" -Token $token -Body @{
        serviceUserId = $person.id
        personalCare = "Support with washing, dressing, grooming, and daily comfort checks."
        medicationSupport = "Prompt prescribed medication and record exceptions through eMAR."
        mobilityAndTransfers = "Use agreed moving and handling plan; encourage safe independence."
        nutrition = "Prepare light meals, encourage fluids, and record appetite concerns."
        reviewDueAt = $reviewDue
    } | Out-Null

    if ($i -le 10) {
        Invoke-Api -Method Post -Path "/api/phase1/family-members" -Token $token -Body @{
            serviceUserId = $person.id
            fullName = ("Pilot Family Contact {0:00}" -f $i)
            email = ("pilot.family.{0:00}@aicare.local" -f $i)
            relationship = $(if ($i % 2 -eq 0) { "Son" } else { "Daughter" })
            accessLevel = "Portal updates"
        } | Out-Null
    }

    if ($i -le 14) {
        $med = Invoke-Api -Method Post -Path "/api/phase1/medications" -Token $token -Body @{
            serviceUserId = $person.id
            name = $(if ($i % 2 -eq 0) { "Paracetamol" } else { "Ramipril" })
            dosage = $(if ($i % 2 -eq 0) { "500mg" } else { "2.5mg" })
            route = "Oral"
            schedule = $(if ($i % 2 -eq 0) { "08:00, 20:00" } else { "08:00" })
            isPrn = $false
            pharmacy = "Pilot Community Pharmacy"
            allergyWarning = "Check MAR and allergy record before administration"
        }
    }
}

Write-Host "Creating one week of visits and MAR records..."
$visitCount = 0
$marCount = 0
$startDate = (Get-Date).Date.AddDays(1).AddHours(8)
$allMeds = Invoke-Api -Method Get -Path "/api/phase1/medications" -Token $token
for ($day = 0; $day -lt 7; $day++) {
    for ($i = 0; $i -lt $people.Count; $i++) {
        $worker = $workers[$i % $workers.Count]
        $startsAt = $startDate.AddDays($day).Date.AddHours(7).AddMinutes($i * 35)
        $hour = $startsAt.Hour
        $visit = Invoke-Api -Method Post -Path "/api/phase1/visits" -Token $token -Headers @{ "Idempotency-Key" = "pilot-$($people[$i].id)-$day" } -Body @{
            serviceUserId = $people[$i].id
            careWorkerId = $worker.id
            startsAt = $startsAt.ToString("o")
            visitType = $(if ($hour -lt 12) { "Morning care" } elseif ($hour -lt 16) { "Lunchtime support" } else { "Tea visit" })
            durationMinutes = 45
            requiredSkills = $worker.specialization
            additionalCareWorkerIds = @()
            changeReason = "Controlled pilot rota seed"
        }
        $visitCount++

        $personMed = @($allMeds | Where-Object { $_.serviceUserId -eq $people[$i].id } | Select-Object -First 1)
        if ($personMed.Count -gt 0 -and $day -lt 5) {
            Invoke-Api -Method Post -Path "/api/phase1/mar" -Token $token -Body @{
                medicationId = $personMed[0].id
                visitId = $visit.id
                careWorkerId = $worker.id
                scheduledAt = $startsAt.AddMinutes(10).ToString("o")
                notes = "Pilot scheduled medication prompt"
            } | Out-Null
            $marCount++
        }
    }
}

$onboarding = Invoke-Api -Method Get -Path "/api/phase1/tenant/onboarding" -Token $token
if ($ActivateTenant) {
    Write-Host "Activating tenant after seed..."
    Invoke-Api -Method Post -Path "/api/phase1/tenant/activate" -Token $token | Out-Null
    $onboarding = Invoke-Api -Method Get -Path "/api/phase1/tenant/onboarding" -Token $token
}

$result = [pscustomobject]@{
    provider = $ProviderName
    adminEmail = $AdminEmail
    organizationId = $me.organizationId
    branches = $branches.Count
    staff = $workers.Count
    serviceUsers = $people.Count
    carePlans = $onboarding.counts.carePlans
    familyMembers = $onboarding.counts.familyMembers
    visitsCreated = $visitCount
    medications = @($allMeds).Count
    marRecordsCreated = $marCount
    status = $onboarding.organization.status
    canActivate = $onboarding.canActivate
}

$result | ConvertTo-Json -Depth 6
