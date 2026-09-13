# Invoice Review Assistant: Implementation Plan

| Document attribute | Value |
| --- | --- |
| Product | Invoice Review Assistant |
| Release | MVP / portfolio demonstration |
| Status | Ready for implementation |
| Task model | Focused, independently reviewable agent assignments |
| Last updated | September 13, 2026 |

## 1. Purpose and authority

This plan converts the five delivery phases in [`PRD.md`](../PRD.md) into dependency-ordered tasks that can be assigned to implementation agents without requiring them to redesign the product, architecture, or public contracts.

The source documents retain this precedence:

1. [`PRD.md`](../PRD.md) controls product scope, behavior, and release acceptance.
2. [`DECISIONS.md`](DECISIONS.md) controls accepted implementation choices.
3. [`ARCHITECTURE.md`](../ARCHITECTURE.md) controls component, domain, persistence, transaction, provider, runtime, and frontend boundaries.
4. [`CONTRACTS.md`](CONTRACTS.md) controls exact HTTP, extraction, audit, error, and export wire contracts.
5. This document controls implementation order, task ownership, handoffs, and phase gates within those boundaries.

An implementation agent must stop and raise a blocker when its task appears to require changing an authoritative document. It must not hide a product, architecture, provider, or contract change inside code.

## 2. How to execute this plan

### 2.1 Task assignment contract

- Assign one task ID to one primary agent.
- Begin a task only after every listed dependency is merged into the agent's starting point.
- Keep the change confined to the task's exclusive ownership area and named integration seams.
- Deliver production code, ordinary task-level tests, and any task-specific fixtures together.
- Run the task's verification before handoff and report commands, results, and any remaining opt-in checks.
- Rebase or refresh from completed dependencies before handoff; never recreate or fork accepted DTOs, domain types, configuration defaults, schemas, or generated files.
- A task is complete only when its handoff condition is true. Partial implementations and production stubs do not satisfy a handoff.

Later hardening tasks supplement task-level tests with races, fault injection, cross-feature coverage, and release verification. They do not defer basic correctness testing from the feature task that owns the behavior.

### 2.2 Shared-hotspot ownership

The following areas have one owner at a time. Other agents consume them through existing seams and leave requested changes in handoff notes for the owning task.

| Shared hotspot | Exclusive owners, in order |
| --- | --- |
| Solution, project, package, lock, and root build manifests | P1-01; later dependency changes are batched by the active phase integrator |
| Core aggregate and provider-neutral ports | P1-02 |
| Typed option contracts and defaults | P1-03 |
| `DbContext`, EF mappings, shared repository implementation, and migrations | P1-05 |
| Public API DTOs, serialization rules, and Problem Details types | P1-06 |
| Host composition and startup pipeline | P1-07, then P2-09, P5-05, and P5-06 in dependency order |
| OpenAPI snapshot, NSwag configuration, and generated TypeScript client | P1-10, then P3-08 |
| Shared backend integration harness | P1-08; feature tasks may add isolated fixtures without restructuring it |
| Frontend application shell, tool configuration, and global design tokens | P1-09 |
| End-to-end harness and shared acceptance fixtures | P5-01, P5-03, and P5-04 |

API feature tasks use separate thin controller files and feature services even when routes share `/api/invoices`. They do not accumulate concurrent edits in one monolithic controller. Feature registration should be exposed through feature-level extension methods so the designated host-composition owner can make the final shared wiring change.

### 2.3 Scope guardrails

No task may introduce authentication, authorization, cloud hosting, containers, distributed storage, background queues, batch upload, retry UI for failed invoices, provider-selection UI, email ingestion, ERP integration, PDF editing, or another post-MVP feature. The application remains a single-user, loopback-bound local web application.

## 3. Phase 1 — Foundation

### P1-01 — Repository scaffold

- **Goal:** Create a restorable, buildable solution and frontend skeleton with all expected production and test projects.
- **Dependencies:** None.
- **Exclusive ownership:** Root solution/build manifests, project files, frontend package manifests and lockfile, repository ignore rules, and empty project skeletons.
- **Deliverables:** Create the solution and project layout from Architecture section 4; target .NET 10; scaffold the Vite React/TypeScript client and backend, core, infrastructure, unit, integration, frontend-test, and end-to-end projects; declare the accepted baseline libraries; add safe ignores for secrets, databases, PDFs, storage roots, generated local data, build output, and provider artifacts; expose baseline restore, build, and test commands.
- **Source references:** PRD sections 15, 16, and 18; Architecture sections 4 and 13; Decisions DEC-001 through DEC-003.
- **Verification:** A clean dependency restore succeeds; backend and frontend compile; empty test projects execute; no source project violates the intended reference direction.
- **Handoff:** Every later task can add code inside an existing project without independently inventing project layout, target frameworks, test runners, or root commands.

### P1-02 — Core domain and ports

- **Goal:** Establish the provider-neutral domain vocabulary and aggregate boundaries used by every backend feature.
- **Dependencies:** P1-01.
- **Exclusive ownership:** Core domain types, the `Invoice` aggregate, lifecycle methods, immutable snapshots, application result types, and persistence/document/PDF/OCR/extraction ports.
- **Deliverables:** Implement typed invoice values; closed status, field, provenance, validation, processing, audit, integrity, and decision enums; draft and validation versions; field metadata and canonical correction values; audit, decision, and processing-failure records; lifecycle invariants; purpose-built repository/unit-of-work ports; asynchronous provider-neutral I/O interfaces with explicit stream ownership and cancellation.
- **Source references:** PRD sections 8, 9, 11.5, 11.6, and 12; Architecture sections 5 and 7; Contracts sections 2 through 6 and 12.
- **Verification:** Core references only the .NET base libraries; unit tests cover valid and invalid lifecycle transitions, terminal immutability, zero versus missing money, no-op semantics, and canonical comparison behavior.
- **Handoff:** Persistence, validation, ingestion, and API agents share one accepted domain model and port set with no framework or provider types in Core.

### P1-03 — Typed configuration

- **Goal:** Define every backend-owned policy once with accepted defaults and actionable validation.
- **Dependencies:** P1-01.
- **Exclusive ownership:** Typed options, default values, option validators, and safe example configuration keys.
- **Deliverables:** Add Storage, Upload, NativeText, PdfRendering, Ocr, Extraction, OpenAI, Currencies, Confidence, and Cors option sections; encode all Architecture section 13 defaults; validate ranges, path prerequisites, loopback/CORS shapes, currency tolerances, timeout relationships, provider/model presence by startup profile, and secret absence from committed configuration.
- **Source references:** PRD sections 8.2, 8.3, 15, and 16.3; Architecture section 13; Decisions DEC-001 through DEC-007.
- **Verification:** Unit tests cover accepted defaults and representative invalid combinations; test and contract-generation profiles can start with deterministic providers without a real credential; the real-provider profile rejects a missing API key without printing it.
- **Handoff:** All later subsystems bind their behavior to these options rather than duplicating numeric thresholds, currency lists, provider settings, or frontend policy.

### P1-04 — Validation kernel

- **Goal:** Implement deterministic financial and review validation before ingestion orchestration needs its initial validation run.
- **Dependencies:** P1-02.
- **Exclusive ownership:** Core validation service, rule implementations, rule result ordering, duplicate-key normalization, payment-term derivation used by validation, and corresponding Core tests.
- **Deliverables:** Implement all nine PRD rules with exact severities; accept currency policy, duplicate candidates, immutable invoice snapshot, and `TimeProvider`; exclude self and processing-failed invoices from duplicates; preserve stable presentation ordering while keeping rule execution order-independent; distinguish warnings from approval-blocking errors.
- **Source references:** PRD section 12 and 18.1; Architecture sections 5.4 and 15; Contracts sections 3.2, 5, and 14.3; Decision DEC-006 and DEC-007.
- **Verification:** Tests cover tolerance boundaries, zero and missing amounts, dates, payment terms, every supported and unsupported currency case, duplicate normalization and all matches, confidence behavior, injected local date, self-exclusion, and shuffled rule execution.
- **Handoff:** Ingestion, explicit validation, and approval can call the same pure validator without OCR, AI, HTTP, database, or filesystem dependencies.

### P1-05 — SQLite persistence

- **Goal:** Persist current invoice state relationally and history append-only with SQLite-safe types and query indexes.
- **Dependencies:** P1-02 and P1-03.
- **Exclusive ownership:** `InvoiceDbContext`, EF entities and mappings, converters, repository/unit-of-work implementations, SQLite connection configuration, and the initial migration.
- **Deliverables:** Implement the Architecture section 6 schema and relationships; store decimals as canonical invariant text, dates as `yyyy-MM-dd`, UTC timestamps safely, and heterogeneous history as versioned canonical JSON; configure `DraftVersion` as the concurrency token; add all required indexes; enable foreign keys, WAL, and the busy timeout; keep EF entities inside Infrastructure.
- **Source references:** PRD sections 15.2 and 16.1; Architecture section 6 and section 9.1; Contracts sections 2.2, 10, and 12.
- **Verification:** Migration applies to an empty temporary database; round-trip tests prove money/date/timestamp fidelity, leading-zero identifier preservation, immutable history relationships, duplicate lookup, concurrency predicates, and deterministic ordering.
- **Handoff:** Feature services can load/save aggregates and purpose-built projections without direct controller access to `DbContext` or repository-per-entity abstractions.

### P1-06 — HTTP contract boundary

- **Goal:** Establish the exact HTTP v1 representation and one consistent safe error surface.
- **Dependencies:** P1-02 and P1-03.
- **Exclusive ownership:** Public request/response DTOs, DTO/domain mapping primitives, input normalization, JSON configuration, Problem Details types/catalog, correlation middleware, and shared endpoint response metadata.
- **Deliverables:** Encode all required/nullable members, string enums, lowercase UUIDs, sequence IDs, dates, timestamps, money strings, explicit nulls, empty collections, case-sensitive properties, and unknown-member rejection; implement the complete stable error catalog and fields/currentVersion behavior; keep DTOs separate from domain and EF types.
- **Source references:** PRD section 13; Architecture section 11; Contracts sections 2 through 13.
- **Verification:** Serialization tests cover every enum and primitive rule, complete request shapes, normalization, malformed inputs, representative Problem Details, correlation echo, sensitive-detail exclusion, and OpenAPI required/nullable metadata.
- **Handoff:** Endpoint tasks reuse these DTOs and error results verbatim rather than creating local variants or changing wire behavior.

### P1-07 — Baseline application host

- **Goal:** Provide one composition root and predictable startup modes for development, tests, contract generation, and later production hosting.
- **Dependencies:** P1-03, P1-04, P1-05, and P1-06.
- **Exclusive ownership:** API host composition, startup pipeline, service registration order, baseline logging, migration initialization, development CORS, exception handling, and OpenAPI endpoint discovery.
- **Deliverables:** Wire Core and Infrastructure through DI; validate options; apply migrations before accepting requests; configure controllers, strict JSON, Problem Details, correlation/log scopes, exact-origin development CORS, development OpenAPI, and a contract-generation mode that skips provider preflight, storage reconciliation, and credentials.
- **Source references:** PRD sections 13.2, 15.3, 15.4, and 16.3; Architecture sections 11, 13, and 14.
- **Verification:** Development and test hosts start with deterministic settings; invalid options fail clearly; migrations precede requests; CORS rejects unconfigured origins; unexpected exceptions return safe correlated errors.
- **Handoff:** Feature tasks register cohesive modules without independently restructuring the shared startup pipeline.

### P1-08 — Backend verification foundation

- **Goal:** Make architecture, HTTP, persistence, and failure behavior cheap to test consistently.
- **Dependencies:** P1-05, P1-06, and P1-07.
- **Exclusive ownership:** Architecture-test rules, shared integration application factory, temporary database/storage lifecycle, deterministic clocks and providers, and reusable fault-injection seams.
- **Deliverables:** Add dependency-direction tests; construct isolated per-test SQLite and managed-storage roots; replace OCR and AI through DI; provide deterministic time, ID, document, and failure controls; capture safe logs and HTTP responses without using real providers.
- **Source references:** PRD section 18; Architecture sections 4.1 and 15.
- **Verification:** Parallel test instances do not share files or databases; injected failures are deterministic; Core dependency violations fail tests; cleanup remains within the generated test root.
- **Handoff:** Backend feature agents can add focused integration cases without rebuilding host, storage, provider, or clock infrastructure.

### P1-09 — Frontend foundation

- **Goal:** Establish the client architecture and visual foundation without embedding invoice business rules.
- **Dependencies:** P1-01.
- **Exclusive ownership:** React application shell, global styles and tokens, frontend tooling, route skeleton, shared accessible primitives, test setup, and top-level loading/error boundaries.
- **Deliverables:** Configure React Router, TanStack Query, React Hook Form, Zod, Tailwind, Radix primitives, Vitest, Testing Library, and the PDF.js-compatible build; create `/` and `/invoices/:invoiceId` route shells; implement navy/ink, warm-white/slate, blue, emerald, amber, and red tokens with tabular numeric styles.
- **Source references:** PRD sections 14, 16.1, and 18.3; Architecture section 12.
- **Verification:** Production and test builds succeed; route shells render; keyboard-accessible shared controls and error boundaries have component tests; no handwritten server DTOs or financial rules are introduced.
- **Handoff:** Feature UI agents build within stable routing, state-ownership, styling, and testing conventions.

### P1-10 — Contract generation workflow

- **Goal:** Make server/client contract drift visible and reproducible.
- **Dependencies:** P1-06, P1-07, and P1-09.
- **Exclusive ownership:** Generated OpenAPI artifact, NSwag Fetch configuration, `src/api/generated`, and contract generation/check commands.
- **Deliverables:** Generate OpenAPI without migrations, reconciliation, OCR preflight, or credentials; configure NSwag's Fetch template; commit generated output; add a check that regenerates into a controlled location and fails on differences; prohibit manual edits and presentation imports of backend/domain types.
- **Source references:** PRD sections 13.2 and 16.1; Architecture section 12.3; Contracts sections 1 and 15.
- **Verification:** Two clean generations are byte-stable; the check detects a deliberate schema difference; generated code compiles with the frontend.
- **Handoff:** P3-08 can regenerate the completed route set through the same deterministic workflow.

### Phase 1 gate

All projects restore, build, and test; the initial migration applies to an empty database; architecture tests pass; development/test hosts start; and OpenAPI plus TypeScript client generation is deterministic.

## 4. Phase 2 — Ingestion and extraction

### P2-01 — Extraction schema and mapping

- **Goal:** Turn strict provider output into safe provider-neutral proposals without partial or fabricated values.
- **Dependencies:** P1-02, P1-03, and P1-08.
- **Exclusive ownership:** Checked-in `invoice_extraction_v1` JSON Schema, schema loader, local validation, semantic mapper, and deterministic extraction provider.
- **Deliverables:** Check in the canonical schema exactly as specified; validate with date formats enabled; enforce null/confidence, nonblank text, exact date/money, currency-candidate, and schema-version semantics; derive normalized payment-term days; map confidence and `aiInference` provenance while keeping document text source separate.
- **Source references:** Architecture sections 8.4 and 8.5; Contracts section 14; Decision DEC-003.
- **Verification:** Contract tests cover valid complete responses, every semantic failure, unknown properties, malformed values, null/confidence combinations, no partial mapping, and deterministic test proposals.
- **Handoff:** Real and deterministic providers return the same provider-neutral proposal type consumed by ingestion.

### P2-02 — Local document store

- **Goal:** Store source PDFs and temporary artifacts under generated, contained, recoverable local keys.
- **Dependencies:** P1-02, P1-03, and P1-08.
- **Exclusive ownership:** `IDocumentStore` implementation, storage-key/path types, staging/final/quarantine directory operations, and storage adapter tests.
- **Deliverables:** Stage streams while counting and hashing; flush and close before commit; atomically rename on the same filesystem; open/read and delete by generated key; normalize and verify every resolved path; implement bounded compensation and explicit stream ownership.
- **Source references:** PRD sections 11.1 and 15.2 through 15.4; Architecture sections 7 and 10; Decision DEC-001.
- **Verification:** Tests cover traversal-style display names, incomplete streams, cancellation, rename/delete failures, same-root containment, hash/length accuracy, disposal, and compensation failure without touching paths outside the generated test root.
- **Handoff:** PDF and orchestration tasks consume storage descriptors and keys without knowing local absolute paths.

### P2-03 — PDF acceptance inspection

- **Goal:** Reject unsafe or unsupported files before an invoice record is created or provider work begins.
- **Dependencies:** P2-02.
- **Exclusive ownership:** PDF upload acceptance service, PdfPig inspection adapter, check ordering, safe typed upload failures, and pre-acceptance fixtures.
- **Deliverables:** Enforce exactly one nonempty `file`; file bytes at exactly 20 MiB by default; `.pdf` display extension; `application/pdf`; PDF signature; structural validity; uniform encrypted-document rejection; maximum 25 pages; staged-file cleanup on every rejection; hashing and byte count during staging rather than a second upload read.
- **Source references:** PRD section 11.1; Architecture sections 8.1 and 8.2; Contracts sections 9.1 and 11.2; Decisions DEC-004 and DEC-005.
- **Verification:** Tests cover empty, wrong extension/type/signature, malformed, encrypted, exactly 20 MiB, one byte over, exactly 25 pages, 26 pages, multiple files, cleanup, and absence of invoice/audit rows after rejection.
- **Handoff:** Accepted descriptors contain trustworthy generated storage data, hash, byte length, and page count for durable acceptance.

### P2-04 — Native PDF text path

- **Goal:** Extract and classify usable native text deterministically before deciding whether OCR is necessary.
- **Dependencies:** P2-03.
- **Exclusive ownership:** Native PdfPig text extraction, document text normalization, usability calculation, and boundary fixtures.
- **Deliverables:** Read pages in document order; normalize line endings, Unicode whitespace, and repeated blank lines without changing content characters; apply the three configurable usability thresholds; return either a complete normalized native document or a whole-document OCR decision.
- **Source references:** PRD sections 11.2 and 15.1; Architecture section 8.3.
- **Verification:** Fixtures cover every threshold immediately below, at, and above its boundary, mixed-page coverage, cancellation, malformed extraction failures, and proof that usable native documents never invoke OCR.
- **Handoff:** Ingestion receives one normalized text source classification and never merges native and OCR text.

### P2-05 — Rendering and OCR path

- **Goal:** Convert scanned PDFs into normalized English text safely on Windows, macOS, and Linux.
- **Dependencies:** P1-03 and P2-03.
- **Exclusive ownership:** PDFtoImage adapter, PDFium serialization, Tesseract process adapter, OCR temporary artifacts, timeouts, cancellation, and OCR preflight service.
- **Deliverables:** Render one page at a time at 300 DPI; serialize PDFium; invoke Tesseract through an argument list with no shell; use `eng` and automatic page segmentation; enforce 30-second page and five-minute document defaults; terminate process trees and remove page files on timeout/cancellation; keep stdout/stderr out of normal logs.
- **Source references:** PRD sections 11.2 and 15; Architecture sections 8.3, 13, and 14; Decisions DEC-001 and DEC-002.
- **Verification:** Deterministic adapter tests cover sequential pages, renderer serialization, unavailable executable/language data, page and document deadlines, cancellation, process cleanup, disposal, output normalization, and safe diagnostics.
- **Handoff:** Ingestion can request whole-document OCR through provider-neutral interfaces and receive either normalized text or a classified safe failure.

### P2-06 — OpenAI extraction adapter

- **Goal:** Implement the accepted real extraction provider without exposing document or provider internals.
- **Dependencies:** P1-03 and P2-01.
- **Exclusive ownership:** OpenAI Responses client adapter, provider request construction, provider deadlines/retry policy, and provider-specific contract tests.
- **Deliverables:** Send normalized text only to configurable `gpt-5.6-terra`; request low reasoning and strict Structured Outputs; set `store: false`; enable no tools; use one supported transient retry, 60-second network timeout, and 90-second overall deadline; classify refusal, incomplete, timeout, unavailable, invalid, and cancellation outcomes.
- **Source references:** Architecture section 8.4; Contracts section 14; Decision DEC-003.
- **Verification:** Tests inspect outbound options and cover success, refusal, incomplete response, timeout, transient retry exhaustion, malformed output, schema failure, cancellation, safe logging, and absence of PDF bytes, credentials, raw text, prompt, or provider response in persistence/logs.
- **Handoff:** The adapter satisfies the same interface as P2-01's deterministic provider and returns only validated proposals or classified failures.

### P2-07 — Ingestion coordinator

- **Goal:** Coordinate durable acceptance, extraction, initial validation, and safe completion without holding transactions across provider work.
- **Dependencies:** P1-04, P1-05, P2-01, P2-02, P2-03, P2-04, P2-05, and P2-06.
- **Exclusive ownership:** Upload application service, ingestion transaction orchestration, processing-stage failure mapping, and ingestion audit construction.
- **Deliverables:** Implement stage/inspect; short rename plus database acceptance transaction; transaction-free native/OCR/AI work; one atomic success save containing fields, metadata, initial validation, status, and audits; one atomic cleanup save for controlled failure; independent bounded completion token after caller disconnect; correct `Processing`, `ReviewRequired`, and `ProcessingFailed` transitions.
- **Source references:** PRD section 10.1; Architecture sections 8.1, 8.5, 9, and 10.2; Contracts sections 3.3, 9.1, and 12.
- **Verification:** Unit/service tests prove no transaction spans OCR or AI; success always enters `reviewRequired`; failures persist safe stage/code/message only; no partial proposal survives; accepted disconnected requests reach a terminal processing outcome.
- **Handoff:** HTTP upload can return the persisted detail representation for both successful and controlled failed processing.

### P2-08 — Startup reconciliation

- **Goal:** Repair or classify filesystem/database drift idempotently before requests are accepted.
- **Dependencies:** P1-05 and P2-02.
- **Exclusive ownership:** Startup storage reconciler, quarantine policy, integrity checks/transitions, and interrupted-processing recovery.
- **Deliverables:** Delete staging files older than 24 hours; quarantine final files without rows; verify referenced existence and byte length; append one integrity event per actual transition; preserve approved/rejected lifecycle states when integrity changes; convert leftover `processing` records to `processingFailed/PROCESS_INTERRUPTED`; make repeated runs no-ops.
- **Source references:** PRD sections 15.2 and 19; Architecture section 10.3; Contracts sections 3.1, 3.3, and 12.2.
- **Verification:** Tests cover every drift case, compensation leftovers, missing/corrupt documents, terminal records, repeated execution, deterministic audit creation, and confinement to managed directories.
- **Handoff:** P2-09 can register reconciliation before listening, and document retrieval can reuse integrity state handling.

### P2-09 — Upload and document endpoints

- **Goal:** Expose secure upload and PDF retrieval using the accepted HTTP behavior.
- **Dependencies:** P1-06, P1-07, P2-07, and P2-08.
- **Exclusive ownership:** Upload and document controller files, their response mapping, document-integrity-on-read behavior, and ingestion feature registration in the host.
- **Deliverables:** Implement `POST /api/invoices` with exactly one `file`; return `201`, `Location`, and `InvoiceDetailDto` for both reviewable and controlled failed records; implement `GET /api/invoices/{id}/document` with verified SHA-256, safe inline disposition, `application/pdf`, `nosniff`, range processing, and correct 404/416/500 distinctions.
- **Source references:** PRD sections 11.1 and 13; Architecture sections 8.5 and 11; Contracts sections 9.1 and 9.2.
- **Verification:** Integration tests assert status, headers, ranges, response shapes, safe filenames, integrity transitions, exact Problem Details, no local path disclosure, and no resource creation for pre-acceptance failures.
- **Handoff:** Backend consumers and the future PDF.js viewer have stable creation and document URLs.

### P2-10 — Ingestion hardening tests

- **Goal:** Complete the ingestion, storage, provider, and recovery verification matrix beyond ordinary adapter tests.
- **Dependencies:** P1-08 and P2-09.
- **Exclusive ownership:** Cross-component ingestion fixtures, failure matrix, and assertions over storage/database/log side effects.
- **Deliverables:** Add failure injection before rename, after rename, during commit, during compensation, and during reconciliation; exercise native/OCR thresholds and cleanup; cover every stable processing-failure code and upload Problem Details code; verify retry, cancellation, idempotence, no partial persistence, and sensitive-data exclusion.
- **Source references:** PRD sections 18.2 and 19; Architecture section 15; Contracts section 15.
- **Verification:** The complete ingestion matrix passes repeatedly and in random order using deterministic providers; generated data remains inside test roots; normal logs contain no invoice content, filenames, provider output, credentials, or local paths.
- **Handoff:** Phase 2 behavior is safe for draft/workflow development and representative acceptance fixtures.

### Phase 2 gate

Deterministic text and scanned PDFs reach `reviewRequired`; post-acceptance provider failures persist and return as `processingFailed`; pre-acceptance failures create no resource; PDF range delivery works; and reconciliation is repeatable after simulated interruption.

## 5. Phase 3 — Validation and workflow

### P3-01 — Draft save workflow

- **Goal:** Persist complete reviewed drafts with auditable corrections and optimistic concurrency.
- **Dependencies:** P1-05, P1-06, and P2-07.
- **Exclusive ownership:** Draft-save application service, canonical diff orchestration, correction/audit persistence, and draft controller.
- **Deliverables:** Accept the complete editable draft plus expected version; normalize inputs; reject missing/state/version conflicts in contract order; append field corrections for actual canonical changes including reverts; increment exactly once for changed saves; invalidate current validation and return to `reviewRequired`; append an empty no-op audit without changing version or status.
- **Source references:** PRD sections 10.2, 11.4, and 11.5; Architecture sections 5.3, 9.1, and 9.2; Contracts sections 7.1 and 10.
- **Verification:** Tests cover every field type, first/repeated/reverted correction, immutable originals, review notes, zero/null differences, no-op saves, ready-state invalidation, stale saves, terminal states, atomic rollback, and currentVersion conflicts.
- **Handoff:** The UI can preserve and reconcile drafts using a reliable versioned save contract.

### P3-02 — Explicit validation workflow

- **Goal:** Validate exactly the persisted draft version requested and atomically publish its current results.
- **Dependencies:** P1-04, P1-05, P1-06, and P2-07.
- **Exclusive ownership:** Explicit-validation service, duplicate-query coordination, validation persistence, and validation controller.
- **Deliverables:** Check existence, expected version, and state; snapshot current values; obtain all duplicate candidates; run pure rules; begin a short transaction; recheck version; persist immutable run/results, current pointer, last-validated version, status, and reviewer-triggered audit; return `readyForApproval` only with no errors.
- **Source references:** PRD sections 10.2 and 12; Architecture sections 5.4 and 9.3; Contracts sections 5, 7.2, and 10.
- **Verification:** Tests cover all result data shapes, deterministic ordering, warnings-only readiness, blocking errors, duplicate changes, self-exclusion, stale snapshot with no persistence, historical-run retention, and exact `VALIDATION_STALE`/version conflict responses.
- **Handoff:** Approval and the frontend can trust current validation/version pointers without rerunning extraction.

### P3-03 — Approval workflow

- **Goal:** Make approval an atomic backend decision protected by fresh deterministic validation.
- **Dependencies:** P3-01 and P3-02.
- **Exclusive ownership:** Approval application service, approval transaction, approval audits, and approval controller.
- **Deliverables:** Require `readyForApproval`, matching expected version, and current validation; rerun deterministic rules and duplicate lookup inside the approval transaction; persist a new approval-triggered validation; on new errors commit `reviewRequired` and return `409 APPROVAL_BLOCKED`; otherwise atomically persist decision, `approved`, validation audit, and approval audit.
- **Source references:** PRD sections 9.2, 10.3, and 11.5; Architecture section 9.4; Contracts sections 7.2, 10, 11, and 12.2.
- **Verification:** Tests cover stale/unvalidated/errored/terminal states, newly introduced duplicates, date changes, warnings-only approval, deterministic event order, blocked-state persistence, successful atomicity, and transaction rollback.
- **Handoff:** Direct API calls cannot bypass financial validation or approve a stale draft.

### P3-04 — Rejection workflow

- **Goal:** Persist a reasoned terminal rejection without unnecessary validation.
- **Dependencies:** P1-06 and P3-01.
- **Exclusive ownership:** Rejection application service, rejection decision/audit persistence, and rejection controller.
- **Deliverables:** Trim the reason; reject blank reasons; check existence, expected version, and permitted source status in contract order; do not run validation; atomically persist rejection reason/timestamp, `rejected`, and audit event; enforce terminal read-only behavior.
- **Source references:** PRD sections 9.2, 10.4, and 11.5; Architecture sections 5.5 and 9.4; Contracts sections 7.2, 10, 11.2, and 12.2.
- **Verification:** Tests cover blank/whitespace reasons, both allowed source statuses, stale version, every disallowed state, no validation side effects, exact errors, audit data, and rollback.
- **Handoff:** The frontend can offer one explicit rejection confirmation backed by immutable decision data.

### P3-05 — Detail, history, and export APIs

- **Goal:** Expose a complete persisted review/terminal representation and immutable audit record.
- **Dependencies:** P2-09, P3-01, P3-02, P3-03, and P3-04.
- **Exclusive ownership:** Detail/history/export query services, projections, controller files, and export serialization.
- **Deliverables:** Implement `GET /api/invoices/{id}`; paginated history ordered by occurredAt then numeric ID; terminal-only `GET /export` with schema version `1.0`, complete unpaginated history, stable filename, and no generated timestamp; include fields, originals, provenance, corrections, validation, document metadata, failures, and decisions exactly as contracted.
- **Source references:** PRD sections 11.5, 11.6, and 13; Architecture section 11; Contracts sections 4 through 6, 9.3, 9.4, 12, and 13.
- **Verification:** Contract tests cover every status shape, explicit nulls and empty arrays, sequence IDs as strings, history paging/order, terminal eligibility, stable semantic export content, restart reads, and exclusion of storage keys, paths, text, prompts, provider data, and PDF bytes.
- **Handoff:** Review and completed-record clients have one authoritative detail model plus dedicated history and export resources.

### P3-06 — Queue API

- **Goal:** Provide deterministic, performant queue search, filtering, pagination, and global summaries.
- **Dependencies:** P1-05, P1-06, P2-07, and P3-02.
- **Exclusive ownership:** Queue query projection, summary aggregation, queue controller, and queue performance fixture.
- **Deliverables:** Implement all queue fields; case-insensitive trimmed search across the four contracted identifiers only; repeated status filters; all four sort modes and UUID tie-breakers; one-based pagination; empty out-of-range pages; global summary counts independent of filters; current warning/error counts and separate processing failures.
- **Source references:** PRD section 11.3 and 15.1; Architecture sections 6.3 and 12.1; Contracts section 8.
- **Verification:** Tests cover defaults, maximum and invalid page sizes, multiple statuses, each search field, excluded content, each sort/tie-breaker, filtered totals versus global summary, processing-failure counts, empty pages, and first-page latency with 1,000 local records.
- **Handoff:** The frontend can keep all queue state in route/query parameters without client-side filtering or summary recomputation.

### P3-07 — Workflow integration hardening

- **Goal:** Prove concurrency, transaction, error, audit, and terminal invariants across the complete backend workflow.
- **Dependencies:** P3-01, P3-02, P3-03, P3-04, P3-05, and P3-06.
- **Exclusive ownership:** Cross-workflow race/fault tests and full stable HTTP error-catalog assertions.
- **Deliverables:** Race stale save, validation, approval, and rejection; inject failures during multi-record writes; create duplicate changes between validation and approval; cover not-found/version/state/operation check order; assert atomic status/decision/audit behavior; verify all material audit event types and correction links.
- **Source references:** PRD sections 18.1 and 18.2; Architecture sections 9 and 15; Contracts sections 10 through 12 and 15.
- **Verification:** Tests show `409` without lost updates, no partial values/history after rollback, persisted blocked approvals, terminal immutability, deterministic audit IDs/order, correct fields/currentVersion, and safe correlated Problem Details for every catalog code.
- **Handoff:** The backend is contract-complete and ready for final generation and frontend integration.

### P3-08 — Final backend contract generation

- **Goal:** Freeze the complete HTTP v1 surface into reproducible OpenAPI and generated client artifacts.
- **Dependencies:** P1-10, P3-05, P3-06, and P3-07.
- **Exclusive ownership:** Final OpenAPI snapshot, generated TypeScript client, and contract-drift baseline.
- **Deliverables:** Regenerate after all ten routes exist; verify multipart part name, response codes, required/nullable flags, string enums, discriminators, Problem Details, pagination, export, dates, money, UUIDs, and sequence IDs; commit only deterministic generated differences.
- **Source references:** Architecture section 12.3; Contracts sections 9 and 15.
- **Verification:** Backend build plus contract generation produces no second-run diff; generated code compiles; the snapshot contains exactly the accepted route set and schemas; presentation code still has no handwritten wire duplicates.
- **Handoff:** Frontend feature agents consume one final generated API surface.

### Phase 3 gate

Every HTTP v1 route conforms to `CONTRACTS.md`; backend unit, integration, concurrency, fault-injection, serialization, and drift checks pass; no operation loses or partially commits invoice state.

## 6. Phase 4 — Reviewer experience

### P4-01 — Frontend API adapters

- **Goal:** Isolate generated transport code from feature state and forms.
- **Dependencies:** P3-08.
- **Exclusive ownership:** Feature API adapters, correlation-aware fetch wrapper, feature error translation, TanStack Query keys, and shared mutation invalidation policy.
- **Deliverables:** Wrap queue, upload, detail, document, draft, validate, approve, reject, history, and export operations; translate generated Problem Details into feature-facing errors while preserving code/fields/currentVersion; define exact query invalidation/replacement after mutations; expose form-friendly values without redefining server contracts.
- **Source references:** PRD sections 13.2 and 15.5; Architecture sections 12.2 and 12.3; Contracts sections 9 through 11.
- **Verification:** Adapter tests cover URL/query construction, repeated statuses, correlation handling, binary/document responses, attachment export, each mutation's cache effects, and conflict data preservation.
- **Handoff:** Presentation components depend on stable feature functions rather than generated transport classes.

### P4-02 — Invoice queue

- **Goal:** Build the landing workspace for scanning, finding, filtering, and opening invoices.
- **Dependencies:** P3-06 and P4-01.
- **Exclusive ownership:** Queue route, summary cards, filter/search/pagination controls, row/table presentation, and queue component tests.
- **Deliverables:** Render pending, warning, error, and approved summaries; compact supplier/reference/date/total/status/exception rows; trimmed search and multi-status filtering through route parameters; deterministic pagination; loading, no-invoice, no-result, and API-error states with clear actions.
- **Source references:** PRD sections 11.3 and 14.2; Architecture sections 12.1 and 12.2; Contracts section 8.
- **Verification:** Component tests cover every state, route restoration, filter clearing, pagination, monetary/date formatting, processing failures, status labeling without color dependence, and stale-data avoidance after errors.
- **Handoff:** Upload and detail routes can invalidate and return to a reliable queue without duplicating list state.

### P4-03 — Upload interaction

- **Goal:** Let a reviewer submit one PDF and understand both reviewable and failed-processing outcomes.
- **Dependencies:** P2-09 and P4-01.
- **Exclusive ownership:** Upload action/dialog, file selection state, convenience checks and guidance, progress state, and upload component tests.
- **Deliverables:** Accept one PDF; show backend-owned configured-limit guidance without enforcing an independent policy; prevent duplicate submission; render safe Problem Details for pre-acceptance failures; treat `201 processingFailed` as a created record with actionable failure details; navigate successful results to the invoice route and invalidate the queue.
- **Source references:** PRD sections 10.1, 11.1, and 14.2; Architecture section 8; Contracts section 9.1.
- **Verification:** Tests cover selection/replacement, empty and multiple input behavior, progress, double clicks, server errors, failed-processing creation, successful navigation, and query invalidation.
- **Handoff:** A created invoice reaches the common detail route regardless of its processing outcome.

### P4-04 — Review form foundation

- **Goal:** Present the complete editable invoice draft with provenance, confidence, validation, and summary concepts kept distinct.
- **Dependencies:** P3-05 and P4-01.
- **Exclusive ownership:** Review detail route, React Hook Form model, field groups, formatting/parsing helpers, field metadata display, and review-summary presentation.
- **Deliverables:** Populate supplier, reference, dates/terms, amounts, and review groups; preserve punctuation, leading zeroes, nullable values, exact two-decimal money text, and date-only values; show confidence/source separately from validation severity; disclose original values and correction state; derive display counts only from backend response data.
- **Source references:** PRD sections 8, 11.4, and 14.3; Architecture sections 12.1 and 12.2; Contracts sections 4 through 6.
- **Verification:** Tests cover every field, zero versus null, money/date formatting, original/current values, confidence bands, source labels, warning/error separation, correction counts, processing failures, and noneditable system metadata.
- **Handoff:** Save, validation, PDF, decision, and completed-state tasks extend one stable detail/form model.

### P4-05 — PDF review panel

- **Goal:** Keep the source invoice usable beside the form without freezing or resetting review state.
- **Dependencies:** P2-09 and P4-04.
- **Exclusive ownership:** React-PDF integration, PDF.js local assets, review split-panel layout, page/zoom/panel controls, and PDF viewer tests.
- **Deliverables:** Load through the document endpoint with ranges; bundle worker, CMaps, and standard fonts locally; render the visible page plus a small buffer with capped device density; preserve page, zoom, and panel width across detail refetches; provide a usable stacked/tabbed narrow layout and document-unavailable state.
- **Source references:** PRD sections 11.1, 11.4, 14.3, and 15.1; Architecture section 12.4; Contracts section 9.2.
- **Verification:** Tests cover document URL use, load/failure states, pagination and zoom controls, mutation refetch stability, responsive fallback, keyboard operation, and absence of CDN requests.
- **Handoff:** Reviewers can compare source and fields throughout edits without navigation or lost viewer context.

### P4-06 — Saving, dirty state, and conflicts

- **Goal:** Save explicit drafts without silently discarding or overwriting reviewer work.
- **Dependencies:** P3-01 and P4-04.
- **Exclusive ownership:** Draft mutation UI, dirty-state/navigation guards, conflict presentation, form reset policy, and save tests.
- **Deliverables:** Send a complete draft with current expected version; disable duplicate saves; reset form baseline only from successful server data; protect route changes and browser unload; prevent decision attempts with unsaved changes; on `409`, preserve entered values, show conflict/current version, and offer explicit refetch for manual reconciliation without automatic retry or merge.
- **Source references:** PRD sections 10.2, 11.4, and 11.5; Architecture sections 9.2 and 12.2; Contracts sections 7.1 and 10.
- **Verification:** Tests cover changed and no-op saves, every editable value, readiness invalidation, navigation cancel/confirm, unload registration, duplicate submission, validation errors, conflict preservation, explicit refetch, and correction/original refresh.
- **Handoff:** Revalidation and decisions operate only on an explicitly saved current version.

### P4-07 — Revalidation experience

- **Goal:** Make backend validation results actionable without reproducing financial logic in the browser.
- **Dependencies:** P3-02 and P4-06.
- **Exclusive ownership:** Revalidation action, inline result rendering, aggregate validation summary, field/result linking, focus movement, and validation UI tests.
- **Deliverables:** Submit the current saved expected version; disable while active; update detail/list/history caches from the response; render warning and error labels separately from confidence; show structured rule data; move focus to the first blocking field after validation; gate readiness actions from server status only.
- **Source references:** PRD sections 10.2, 12, and 14.4; Architecture sections 12.2 and 12.4; Contracts section 5.
- **Verification:** Tests cover warnings-only readiness, multiple blocking fields, summary counts, stable field linking, focus behavior, stale validation conflicts, no duplicated rule calculations, and action enabled/disabled states.
- **Handoff:** The decision UI can trust the persisted status and current version shown after revalidation.

### P4-08 — Approval and rejection experience

- **Goal:** Provide explicit, accessible terminal decisions while preserving backend authority.
- **Dependencies:** P3-03, P3-04, and P4-07.
- **Exclusive ownership:** Approval/rejection controls, confirmation dialogs, rejection form, decision conflict/error presentation, and decision tests.
- **Deliverables:** Enable approval only for server status `readyForApproval`; require confirmation; require and trim a rejection reason client-side for usability while honoring server errors; block both actions when dirty; prevent duplicate submissions; handle `APPROVAL_BLOCKED` and version/state conflicts by preserving context and refetching explicitly; transition successful decisions to read-only detail.
- **Source references:** PRD sections 10.3, 10.4, 11.5, and 14.4; Architecture section 12.4; Contracts sections 7.2, 10, and 11.
- **Verification:** Tests cover all gating states, keyboard/focus restoration, blank reason, warnings-only approval, blocked approval, stale conflicts, confirmation cancellation, duplicate actions, success cache updates, and terminal state.
- **Handoff:** The completed-record view receives authoritative decision data rather than locally inferred state.

### P4-09 — Completed records

- **Goal:** Make approved and rejected invoices fully inspectable and immutable after restart.
- **Dependencies:** P3-05 and P4-04.
- **Exclusive ownership:** Terminal detail presentation, history list/pagination, correction and decision display, document/export actions, and completed-record tests.
- **Deliverables:** Render final values, status, latest validation, original values, corrections, notes, decision timestamp/reason, document metadata, source access, complete paginated audit history, and terminal JSON export; remove or disable all editing/validation/decision controls; handle unavailable documents and export errors safely.
- **Source references:** PRD sections 10.3, 10.4, and 11.5; Architecture section 12.1; Contracts sections 9.2 through 9.4, 12, and 13.
- **Verification:** Tests cover approved/rejected differences, warning retention, correction/event ordering, history pagination, PDF link behavior, export attachment handling, restart-shaped reloads, and complete read-only enforcement.
- **Handoff:** Terminal records satisfy the demonstration's traceability and artifact-access requirements.

### P4-10 — Frontend integration hardening

- **Goal:** Complete cross-feature frontend coverage and remove state-management regressions before end-to-end work.
- **Dependencies:** P4-02, P4-03, P4-04, P4-05, P4-06, P4-07, P4-08, and P4-09.
- **Exclusive ownership:** Cross-feature component/integration tests, shared frontend test builders, and fixes confined to already accepted UI behavior.
- **Deliverables:** Exercise queue invalidation after upload/save/validation/decision; detail refetch without PDF-state reset; dirty navigation across routes; Problem Details field mapping; conflict recovery; all loading/empty/error states; confidence versus validation; terminal transitions; rejection reason and action gating.
- **Source references:** PRD section 18.3; Architecture section 15 frontend row.
- **Verification:** The complete frontend suite runs deterministically without a live backend; no test relies solely on snapshots for interaction behavior; no browser component contains duplicate financial, currency, confidence, or lifecycle policy.
- **Handoff:** The integrated reviewer experience is stable enough for accessibility, visual, production-host, and Playwright acceptance work.

### Phase 4 gate

The queue, upload, review, save, revalidate, approve, reject, history, PDF, and export experiences work against deterministic backend providers; required frontend tests pass; server state and editable form state remain correctly separated.

## 7. Phase 5 — Polish and demonstration readiness

### P5-01 — Representative fixture corpus

- **Goal:** Provide sanitized, deterministic documents and scenarios that exercise the accepted demonstration surface.
- **Dependencies:** P2-10 and P3-07.
- **Exclusive ownership:** Shared acceptance PDF corpus, expected extraction proposals, deterministic provider profiles, and fixture documentation.
- **Deliverables:** Add at least one text and one scanned PDF; valid USD, EUR, GBP, and MKD cases; low/unknown-confidence values; duplicate pairs; malformed, encrypted, empty, wrong-signature, exact/over size, exact/over page-count, and processing-failure cases; synthetic data only, with no real supplier or personal information.
- **Source references:** PRD sections 18.4 and 19; Architecture section 15; Decision DEC-004, DEC-005, and DEC-007.
- **Verification:** Fixtures are reproducible, license-safe, documented, small except intentional boundary files, and usable without real OCR/OpenAI in default tests; exact expected values and failure categories are asserted.
- **Handoff:** Playwright and portability tasks share one stable acceptance corpus rather than inventing local samples.

### P5-02 — Accessibility and responsive polish

- **Goal:** Bring the integrated interface to the required accessible financial-operations standard.
- **Dependencies:** P4-10.
- **Exclusive ownership:** Cross-page accessibility fixes, responsive behavior, focus polish, contrast/tokens, and accessibility regression tests.
- **Deliverables:** Verify keyboard operation, programmatic labels, focus after validation/dialogs/errors, summary-to-field navigation, color-independent statuses, WCAG 2.2 AA contrast, duplicate-action prevention, narrow review layout, readable compact tables, tabular numerals, and restrained specified visual direction.
- **Source references:** PRD section 14 and section 19; Architecture sections 12.4 and 15.
- **Verification:** Automated accessibility checks report no serious violations on queue, review, dialogs, failures, and completed states; documented keyboard and responsive manual checks pass at representative widths.
- **Handoff:** End-to-end scenarios can assert stable accessible names and focus outcomes.

### P5-03 — Happy-path end-to-end test

- **Goal:** Automate the primary portfolio journey through the production-facing browser surface.
- **Dependencies:** P4-10 and P5-01.
- **Exclusive ownership:** Playwright happy-path scenario and its isolated application/test-provider lifecycle.
- **Deliverables:** Cover Upload → Review → Correct → Save → Revalidate → Approve → Inspect history; assert the corrected final value, retained original, version increment, validation of the latest version, approval audit, document access, and read-only terminal state.
- **Source references:** PRD sections 10, 18.4, and 19; Architecture section 15.
- **Verification:** The scenario passes repeatedly from an empty temporary data root, uses no network provider, captures actionable failure artifacts, and leaves no source-controlled data.
- **Handoff:** The central release journey has one executable acceptance proof.

### P5-04 — Failure and decision end-to-end tests

- **Goal:** Automate the highest-risk reviewer-visible alternatives to the happy path.
- **Dependencies:** P4-10 and P5-01.
- **Exclusive ownership:** Playwright failure/rejection/conflict/restart scenarios and their scenario-specific fixtures.
- **Deliverables:** Cover invalid pre-acceptance upload, persisted processing failure, blocking validation, rejection with reason, stale save conflict with unsaved-value preservation, document access, export eligibility, approved/rejected immutability, and persistence across a controlled application restart.
- **Source references:** PRD sections 9 through 13, 18.4, and 19; Architecture section 15.
- **Verification:** Tests assert stable error codes and accessible user recovery, avoid timing races through deterministic controls, and demonstrate that UI restrictions match direct API enforcement.
- **Handoff:** Release verification covers both successful and exceptional business paths.

### P5-05 — Production-style hosting

- **Goal:** Publish one loopback-bound ASP.NET Core process that serves both the API and built React application.
- **Dependencies:** P1-07 and P4-10.
- **Exclusive ownership:** Production host/static-file composition, Vite-to-publish integration, CSP/response headers, SPA fallback order, and publish configuration.
- **Deliverables:** Copy Vite output and local PDF.js assets into publish output; map API/document routes before fallback; serve one origin; bind to configured loopback only; add a conservative same-origin CSP compatible with PDF.js; retain range delivery and safe error handling in published mode.
- **Source references:** PRD sections 15.3 and 16.3; Architecture sections 12.4, 13, and 14; Decision DEC-001.
- **Verification:** A clean publish serves deep-linked SPA routes, API JSON, PDF ranges, worker/fonts/CMaps, and CSP without a development server or CDN; non-loopback defaults are rejected.
- **Handoff:** Startup documentation can launch the actual demonstration artifact rather than separate development processes.

### P5-06 — Startup and setup experience

- **Goal:** Give a fresh evaluator one documented cross-platform command with actionable prerequisite checks.
- **Dependencies:** P2-08, P2-09, and P5-05.
- **Exclusive ownership:** Production startup preflight sequence, cross-platform launcher/command, setup documentation, and startup smoke tests.
- **Deliverables:** Validate options/loopback, directories, migrations/SQLite, PDFium, Tesseract version and `eng`, OpenAI credential/model, then reconciliation before listening; document Windows, macOS, and Linux prerequisites, user-secret/environment configuration, normalized-text disclosure, storage location behavior, deterministic test-provider mode, and troubleshooting without printing secrets or sensitive path content.
- **Source references:** PRD sections 16.3, 17, and 19; Architecture section 13; Decisions DEC-001 through DEC-003.
- **Verification:** Fresh-path tests cover successful deterministic startup and each failed preflight; the documented command builds/starts the production-style host on each platform family; no committed configuration contains a key.
- **Handoff:** An evaluator can set up, run, stop, restart, and troubleshoot the local demonstration without undocumented steps.

### P5-07 — Security and diagnostics audit

- **Goal:** Verify that integrated behavior preserves the local security/privacy boundary and provides safe diagnostics.
- **Dependencies:** P2-10, P3-07, P4-10, and P5-05.
- **Exclusive ownership:** Cross-cutting security/privacy/observability tests and narrowly scoped hardening fixes.
- **Deliverables:** Audit untrusted upload metadata, generated paths, shell-free Tesseract invocation, exact-origin CORS, same-origin CSP, loopback binding, anti-sniffing/range headers, correlation propagation, stage/duration/status logs, provider settings, and normal-log exclusions for content, filenames, bodies, prompts, responses, secrets, and local paths.
- **Source references:** PRD sections 15.3, 15.4, and 17; Architecture section 14 and section 15; Contracts section 11.
- **Verification:** Automated probes cover traversal, hostile metadata, invalid correlation IDs, representative failures, CORS/CSP, secret scanning, and captured-log assertions; HTTP errors contain no stack traces or infrastructure details.
- **Handoff:** Performance and final-release checks operate on the hardened production configuration.

### P5-08 — Performance and portability verification

- **Goal:** Demonstrate that the finished local application meets its performance, persistence, cancellation, publish, and platform obligations.
- **Dependencies:** P5-02, P5-03, P5-04, P5-05, P5-06, and P5-07.
- **Exclusive ownership:** Performance harness/results, restart and publish smoke checks, three-platform verification record, and opt-in real-provider smoke suite.
- **Deliverables:** Measure queue first page and local mutations against the 500 ms goal with 1,000 records; verify incremental rows/PDF pages; exercise OCR/AI cancellation; restart with committed database/PDF/correction/history state; publish and start on Windows, macOS, and Linux; keep installed-Tesseract and configured-OpenAI smoke tests opt-in.
- **Source references:** PRD sections 15.1, 15.2, and 19; Architecture section 15; Decision DEC-001.
- **Verification:** Record reproducible commands, environment, and pass/fail thresholds; default automation remains deterministic and provider-free; all three platform checks include paths, PDFium, Tesseract preflight/termination, SQLite persistence, publish, and the single startup path.
- **Handoff:** The final gate receives evidence rather than unverified portability or performance claims.

### P5-09 — Final release gate

- **Goal:** Decide demonstration readiness against every accepted requirement and artifact-safety constraint.
- **Dependencies:** P5-01, P5-02, P5-03, P5-04, P5-05, P5-06, P5-07, and P5-08.
- **Exclusive ownership:** Final acceptance checklist, clean-environment verification, contract drift check, and release report.
- **Deliverables:** Run Core, architecture, integration, frontend, end-to-end, publish, and documentation checks; trace every PRD release criterion and Architecture verification row to passing evidence; regenerate contracts with no diff; perform a clean deterministic setup; scan tracked content for credentials, invoice documents, databases, storage roots, temporary files, logs, and provider output.
- **Source references:** PRD section 19; Architecture section 15; Contracts section 15.
- **Verification:** Every mandatory check passes or the release remains blocked with the owning task identified; opt-in real-provider smoke status is reported separately and cannot mask deterministic-suite failures.
- **Handoff:** A fresh evaluator can configure, launch, demonstrate, restart, inspect, and verify the MVP through the documented path.

### Phase 5 gate

A fresh local setup launches the published application through one documented command; the text and scanned demonstration paths work; all required suites and platform checks pass; records survive restart; and no secret or generated invoice data is tracked.

## 8. Parallel execution waves

Tasks inside one wave may run concurrently only when their dependencies are merged and their declared ownership areas remain separate. A parenthesized arrow denotes required sequencing within a broader wave.

| Wave | Safe assignments | Exit condition |
| --- | --- | --- |
| 1 | P1-01 | Repository skeleton is merged |
| 2 | P1-02, P1-03, P1-09 | Domain, configuration, and frontend foundations are available |
| 3 | P1-04, P1-05, P1-06 | Validation, persistence, and HTTP boundaries are available |
| 4 | P1-07; then P1-08 and P1-10 in parallel | Host, generation, and shared test harness pass the Phase 1 gate |
| 5 | P2-01, P2-02 | Extraction mapping and storage seams are available |
| 6 | P2-03, P2-06, P2-08; then P2-04 and P2-05 | All concrete ingestion adapters and recovery behavior are complete |
| 7 | P2-07 → P2-09 → P2-10 | Phase 2 gate passes |
| 8 | P3-01, P3-02; then P3-03 and P3-04 | All mutation workflows are available |
| 9 | P3-05, P3-06; then P3-07 → P3-08 | Phase 3 gate passes and the final client is generated |
| 10 | P4-01; then P4-02, P4-03, and P4-04 | Frontend adapters and primary route foundations are available |
| 11 | P4-05, P4-06, P4-09; then P4-07 → P4-08 → P4-10 | Phase 4 gate passes |
| 12 | P5-01, P5-02, P5-05 | Fixtures, polish, and production host are ready |
| 13 | P5-03, P5-04, P5-06, P5-07 | End-to-end, startup, and security evidence is available |
| 14 | P5-08 → P5-09 | Phase 5 gate and release decision are complete |

Within a wave, an orchestrator may reduce concurrency to respect its available agent limit. It must not move a task earlier than its declared dependencies or run two owners of the same shared hotspot concurrently.

## 9. Requirement traceability

### 9.1 HTTP routes

| Contract route | Owning task |
| --- | --- |
| `POST /api/invoices` | P2-09 |
| `GET /api/invoices` | P3-06 |
| `GET /api/invoices/{id}` | P3-05 |
| `GET /api/invoices/{id}/document` | P2-09 |
| `PUT /api/invoices/{id}/draft` | P3-01 |
| `POST /api/invoices/{id}/validate` | P3-02 |
| `POST /api/invoices/{id}/approve` | P3-03 |
| `POST /api/invoices/{id}/reject` | P3-04 |
| `GET /api/invoices/{id}/history` | P3-05 |
| `GET /api/invoices/{id}/export` | P3-05 |

P1-06 owns the shared representation and error boundary for every route, P3-07 verifies cross-route behavior, and P3-08 freezes the completed surface into OpenAPI and the generated client.

### 9.2 Validation and audit contracts

| Contract behavior | Implementation owner | Integration owner |
| --- | --- | --- |
| `REQUIRED_FIELD_MISSING` | P1-04 | P2-07, P3-02, P3-03 |
| `AMOUNT_RECONCILIATION_FAILED` | P1-04 | P2-07, P3-02, P3-03 |
| `NEGATIVE_AMOUNT_UNEXPECTED` | P1-04 | P2-07, P3-02, P3-03 |
| `DUE_DATE_BEFORE_INVOICE_DATE` | P1-04 | P2-07, P3-02, P3-03 |
| `PAYMENT_TERMS_MISMATCH` | P1-04 | P2-07, P3-02, P3-03 |
| `POSSIBLE_DUPLICATE_INVOICE` | P1-04 | P3-02, P3-03 |
| `CURRENCY_INVALID` | P1-04 | P2-07, P3-02, P3-03 |
| `LOW_EXTRACTION_CONFIDENCE` | P1-04 | P2-07, P3-02, P3-03 |
| `INVOICE_DATE_IN_FUTURE` | P1-04 | P2-07, P3-02, P3-03 |
| `invoiceUploaded` | P2-07 | P2-09, P2-10 |
| `extractionCompleted` | P2-07 | P2-09, P2-10 |
| `extractionFailed` | P2-07, P2-08 | P2-09, P2-10 |
| `draftSaved` | P3-01 | P3-07, P4-06 |
| `validationCompleted` | P2-07, P3-02, P3-03 | P3-07, P4-07 |
| `invoiceApproved` | P3-03 | P3-07, P4-08 |
| `invoiceRejected` | P3-04 | P3-07, P4-08 |
| `documentIntegrityChanged` | P2-08, P2-09 | P2-10, P4-09 |

### 9.3 Architecture verification matrix

| Verification area | Task coverage |
| --- | --- |
| Dependency direction | P1-02, P1-08 |
| Aggregate rules | P1-02, P1-04 |
| Deterministic validation | P1-04, P3-02, P3-03 |
| Corrections and original values | P3-01, P3-07 |
| Optimistic concurrency | P3-01, P3-07 |
| Transaction atomicity and rollback | P2-07, P2-10, P3-03, P3-07 |
| Upload acceptance boundaries | P2-03, P2-10 |
| Storage consistency and reconciliation | P2-02, P2-08, P2-10 |
| Native text and OCR | P2-04, P2-05, P2-10 |
| AI provider contract | P2-01, P2-06, P2-10 |
| HTTP error mapping | P1-06, P3-07 |
| PDF delivery | P2-09, P2-10 |
| Frontend behavior | P4-02 through P4-10 |
| OpenAPI and generated-client drift | P1-10, P3-08 |
| End-to-end workflow | P5-03, P5-04 |
| Cross-platform operation | P5-05, P5-06, P5-08 |

### 9.4 PRD release acceptance

| Release criterion | Task coverage |
| --- | --- |
| Fresh documented local setup | P5-06, P5-08 |
| One production-style startup command | P5-05, P5-06 |
| Text and scanned PDF paths | P2-04, P2-05, P5-01, P5-03 |
| Accurate queue summaries and filtering | P3-06, P4-02 |
| Source PDF beside editable fields | P4-04, P4-05 |
| Auditable manual corrections | P3-01, P4-06, P5-03 |
| Complete structured validation rule set | P1-04, P3-02 |
| Blocking errors prevent direct and UI approval | P3-03, P4-08, P5-04 |
| Approval and reasoned rejection | P3-03, P3-04, P4-08 |
| Terminal records survive restart and remain read-only | P3-05, P4-09, P5-04 |
| JSON export and source document access | P2-09, P3-05, P4-09 |
| All automated suites pass | P5-09 |
| No secrets or generated invoice data are tracked | P1-01, P5-07, P5-09 |
| Required visual direction and accessibility | P1-09, P5-02 |

## 10. Final definition of done

The MVP is implementation-complete only when:

- Every task card has a merged handoff satisfying its verification and no unresolved contract or architecture blocker.
- Every PRD release acceptance criterion is backed by an automated check or an explicit cross-platform/manual verification record where automation is impractical.
- All ten HTTP routes, nine validation codes, eight audit event types, stable HTTP errors, processing-failure codes, extraction schema `1.0`, and export schema `1.0` match `CONTRACTS.md`.
- The Architecture section 15 verification matrix is fully covered, including concurrency, transaction failure, storage consistency, provider behavior, contract drift, accessibility, end-to-end flow, and cross-platform startup.
- The default automated suites use deterministic OCR and AI adapters; real Tesseract/OpenAI smoke tests remain opt-in and clearly reported.
- The production-style publish is loopback-bound, same-origin, restart-durable, and launchable through the documented single command.
- No credential, real invoice, PDF fixture containing sensitive information, database, storage root, log, normalized document text, prompt, or raw provider response is committed.

## 11. Assumptions

- A focused task is one independently reviewable outcome, normally confined to one primary subsystem plus its tests.
- Minimal fixtures required to test an earlier task belong to that task; P5-01 consolidates the representative acceptance corpus.
- Pure validation appears in Phase 1 because successful ingestion must persist an initial validation run before entering `reviewRequired`.
- Shared manifest changes discovered after P1-01 are batched by the active phase integrator rather than edited concurrently by feature agents.
- Human-readable messages may improve within the accepted contracts, but agents consume stable codes and structured data.
