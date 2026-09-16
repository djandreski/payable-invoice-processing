[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Tesseract', 'OpenAI', 'All')]
    [string]$Provider
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\tests\InvoiceReviewAssistant.IntegrationTests\InvoiceReviewAssistant.IntegrationTests.csproj'

if ($Provider -in @('Tesseract', 'All')) {
    $env:INVOICE_REVIEW_RUN_TESSERACT_SMOKE = '1'
    dotnet test $project --filter 'FullyQualifiedName~InstalledTesseractSmokeTests'
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ($Provider -in @('OpenAI', 'All')) {
    if ([string]::IsNullOrWhiteSpace($env:OPENAI_API_KEY)) {
        throw 'OPENAI_API_KEY must be set for the opt-in OpenAI smoke test.'
    }
    $env:INVOICE_REVIEW_RUN_OPENAI_SMOKE = '1'
    dotnet test $project --filter 'FullyQualifiedName~ConfiguredOpenAiSmokeTests'
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
