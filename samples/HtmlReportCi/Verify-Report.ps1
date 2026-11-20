param([string] $Dotnet = 'dotnet')

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$project = Join-Path $PSScriptRoot 'HtmlReportCi.csproj'
$output = Join-Path ([IO.Path]::GetTempPath()) ('deep-equal-html-' + [Guid]::NewGuid().ToString('N'))
$report = Join-Path $output 'inventory-diff.html'
$priorMismatch = $env:DEEPEQUAL_DEMO_MISMATCH
$priorDirectory = $env:DEEPEQUAL_REPORT_DIRECTORY
$env:DEEPEQUAL_REPORT_DIRECTORY = $output

try {
    $env:DEEPEQUAL_DEMO_MISMATCH = '0'
    & $Dotnet test $project -c Release --no-restore
    if ($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath $report)) {
        throw 'The matching inventory test must pass without generating a report.'
    }

    $env:DEEPEQUAL_DEMO_MISMATCH = '1'
    $failure = (& $Dotnet test $project -c Release --no-build --no-restore 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 1 -or $failure -notmatch 'DeepComparisonException' -or !(Test-Path -LiteralPath $report)) {
        throw 'The mismatched inventory test must fail with a comparison error and generate a report.'
    }

    $html = Get-Content -LiteralPath $report -Raw
    foreach ($secret in @('SupplierReferences', 'sample-private-key', 'sample-secret-before', 'sample-secret-after')) {
        if ($html.Contains($secret) -or $failure.Contains($secret)) {
            throw 'Private data was present in the report or failing test output.'
        }
    }
    foreach ($expected in @('SKU-9', 'SKU-11', 'SKU-12', '[redacted]', '<td class="before"><code>2</code></td><td class="after"><code>3</code></td>')) {
        if (!$html.Contains($expected)) {
            throw "Expected report content was missing: $expected"
        }
    }
    Write-Output 'Verified the failing assertion and redacted HTML report.'
}
finally {
    $env:DEEPEQUAL_DEMO_MISMATCH = $priorMismatch
    $env:DEEPEQUAL_REPORT_DIRECTORY = $priorDirectory
    if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output }
}
