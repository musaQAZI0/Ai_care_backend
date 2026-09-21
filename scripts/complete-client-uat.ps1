param([string]$Password)
$uatPassword=$Password
$helper=Get-Content "$PSScriptRoot/seed-pilot-data.ps1" -Raw
$ErrorActionPreference='Stop'
$fnStart=$helper.IndexOf('function Invoke-Api')
Invoke-Expression ($helper.Substring($fnStart,$helper.IndexOf('function New-DateOnlyString')-$fnStart))
$ApiBaseUrl='https://ai-care-backend-yeoh.onrender.com';$UseCurl=$true;$Resume=$false
$org='e0f7117e-6e54-42a5-99e9-2fa7bc7467c1'
$login=Invoke-Api -Method Post -Path '/api/auth/login' -Body @{userName='client.uat.admin@aicare.local';password=$uatPassword}
$token=$login.token
$me=Invoke-Api -Method Get -Path '/api/auth/me' -Token $token
if($me.organizationId -ne $org){throw 'Wrong organization'}
$visits=@(Invoke-Api -Method Get -Path '/api/phase1/visits' -Token $token)
$workers=@(Invoke-Api -Method Get -Path '/api/phase1/care-workers' -Token $token)
$mar=@(Invoke-Api -Method Get -Path '/api/phase1/mar' -Token $token)
$users=@(Invoke-Api -Method Get -Path '/api/phase1/admin/users' -Token $token)
$ids=@('33ccecd9-1244-47d4-a880-36017e2257fb','f2daf12a-56e1-464a-be97-3a0e21b258ef','91d16bae-889e-4d9f-913f-736c9b087d28','b2358a69-e4f8-450e-9806-895b7e683ff1','ea889979-6b27-406c-8eff-a0d621243a8e','f325bb54-883f-481f-a083-310eebe5df57','a93c5273-99f6-49de-bb7a-dab941ce58cc','e5b5e277-382a-4bb7-a309-ed813dc1b68c')
foreach($id in $ids){
 $w=$workers|Where-Object id -eq $id
 if(!$w){continue}
 if($w.organizationId -ne $org -or $w.fullName -notmatch '^Pilot Care Worker 0[1-8]$' -or $w.assignedServiceUsers -ne 0){throw 'Unexpected worker state'}
 if(@($visits|Where-Object {$_.careWorkerId -eq $id -or $id -in $_.additionalCareWorkerIds}).Count -or @($mar|Where-Object careWorkerId -eq $id).Count -or @($users|Where-Object careWorkerId -eq $id).Count){throw 'Worker referenced'}
}
foreach($id in $ids){$w=$workers|Where-Object id -eq $id;if($w -and $w.availability -ne 'Archived'){Invoke-Api -Method Delete -Path "/api/phase1/care-workers/$id" -Token $token|Out-Null}}
Write-Host 'Eight duplicate workers processed through archive/delete API.'
$meds=@(Invoke-Api -Method Get -Path '/api/phase1/medications' -Token $token)
if($meds.Count -ne 14 -or $visits.Count -ne 140){throw 'Unexpected dataset sizes'}
foreach($med in $meds){
 if($med.organizationId -ne $org -or $med.pharmacy -ne 'Pilot Community Pharmacy'){throw 'Medication outside synthetic scope'}
 $pv=@($visits|Where-Object serviceUserId -eq $med.serviceUserId|Sort-Object {[datetimeoffset]$_.startsAt}|Select-Object -First 5)
 if($pv.Count -ne 5){throw 'Expected five visits'}
 $path="/api/phase1/medication-safety/medications/$($med.id)/profile"
 $profile=Invoke-Api -Method Get -Path $path -Token $token
 if($profile -and $profile.sourceReference -ne "UAT-SEED-$($med.id)"){throw 'Existing non-seed profile'}
 if(!$profile -or $profile.reconciliationStatus -ne 'Verified'){
 Invoke-Api -Method Put -Path $path -Token $token -Body @{
 indication='SYNTHETIC UAT fixture - not a clinical prescription';prescriber='Fictional UAT prescriber';form='Tablet';strength=$med.dosage
 startDate=([datetimeoffset]$pv[0].startsAt).AddDays(-1).ToUniversalTime().ToString('o');endDate=$null;doseWindowMinutes=60
 maxPrnDoses24h=$null;minPrnIntervalMinutes=$null;prnIndication='';prnEffectReviewMinutes=$null;stockOnHand=28;reorderLevel=7;requiresWitness=$false
 lastReconciledAt=[datetime]::UtcNow.ToString('o');reconciledBy='Synthetic UAT seed';reconciliationStatus='Verified';sourceType='Synthetic test fixture';sourceReference="UAT-SEED-$($med.id)"
 changeReason='User-approved synthetic client testing only; no real prescription or clinical verification'
 }|Out-Null
 }
 foreach($v in $pv){
 $existing=@($mar|Where-Object {$_.medicationId -eq $med.id -and $_.visitId -eq $v.id})
 if($existing.Count -gt 1){throw 'Duplicate MAR'}
 if($existing.Count -eq 1){continue}
 $r=Invoke-Api -Method Post -Path '/api/phase1/mar' -Token $token -Body @{medicationId=$med.id;visitId=$v.id;careWorkerId=$v.careWorkerId;scheduledAt=([datetimeoffset]$v.startsAt).AddMinutes(10).ToUniversalTime().ToString('o');notes='SYNTHETIC UAT scheduled medication prompt - test data only'}
 $mar+=$r
 }
 Write-Host "MAR records prepared: $($mar.Count)/70"
}
$finalWorkers=@(Invoke-Api -Method Get -Path '/api/phase1/care-workers' -Token $token)
$finalMar=@(Invoke-Api -Method Get -Path '/api/phase1/mar' -Token $token)
$remaining=@($finalWorkers|Where-Object {$_.id -in $ids -and $_.availability -ne 'Archived'})
if($remaining.Count -or $finalMar.Count -ne 70){throw 'Final count mismatch'}
if(@($finalMar|Group-Object medicationId,visitId|Where-Object Count -gt 1).Count){throw 'Duplicate MAR pairs'}
if(@($finalMar|Where-Object {$_.organizationId -ne $org -or $_.outcome -ne 'Scheduled'}).Count){throw 'Unexpected MAR scope or outcome'}
[pscustomobject]@{organizationId=$org;nonArchivedWorkers=@($finalWorkers|Where-Object availability -ne 'Archived').Count;archivedDuplicates=@($finalWorkers|Where-Object {$_.id -in $ids -and $_.availability -eq 'Archived'}).Count;visits=$visits.Count;medications=$meds.Count;marRecords=$finalMar.Count;duplicateMarPairs=0}|ConvertTo-Json
