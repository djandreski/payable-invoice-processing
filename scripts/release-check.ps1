[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publishRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("invoice-review-release-" + [Guid]::NewGuid().ToString('N'))

function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command failed with exit code $LASTEXITCODE."
    }
}

try {
    Push-Location $repositoryRoot
    Invoke-Checked 'npm' @('run', 'restore')
    Invoke-Checked 'npm' @('run', 'build')
    Invoke-Checked 'dotnet' @('format', 'InvoiceReviewAssistant.sln', '--verify-no-changes', '--no-restore')
    Invoke-Checked 'npm' @('run', 'contracts:check')
    Invoke-Checked 'npm' @('run', 'contracts:test')
    Invoke-Checked 'npm' @('test')
    Invoke-Checked 'dotnet' @('publish', 'src/InvoiceReviewAssistant.Api/InvoiceReviewAssistant.Api.csproj', '--configuration', 'Release', '--no-restore', '--output', $publishRoot)
    Invoke-Checked 'pwsh' @('-NoProfile', '-File', './scripts/verify-platform.ps1')
    Invoke-Checked 'pwsh' @('-NoProfile', '-File', './scripts/check-release-hygiene.ps1')
    Write-Host 'PASS all locally executable release checks'
}
finally {
    Pop-Location
    if (Test-Path -LiteralPath $publishRoot) {
        Remove-Item -LiteralPath $publishRoot -Recurse -Force
    }
}
