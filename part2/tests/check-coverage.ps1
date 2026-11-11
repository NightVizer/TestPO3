param([string]$ResultsDirectory = "$PSScriptRoot/SubscriptionReminders.Tests/TestResults")

$ErrorActionPreference = 'Stop'
$coverageFile = Get-ChildItem -LiteralPath $ResultsDirectory -Filter coverage.opencover.xml -Recurse |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($null -eq $coverageFile) { throw 'OpenCover result not found. Run dotnet test with coverage.runsettings first.' }

[xml]$coverage = Get-Content -LiteralPath $coverageFile.FullName -Raw -Encoding UTF8
$modules = @($coverage.CoverageSession.Modules.Module)
if ($modules.Count -ne 1 -or $modules[0].ModuleName -ne 'SubscriptionReminders.Business') {
    throw 'Coverage result must contain only the Business assembly.'
}
$summary = $coverage.CoverageSession.Summary

$sequenceTotal = [int]$summary.GetAttribute('numSequencePoints')
$sequenceVisited = [int]$summary.GetAttribute('visitedSequencePoints')
$branchTotal = [int]$summary.GetAttribute('numBranchPoints')
$branchVisited = [int]$summary.GetAttribute('visitedBranchPoints')
if ($sequenceTotal -eq 0 -or $branchTotal -eq 0) { throw 'Coverage result has no measured code.' }

$sequencePercent = 100 * $sequenceVisited / $sequenceTotal
$branchPercent = 100 * $branchVisited / $branchTotal
Write-Host ('Business sequence points: {0}/{1} ({2:N1}%)' -f $sequenceVisited, $sequenceTotal, $sequencePercent)
Write-Host ('Business branches: {0}/{1} ({2:N1}%)' -f $branchVisited, $branchTotal, $branchPercent)
if ($sequencePercent -lt 90 -or $branchPercent -lt 80) {
    throw 'Business coverage is below the required threshold.'
}
