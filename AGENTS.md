# AGENTS.md

## Purpose and scope

These instructions apply to every implementation task in this repository. A task prompt may be as short as:

- `Implement P2-04 from docs/IMPLEMENTATION_PLAN.md. Follow AGENTS.md.`
- `Implement Phase 2 from docs/IMPLEMENTATION_PLAN.md. Follow AGENTS.md.`

The task or phase named by the user is the authorized scope. Do not implement later tasks, adjacent product ideas, or cleanup unrelated to that scope.

## Required context and authority

Before changing code:

1. Read this file.
2. Read the assigned task card or phase in [`docs/IMPLEMENTATION_PLAN.md`](docs/IMPLEMENTATION_PLAN.md), including its dependencies, exclusive ownership, verification, handoff, phase gate, and execution wave.
3. Read the relevant sections referenced by that task card. Use this authority order when documents overlap:
   1. [`PRD.md`](PRD.md) — product scope, behavior, and release acceptance.
   2. [`docs/DECISIONS.md`](docs/DECISIONS.md) — accepted implementation choices.
   3. [`ARCHITECTURE.md`](ARCHITECTURE.md) — component, domain, persistence, transaction, provider, runtime, and frontend boundaries.
   4. [`docs/CONTRACTS.md`](docs/CONTRACTS.md) — exact HTTP, extraction, audit, error, and export contracts.
   5. [`docs/IMPLEMENTATION_PLAN.md`](docs/IMPLEMENTATION_PLAN.md) — implementation order, ownership, verification, and handoffs.
4. Inspect the current implementation and tests. Do not assume the repository is still in the state described by an earlier task or conversation.
5. Verify that every dependency listed by the assigned task is present. For a phase assignment, verify the preceding phase gate before starting.

If implementation appears to require changing an authoritative behavior, public contract, accepted provider, architecture boundary, or product scope, stop and report the conflict. Do not silently redesign it in code.

## Product and architecture invariants

- This is a single-user, local Invoice Review Assistant for a portfolio-quality MVP.
- The system is a modular monolith: one ASP.NET Core process, one React application, one SQLite database, and application-managed local PDF storage.
- The backend owns validation, lifecycle transitions, approval eligibility, persistence, audit history, and all security-sensitive checks.
- AI proposes extracted values; deterministic rules verify them; the human reviewer makes the final decision.
- The source PDF, final/current values, original extracted values, corrections, validation runs, decisions, and audit events must survive restart.
- Core references only .NET base libraries. It must not reference ASP.NET Core, EF Core, SQLite, filesystem implementations, PDF/OCR libraries, or provider SDKs.
- Infrastructure implements persistence, storage, PDF, OCR, AI, and reconciliation ports. API is the composition and HTTP boundary. DTOs, EF entities, and Core objects remain separate.
- Controllers are thin adapters. Application services coordinate use cases. Domain methods enforce invariants. Avoid MediatR, a second message bus, repository-per-entity abstractions, and generic `Helpers` buckets.
- Synchronous OCR or AI work must never hold a database transaction open.
- `DraftVersion` is the optimistic-concurrency token. Never auto-merge stale financial edits.
- Approved and rejected invoices are terminal and read-only. `processingFailed` has no retry/edit transition in the MVP.
- Money uses `decimal` in backend logic and exact two-decimal strings on the wire. Identifiers remain strings so punctuation and leading zeroes survive.
- Persist event times in UTC. Use injected `TimeProvider` for rules involving the local calendar date.
- The frontend consumes the generated client through feature adapters. It must not duplicate financial rules, currency tolerances, confidence thresholds, lifecycle logic, or approval eligibility.
- TanStack Query owns server state; React Hook Form owns the editable draft and dirty state; route/query parameters own queue search, filters, sort, and page state.

## Accepted implementation choices

Do not replace these choices without an accepted update to `docs/DECISIONS.md`:

- Runtime and backend: .NET 10 with controller-based ASP.NET Core APIs.
- Frontend: React, TypeScript, Vite, React Router, TanStack Query, React Hook Form, Zod, Tailwind CSS, accessible headless primitives, and React-PDF/PDF.js.
- Persistence: EF Core with SQLite plus EF migrations.
- Native PDF handling: PdfPig; PDF rendering: PDFtoImage/PDFium.
- OCR: Tesseract 5 executable, English data, configurable path, and deterministic test replacement.
- AI: OpenAI Responses API, configurable `gpt-5.6-terra` default, low reasoning, strict Structured Outputs, normalized text only, `store: false`, and no tools.
- Default upload limits: 20 MiB and 25 pages. Reject all encrypted/password-protected PDFs.
- Accepted currencies: USD, EUR, GBP, and MKD with configured `0.01` tolerance.
- Low confidence is a warning, not a separate acknowledgement workflow or approval blocker.
- Supported finished environments: Windows, macOS, and Linux without containers or cloud infrastructure.

## Scope exclusions

Do not add authentication, authorization, roles, cloud hosting, containers, distributed storage, hosted databases, background queues, schedulers, batch upload, failed-invoice retry, provider-selection UI, email ingestion, purchase-order matching, supplier master data, ERP/accounting integration, PDF editing, autonomous approval, or other post-MVP capabilities.

## Task execution protocol

### For a single task assignment

1. Work only on the named task ID.
2. Confirm its dependencies in the current workspace.
3. Respect its exclusive ownership area and existing integration seams.
4. Implement the complete deliverables, including ordinary unit/component/integration tests owned by the task.
5. Run the task's verification and the narrowest relevant build/test checks.
6. Leave the workspace in a buildable state and provide the required handoff report.

A single task should remain one primary-agent responsibility. Delegate only if the user explicitly requests further subdivision and the subparts do not share files or state.

### For a complete phase assignment

The primary agent is the phase orchestrator:

1. Verify the preceding phase gate.
2. Follow the phase's dependency graph and the parallel execution waves in `docs/IMPLEMENTATION_PLAN.md`.
3. Use subagents for independent tasks in the same safe wave when available. Assign exactly one task ID to each subagent.
4. Never run dependent tasks or two owners of the same shared hotspot concurrently.
5. Give every subagent the task ID and instruct it to follow this `AGENTS.md`; do not paste or reinterpret the task card.
6. Review and integrate each handoff before releasing dependent work.
7. Run the complete phase gate after all task handoffs are integrated.

If concurrency is limited, reduce the number of simultaneous agents; never weaken dependency or ownership rules to increase parallelism.

## Ownership and collaboration rules

- Treat solution/project/package manifests, lockfiles, the Core aggregate and ports, typed option contracts, `DbContext` and migrations, API DTOs, host composition, shared test harnesses, OpenAPI snapshots, and generated client files as exclusive hotspots.
- Follow the owner order in Implementation Plan section 2.2. Agents outside the active owner task must not edit a hotspot merely to make their feature convenient.
- Feature work should expose cohesive registration or adapter seams. The designated integration owner performs shared host or manifest wiring.
- API features use separate thin controller and service files even when they share the `/api/invoices` prefix.
- Generated OpenAPI and TypeScript client files are never hand-edited. Regenerate them only in their owning tasks.
- Migrations are created or changed only by the task that owns persistence/migrations.
- Existing and uncommitted work belongs to the user or another task. Preserve it, avoid unrelated formatting, and do not discard or overwrite it.
- When another agent is working concurrently, do not modify its owned files. Communicate a required integration change through the orchestrator or handoff.
- Keep changes focused. Do not perform opportunistic refactors unless required to meet the assigned acceptance criteria.

## Backend implementation rules

- Keep all I/O asynchronous and accept `CancellationToken` through application and infrastructure boundaries.
- Make stream ownership explicit and dispose returned streams, PDF resources, rendered pages, and child processes correctly.
- Use typed application results for expected domain failures. Centralized exception handling owns unexpected HTTP failures.
- Enforce request normalization, validation, state checks, and expected-version checks in the order defined by `docs/CONTRACTS.md`.
- Do not trust client-supplied status, validation, confidence, provenance, timestamps, normalized keys, decisions, or audit data.
- Use short atomic persistence boundaries exactly as defined by Architecture section 9. Approval, decision, validation, corrections, and their audits must not partially commit.
- Persist history append-only. No public API may edit or delete audit events, validation runs, corrections, decisions, or documents.
- Keep SQLite-specific conversions and query workarounds in Infrastructure. Financial calculations happen in Core, not SQL.
- Generate storage and temporary names. Normalize and prove containment beneath the configured managed root before filesystem access.
- Invoke Tesseract with structured arguments and no shell. Enforce cancellation, deadlines, process-tree termination, and cleanup.
- Never persist raw AI responses, prompts, reasoning, normalized document text, or token-level content.

## HTTP and frontend rules

- Implement the exact routes, DTOs, required/nullability rules, serialization formats, pagination, ordering, error codes, and response statuses in `docs/CONTRACTS.md`.
- Every JSON error uses the common Problem Details shape and correlation ID. Never return stack traces, exception messages, provider details, credentials, or local paths.
- PDF responses use verified stored files, `application/pdf`, safe inline disposition, `nosniff`, and range processing.
- Preserve unsaved frontend form values on `409`. Refetch and let the reviewer reconcile; never silently reset, merge, or retry.
- Keep confidence and provenance visually and semantically separate from deterministic validation.
- Status and validation must not rely on color alone. All primary controls, dialogs, summaries, and focus transitions must remain keyboard-accessible.
- Keep PDF page, zoom, and panel state stable when invoice data refetches.
- Bundle PDF.js worker, CMaps, and fonts locally. Do not add runtime CDN dependencies.

## Security, privacy, and repository hygiene

- Treat uploads, filenames, request data, provider responses, and browser state as untrusted.
- Never log or commit raw invoice text, full extracted records, request bodies, PDF bytes, prompts, provider responses, credentials, API keys, user-supplied filenames, or sensitive absolute paths.
- Load secrets only from user secrets or environment-specific configuration. Do not place secrets in committed JSON, source, fixtures, snapshots, or test output.
- Use sanitized synthetic invoice fixtures only. Never commit real supplier, financial, or personal information.
- Keep databases, managed storage roots, temporary render/OCR files, logs, generated local data, and test artifacts out of source control.
- Development CORS allows one configured origin. Production serves one origin and binds to loopback.
- Do not execute uploaded content or interpolate user-controlled data into shell commands.

## Testing and verification

- Every task adds tests for its own behavior; do not defer basic coverage to a later hardening task.
- Use deterministic fake OCR and AI adapters in all default unit, integration, frontend, and end-to-end suites.
- Real Tesseract and OpenAI checks are opt-in smoke tests and must not be required for the default test run.
- Prefer isolated temporary database and storage roots. Tests must never read or delete outside their generated root.
- Use injected time and deterministic provider outputs instead of wall-clock sleeps or live services.
- Run focused tests while developing, then the task card's complete verification before handoff.
- A phase orchestrator runs the full phase gate. P5-09 runs every mandatory suite and the contract-drift, publish, security, and artifact checks.
- Do not declare a task complete when required checks fail. Report the failure and owning blocker precisely.

When the repository exposes canonical restore, build, formatting, test, generation, publish, or startup commands, use those commands rather than inventing alternatives. P1-01 establishes the initial commands; later owning tasks may extend them without creating competing entry points.

## Required handoff format

End every implementation task with a concise report containing:

1. **Task:** assigned ID and title.
2. **Status:** complete or blocked.
3. **Implemented:** behavior and important files changed.
4. **Verification:** exact checks run and their results.
5. **Handoff:** confirmation that the task card's handoff condition is satisfied.
6. **Blockers or follow-ups:** only unresolved items within scope, including any requested hotspot change for the next owner.

For a phase assignment, also list every task ID and its status, the phase-gate results, and any opt-in checks that were intentionally not run. Do not claim the phase is complete until every task and the phase gate pass.
