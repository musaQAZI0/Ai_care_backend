$path = Join-Path $PSScriptRoot "../postman/AiCare.postman_collection.json"
$collection = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
foreach ($entry in @(@{key="connectorId";value=""},@{key="syncFailureId";value=""})) {
    if (-not ($collection.variable | Where-Object key -eq $entry.key)) { $collection.variable += [pscustomobject]$entry }
}
$auth = [pscustomobject]@{type="bearer";bearer=@([pscustomobject]@{key="token";value="{{token}}";type="string"})}
function New-Request($name,$method,$segments,$body) {
    $request = [ordered]@{method=$method;header=@([pscustomobject]@{key="Content-Type";value="application/json"});url=[pscustomobject]@{raw="{{baseUrl}}/$($segments -join '/')";host=@("{{baseUrl}}");path=$segments};auth=$auth}
    if ($null -ne $body) {$request.body=[pscustomobject]@{mode="raw";raw=$body;options=[pscustomobject]@{raw=[pscustomobject]@{language="json"}}}}
    [pscustomobject]@{name=$name;request=[pscustomobject]$request;response=@()}
}
$connectorBody = '{"name":"Payroll connector","connectorType":"Payroll","endpointUrl":"https://vendor.example/api","configuration":{"mode":"test"}}'
$importBody = '{"connectorId":"{{connectorId}}","resourceType":"Workers","options":{"scope":"branch"}}'
$exportBody = '{"connectorId":"{{connectorId}}","resourceType":"Payroll","simulateFailure":false}'
$webhookBody = '{"eventType":"worker.updated","externalEventId":"evt-example-1","payload":{"workerId":"example"}}'
$folder=[pscustomobject]@{name="Phase 8 - Integration Hub";item=@(
    (New-Request "Integration dashboard" "GET" @("api","phase1","integrations","dashboard") $null),
    (New-Request "List connectors" "GET" @("api","phase1","integrations","connectors") $null),
    (New-Request "Create connector" "POST" @("api","phase1","integrations","connectors") $connectorBody),
    (New-Request "Run import job" "POST" @("api","phase1","integrations","jobs","import") $importBody),
    (New-Request "Run export job" "POST" @("api","phase1","integrations","jobs","export") $exportBody),
    (New-Request "Receive webhook event" "POST" @("api","phase1","integrations","webhooks","{{connectorId}}") $webhookBody),
    (New-Request "List failed syncs" "GET" @("api","phase1","integrations","failures") $null),
    (New-Request "Retry failed sync" "POST" @("api","phase1","integrations","failures","{{syncFailureId}}","retry") $null)
)}
$collection.item=@($collection.item|Where-Object name -ne "Phase 8 - Integration Hub")+@($folder)
$collection|ConvertTo-Json -Depth 100|Set-Content -LiteralPath $path -Encoding utf8
