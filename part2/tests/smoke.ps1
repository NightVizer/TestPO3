param([string]$BaseUrl = 'http://localhost:5087')

$ErrorActionPreference = 'Stop'
$tempDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('subscriptions-smoke-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $tempDirectory | Out-Null
$checks = 0

function Send-Curl {
    param([string]$Method, [string]$Path, [string]$Body, [string]$ContentType = 'application/json')
    $responsePath = Join-Path $tempDirectory 'response.json'
    $arguments = @('--silent', '--show-error', '--output', $responsePath,
        '--write-out', '%{http_code}', '--request', $Method)
    if ($PSBoundParameters.ContainsKey('Body')) {
        $bodyPath = Join-Path $tempDirectory 'request.json'
        [System.IO.File]::WriteAllText($bodyPath, $Body)
        $arguments += @('--header', "Content-Type: $ContentType", '--data-binary', "@$bodyPath")
    }
    $status = & curl.exe @arguments ($BaseUrl.TrimEnd('/') + $Path)
    if ($LASTEXITCODE -ne 0) { throw "curl failed: $Method $Path" }
    $raw = [System.IO.File]::ReadAllText($responsePath)
    return [pscustomobject]@{
        Status = [int]$status
        Raw = $raw
        Json = if ($raw) { ConvertFrom-Json -InputObject $raw } else { $null }
    }
}

function Check {
    param([bool]$Condition, [string]$Name)
    if (-not $Condition) { throw "FAIL: $Name" }
    $script:checks++
    Write-Host "PASS: $Name"
}

function Check-Error {
    param($Response, [int]$Status, [string]$Name)
    Check ($Response.Status -eq $Status -and
        $Response.Json.error -is [string] -and
        $Response.Json.error.Length -gt 0) $Name
}

try {
    $catalog = Send-Curl GET '/services'
    Check ($catalog.Status -eq 200 -and
        $catalog.Raw.Trim() -eq
        '[{"id":1,"name":"музыка"},{"id":2,"name":"видео"},{"id":3,"name":"облако"}]') 'catalog and order'

    $list = Send-Curl GET '/subscriptions'
    Check ($list.Status -eq 200 -and $list.Raw.Trim() -eq '[]') 'empty subscription list'
    $reminders = Send-Curl GET '/reminders?date=2030-12-30'
    Check ($reminders.Status -eq 200 -and $reminders.Raw.Trim() -eq '[]') 'empty reminder list'

    $dates = @('2030-12-29', '2030-12-30', '2031-01-02', '2031-01-03', '2030-12-30')
    $created = @()
    foreach ($index in 0..4) {
        $serviceId = if ($index -eq 2) { 2 } elseif ($index -eq 4) { 3 } else { 1 }
        $body = @{ serviceId = $serviceId; amountCents = 100 + $index;
            nextPaymentDate = $dates[$index] } | ConvertTo-Json -Compress
        $response = Send-Curl POST '/subscriptions' $body
        Check ($response.Status -eq 201 -and $response.Json.id -gt 0 -and
            $response.Json.serviceId -eq $serviceId -and
            $response.Json.amountCents -eq (100 + $index) -and
            $response.Json.nextPaymentDate -eq $dates[$index]) "create subscription $index"
        $created += $response.Json
    }
    Check ($created[0].serviceId -eq $created[1].serviceId) 'multiple subscriptions per service'

    $list = Send-Curl GET '/subscriptions'
    $actualIds = @($list.Json | ForEach-Object { $_.id })
    $expectedIds = @($created | ForEach-Object { $_.id } | Sort-Object)
    Check ($list.Status -eq 200 -and
        ($actualIds -join ',') -eq ($expectedIds -join ',')) 'subscription list sorted by id'

    $reminders = Send-Curl GET '/reminders?date=2030-12-30'
    $expectedReminderIds = @($created[1].id, $created[4].id, $created[2].id)
    Check ($reminders.Status -eq 200 -and
        (@($reminders.Json | ForEach-Object { $_.subscriptionId }) -join ',') -eq
        ($expectedReminderIds -join ',') -and
        $reminders.Json[2].nextPaymentDate -eq '2031-01-02') 'date window, tie order, year boundary'

    $valid = '{"serviceId":1,"amountCents":200,"nextPaymentDate":"2031-01-01"}'
    $invalidBodies = @(
        @{ Label = 'unknown service'; Body = '{"serviceId":99,"amountCents":100,"nextPaymentDate":"2031-01-01"}' },
        @{ Label = 'zero amount'; Body = '{"serviceId":1,"amountCents":0,"nextPaymentDate":"2031-01-01"}' },
        @{ Label = 'negative amount'; Body = '{"serviceId":1,"amountCents":-1,"nextPaymentDate":"2031-01-01"}' },
        @{ Label = 'invalid calendar date'; Body = '{"serviceId":1,"amountCents":100,"nextPaymentDate":"2031-02-30"}' },
        @{ Label = 'invalid date format'; Body = '{"serviceId":1,"amountCents":100,"nextPaymentDate":"2031-1-1"}' },
        @{ Label = 'missing date'; Body = '{"serviceId":1,"amountCents":100}' },
        @{ Label = 'empty object'; Body = '{}' },
        @{ Label = 'malformed json'; Body = '{bad' }
    )
    foreach ($case in $invalidBodies) {
        Check-Error (Send-Curl POST '/subscriptions' $case.Body) 400 "POST $($case.Label)"
    }
    Check-Error (Send-Curl POST '/subscriptions' '{"amountCents":100,"nextPaymentDate":"2031-01-01"}') 400 'POST missing service'
    Check-Error (Send-Curl POST '/subscriptions' '{"serviceId":1,"amountCents":"wrong","nextPaymentDate":"2031-01-01"}') 400 'POST wrong amount type'
    Check-Error (Send-Curl POST '/subscriptions' $valid 'text/plain') 400 'POST wrong content type'
    Check-Error (Send-Curl POST '/subscriptions') 400 'POST missing body'
    $list = Send-Curl GET '/subscriptions'
    Check ($list.Status -eq 200 -and @($list.Json).Count -eq 5) 'invalid POST requests leave data unchanged'
    Check-Error (Send-Curl PUT "/subscriptions/$($created[0].id)" '{}') 400 'PUT invalid body'
    $invalidBodyAndId = Send-Curl PUT '/subscriptions/abc' '{}'
    Check ($invalidBodyAndId.Status -eq 400 -and
        $invalidBodyAndId.Json.error -eq 'invalid subscription') 'PUT checks body before id'
    Check-Error (Send-Curl PUT '/subscriptions/abc' $valid) 400 'PUT invalid id'
    Check-Error (Send-Curl PUT '/subscriptions/999999' $valid) 404 'PUT unknown id'
    $list = Send-Curl GET '/subscriptions'
    Check (($list.Json | Where-Object id -eq $created[0].id).amountCents -eq 100) 'invalid PUT requests leave data unchanged'

    $updated = Send-Curl PUT "/subscriptions/$($created[0].id)" $valid
    Check ($updated.Status -eq 200 -and $updated.Json.id -eq $created[0].id -and
        $updated.Json.amountCents -eq 200 -and
        $updated.Json.nextPaymentDate -eq '2031-01-01') 'update subscription'
    $reminders = Send-Curl GET '/reminders?date=2030-12-30'
    Check ((@($reminders.Json | ForEach-Object { $_.subscriptionId }) -join ',') -eq
        (@($created[1].id, $created[4].id, $created[0].id, $created[2].id) -join ',')) 'reminders sorted by date after update'

    Check-Error (Send-Curl GET '/reminders') 400 'missing reminder date'
    Check-Error (Send-Curl GET '/reminders?date=2031-02-30') 400 'invalid reminder date'
    Check-Error (Send-Curl GET '/reminders?date=2031-1-1') 400 'invalid reminder date format'
    Check-Error (Send-Curl DELETE '/subscriptions/0') 400 'nonpositive id'
    Check-Error (Send-Curl DELETE '/subscriptions/abc') 400 'nonnumeric id'
    Check-Error (Send-Curl DELETE '/subscriptions/999999') 404 'DELETE unknown id'
    $deleted = Send-Curl DELETE "/subscriptions/$($created[0].id)"
    Check ($deleted.Status -eq 204 -and $deleted.Raw -eq '') 'delete subscription'
    Check-Error (Send-Curl DELETE "/subscriptions/$($created[0].id)") 404 'DELETE already deleted'
    $list = Send-Curl GET '/subscriptions'
    Check (-not (@($list.Json | ForEach-Object { $_.id }) -contains $created[0].id)) 'deleted subscription absent'

    Write-Host "Smoke checks passed: $checks"
}
finally {
    Remove-Item -LiteralPath $tempDirectory -Recurse -Force
}
