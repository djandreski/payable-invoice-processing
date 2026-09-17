[CmdletBinding()]
param(
    [int]$Port = 5080,
    [ValidateSet('127.0.0.1', '::1')]
    [string]$LoopbackAddress = '127.0.0.1',
    [string]$PublishOutput
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repositoryRoot 'src/InvoiceReviewAssistant.Api/InvoiceReviewAssistant.Api.csproj'
$output = if ([string]::IsNullOrWhiteSpace($PublishOutput)) {
    Join-Path $repositoryRoot '.artifacts/demo'
}
else {
    [System.IO.Path]::GetFullPath($PublishOutput)
}

dotnet publish $project --configuration Release --output $output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:Hosting__LoopbackAddress = $LoopbackAddress
$env:Hosting__Port = "$Port"
$baseUri = [UriBuilder]::new('http', $LoopbackAddress, $Port).Uri.AbsoluteUri
Write-Host "Invoice Review Assistant starting at $baseUri" -ForegroundColor Green
Push-Location $output
try {
    & dotnet (Join-Path $output 'InvoiceReviewAssistant.Api.dll')
}
finally {
    Pop-Location
}
