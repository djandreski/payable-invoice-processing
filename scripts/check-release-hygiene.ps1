[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$candidateFiles = @(git -C $repositoryRoot ls-files --cached --others --exclude-standard)
if ($LASTEXITCODE -ne 0) {
    throw 'Could not enumerate repository release candidates.'
}

$forbiddenExtensions = @(
    '.pdf', '.db', '.db-shm', '.db-wal', '.sqlite', '.sqlite3', '.log', '.trx',
    '.coverage', '.coveragexml', '.key', '.pem', '.pfx', '.prompt.json',
    '.response.json', '.provider.json'
)
$forbiddenDirectoryPattern = '(^|/)(\.artifacts|node_modules|bin|obj|dist|storage|staging|quarantine|temp|local-data|playwright-report|test-results)(/|$)'
$artifactFindings = [System.Collections.Generic.List[string]]::new()
$secretFindings = [System.Collections.Generic.List[string]]::new()

foreach ($relativePath in $candidateFiles) {
    $normalized = $relativePath.Replace('\', '/')
    $lower = $normalized.ToLowerInvariant()
    $hasForbiddenExtension = @($forbiddenExtensions | Where-Object { $lower.EndsWith($_, [StringComparison]::Ordinal) }).Count -gt 0
    if ($lower -match $forbiddenDirectoryPattern -or $hasForbiddenExtension) {
        $artifactFindings.Add($normalized)
        continue
    }

    $fullPath = Join-Path $repositoryRoot $relativePath
    try {
        $content = [System.IO.File]::ReadAllText($fullPath)
    }
    catch [System.Text.DecoderFallbackException] {
        continue
    }

    $patterns = @(
        '(?<![A-Za-z0-9])sk-(?!test-|deterministic-|private-|synthetic-)[A-Za-z0-9_-]{16,}',
        '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----',
        'OPENAI_API_KEY\s*=\s*["''][^<"'']{8,}["'']'
    )
    foreach ($pattern in $patterns) {
        if ($content -match $pattern) {
            $secretFindings.Add($normalized)
            break
        }
    }
}

if ($artifactFindings.Count -gt 0) {
    throw "Forbidden generated/data artifacts are release candidates: $($artifactFindings -join ', ')"
}
if ($secretFindings.Count -gt 0) {
    throw "Potential credentials are present in release candidates: $($secretFindings -join ', ')"
}

$requiredDocumentation = @(
    'README.md', 'PRD.md', 'ARCHITECTURE.md', 'AGENTS.md',
    'docs/CONTRACTS.md', 'docs/DECISIONS.md', 'docs/IMPLEMENTATION_PLAN.md',
    'docs/P5-02_ACCESSIBILITY_CHECKS.md', 'docs/P5-08_PORTABILITY_AND_PERFORMANCE.md',
    'docs/P5-09_RELEASE_REPORT.md'
)
foreach ($relativePath in $requiredDocumentation) {
    if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $relativePath) -PathType Leaf)) {
        throw "Required release documentation is missing: $relativePath"
    }
}

$readme = [System.IO.File]::ReadAllText((Join-Path $repositoryRoot 'README.md'))
foreach ($requiredText in 'Windows', 'macOS', 'Linux', 'Tesseract 5', 'OpenAI', 'pwsh ./scripts/start-demo.ps1', 'restart') {
    if ($readme -notmatch [Regex]::Escape($requiredText)) {
        throw "README setup documentation is missing required coverage: $requiredText"
    }
}

Write-Host "PASS release candidates scanned: $($candidateFiles.Count) files"
Write-Host 'PASS no credential, invoice document, database, managed storage, log, provider output, or generated test artifact is a release candidate'
Write-Host 'PASS required setup, accessibility, portability, and release documentation is present'
