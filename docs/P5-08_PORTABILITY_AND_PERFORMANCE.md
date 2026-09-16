# P5-08 portability and performance record

This record separates mandatory deterministic evidence from opt-in checks that contact or depend on real providers. The phase is not portable merely because it compiles for another runtime: a platform row passes only after its publish, startup, PDFium, path, process, SQLite restart, and documented-launch checks execute on that operating system.

## Reproducible commands

```powershell
npm run restore
npm run build
dotnet test tests/InvoiceReviewAssistant.IntegrationTests/InvoiceReviewAssistant.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~LocalPerformanceAcceptanceTests|FullyQualifiedName~First_page_with_one_thousand"
dotnet test tests/InvoiceReviewAssistant.IntegrationTests/InvoiceReviewAssistant.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~OpenAiInvoiceExtractionProviderTests|FullyQualifiedName~TesseractOcrEngineTests|FullyQualifiedName~ExternalProcessRunnerTests"
npm run test --prefix src/invoice-review-client
dotnet test tests/InvoiceReviewAssistant.EndToEndTests/InvoiceReviewAssistant.EndToEndTests.csproj --no-build
pwsh ./scripts/verify-platform.ps1
```

The performance test warms the queue query, uses 1,000 persisted local records, excludes PDF/provider ingestion setup from each measurement, and fails any measured local operation at 500 ms or above. Queue pagination and the PDF panel's current-page plus one-page buffer provide incremental rendering; their component tests are part of the frontend suite. OCR and AI deadline/cancellation checks use deterministic process and HTTP replacements in the mandatory integration suite.

Two consecutive isolated warm runs passed. Run one measured queue 14.8 ms, save 44.1 ms, validation 23.1 ms, approval 19.0 ms, and rejection 27.7 ms. Run two measured queue 8.5 ms, save 19.0 ms, validation 12.0 ms, approval 16.0 ms, and rejection 14.3 ms. The solution gate serializes test projects and the benchmark uses a non-parallel collection so unrelated browser/test setup cannot corrupt the local latency measurement.

## Platform matrix

| Platform | Environment | Mandatory platform result |
| --- | --- | --- |
| Windows x64 | Windows 10.0.26200; .NET 10.0.401 SDK/10.0.12 runtime; Node 24.19.0; npm 11.17.0; PowerShell 7.6.5 | **Passed:** documented launcher, Release publish, PDFium preflight, loopback health, SPA deep link, SQLite migration, and controlled restart. Tesseract is not installed, so the opt-in installed-provider check is omitted. |
| Linux x64 | Ubuntu under WSL2 is present, but .NET, Node, PowerShell, and Tesseract are unavailable in that distribution | **Not run:** mandatory application publish/start evidence cannot execute without its documented prerequisites. |
| macOS | No macOS runner is connected to this workspace | **Not run:** mandatory platform evidence requires a macOS runner. |

The checked-in `.github/workflows/portability.yml` defines the same deterministic suites, installed-Tesseract check, and published restart smoke on Windows, macOS, and Linux. A workflow definition is reproducible coverage, not evidence that those jobs passed.

`dotnet publish` also completed for `linux-x64` and `osx-x64` on the Windows host. Those cross-target builds catch publish-asset mistakes but are intentionally not marked as Linux or macOS execution evidence.

**Task status: blocked.** The Windows implementation and evidence are complete, but the mandatory Linux and macOS execution rows are unavailable in this workspace. The owning missing evidence is the P5-08 three-platform verification record, not an application test failure.

## Opt-in real-provider checks

These checks are excluded from `npm test` and must remain separate from release-deterministic suites:

```powershell
pwsh ./scripts/test-real-providers.ps1 -Provider Tesseract
$env:OPENAI_API_KEY = '<configured outside source control>'
pwsh ./scripts/test-real-providers.ps1 -Provider OpenAI
```

The Tesseract check requires version 5 and `eng`. The OpenAI check sends only one synthetic normalized-text invoice through the accepted Responses API configuration (`store: false`, no tools, strict schema). Never place the credential in a command argument, committed configuration, log, or test result.
