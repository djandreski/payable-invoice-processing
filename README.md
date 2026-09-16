# Invoice Review Assistant

## Run the local demonstration

Install the .NET SDK version in `global.json`, Node.js 22 LTS or later (with npm), PowerShell 7 or later, and Tesseract 5 with the English (`eng`) language data. PDFium is supplied by the application's managed PDF rendering package; no system PDF viewer is required.

Configure a real OpenAI run without committing a key. From the repository root, use either .NET user secrets:

```powershell
dotnet user-secrets set "OpenAI:ApiKey" "your-key" --project src/InvoiceReviewAssistant.Api
```

or the `OpenAI__ApiKey` environment variable. The default model is `gpt-5.6-terra`; override it with `OpenAI__Model` only when needed. Invoice text normalized from uploaded documents is sent to the configured OpenAI provider only when the real extraction profile is active. The app does not use external analytics.

Start the complete production-style application with one command on Windows, macOS, or Linux:

```powershell
pwsh ./scripts/start-demo.ps1
```

The script publishes the React build and API together, then starts the single loopback-only host at `http://127.0.0.1:5080`. Stop it with `Ctrl+C`. Restart it by running the same command; committed records and source documents remain available. Use `pwsh ./scripts/start-demo.ps1 -Port 5090` to choose another local port. SQLite data and managed PDFs remain beneath the platform's local-application-data `InvoiceReviewAssistant` directory; the app creates `documents`, `staging`, `quarantine`, and temporary OCR storage there. Do not manually alter those directories while the app is running.

For deterministic tests, use the existing test commands; they configure the deterministic extraction provider, so neither an OpenAI credential nor a live provider request or Tesseract installation is required. The production startup path always checks typed options and loopback binding, managed directories, SQLite migrations, PDFium, and storage reconciliation before it listens. Real-provider profiles additionally check Tesseract 5 plus `eng` and real-provider credentials/model.

## Troubleshooting

- `Ocr` startup failures: install Tesseract 5, ensure `tesseract --version` works, and install `eng` data. Set `Ocr__ExecutablePath` only if it is not on `PATH`.
- `PdfRendering` startup failures: restore and republish with the supported platform runtime; do not substitute a browser PDF viewer.
- `SQLite` or `Storage` startup failures: make sure the local application-data location is writable and not locked by another running copy.
- `OpenAI` startup failures: set a non-empty `OpenAI__ApiKey` (or user secret) and `OpenAI__Model` for the real profile. Never paste either value into configuration committed to source control.
- Binding failures: use only `127.0.0.1` or `::1`; network addresses are intentionally rejected.
