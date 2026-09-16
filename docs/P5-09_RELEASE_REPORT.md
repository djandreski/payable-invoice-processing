# P5-09 release report

## Decision

**Blocked for final release.** All locally actionable Windows and deterministic checks must pass before handoff, but the accepted three-platform gate also requires executed macOS and Linux evidence. This workspace has no macOS runner, and its WSL Ubuntu distribution does not contain .NET, Node.js, PowerShell, or Tesseract. Cross-target publishes and a checked-in CI matrix do not substitute for executing the application on those operating systems.

## Release acceptance trace

| PRD section 19 criterion | Evidence owner | Current disposition |
| --- | --- | --- |
| Fresh documented setup and one production-style launch command | P5-05, P5-06, P5-08 | Windows executable evidence; Linux/macOS evidence blocked |
| Text and scanned extraction paths | P5-01, Phase 2 deterministic suites | Covered with synthetic text/scanned fixtures and fake-provider automation; real providers remain opt-in |
| Persisted queue summaries and filters | Phases 3–4 integration/frontend suites | Mandatory deterministic coverage |
| Side-by-side source PDF and editable fields | Phase 4 frontend and P5-03 | Component and browser coverage |
| Corrections retain originals and audit history | Phase 3 and P5-03 | Integration and browser coverage |
| Structured validation and direct/UI approval blocking | Phase 3 and P5-04 | Integration and browser coverage |
| Approval and reasoned rejection | P5-03 and P5-04 | Browser and direct API coverage |
| Terminal records survive restart | P5-04 and P5-08 | Browser persistence on Windows; platform rows still blocked |
| JSON export and source PDF access | P5-03 and P5-04 | Browser/direct API coverage |
| Core, architecture, integration, frontend, and E2E suites | P5-09 | Passed locally: Core 47, Integration 316, E2E 9, frontend 56; architecture assertions are included in Integration |
| No secrets or generated invoice data tracked | P5-07 and P5-09 | Passed scan of 412 tracked/unignored release-candidate files |
| Financial-operations direction and accessibility | P5-02 | Passed component/jsdom axe coverage and production Chromium keyboard/responsive/browser-axe checks at 1280, 768, and 320 px |

## Architecture section 15 trace

| Verification row | Mandatory evidence |
| --- | --- |
| Dependency direction and aggregate rules | Architecture/Core projects in the solution test run |
| Validation, corrections, concurrency, and transactions | Core and integration suites |
| Upload acceptance and storage consistency | Integration suite plus P5-01 fixture catalog |
| Native/OCR and AI provider boundaries | Deterministic integration suites; real-provider smoke reported separately |
| Error mapping and PDF delivery | Integration suite |
| Frontend and contract drift | Vitest/axe checks and `contracts:check`/`contracts:test` |
| End to end | Production-hosted Playwright happy, failure, conflict, decision, and restart scenarios |
| Cross-platform | Windows executed; Linux/macOS mandatory evidence blocked |

## Commands and environment

Run the complete locally executable gate with:

```powershell
pwsh ./scripts/release-check.ps1
```

The script performs canonical restore/build, format verification, contract drift and contract tests, every deterministic .NET/frontend/E2E suite, a clean Release publish, documented production launch/restart smoke, documentation checks, and a release-candidate artifact/credential scan. Its publish and runtime roots are isolated temporary directories and are removed afterward.

The final Windows run passed with a zero-warning build; four contract baseline tests; 47 Core tests; 316 Integration tests; nine Playwright end-to-end tests; 56 frontend tests; Release publish; production PDFium/start/deep-link/SQLite/restart smoke; and the documentation/security/artifact scan. The Integration run separately reported the installed-Tesseract and configured-OpenAI smoke tests as skipped because they are opt-in. Formatting reported no changes, and contract generation reported no drift.

Opt-in real-provider commands are documented in `docs/P5-08_PORTABILITY_AND_PERFORMANCE.md`. Their status is reported separately and cannot turn a failed deterministic or platform gate green.

**Task status: blocked.** Every check executable on this Windows workspace passes, but P5-09 cannot make a release-ready decision while its P5-08 Linux and macOS acceptance evidence is incomplete.
