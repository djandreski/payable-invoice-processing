# Invoice Review Assistant: Implementation Decisions

| Document attribute | Value |
| --- | --- |
| Status | Accepted for MVP implementation |
| Applies to | Invoice Review Assistant MVP |
| Decision date | September 13, 2026 |
| Requirements source | [`PRD.md`](../PRD.md), especially section 22.2 |

## Purpose and authority

This document resolves implementation choices that the PRD intentionally leaves open. The PRD remains the source of truth for product scope, behavior, and acceptance criteria. This record controls the choices listed below unless a later accepted decision explicitly supersedes one of them.

Implementation agents must not silently replace these choices. A change must record the reason, consequences, and superseded decision in this file before implementation proceeds.

## DEC-001: Supported operating systems

**Status:** Accepted

**Decision:** The finished local application will support Windows, macOS, and Linux. The supported experience includes application startup, native PDF text extraction, OCR fallback, SQLite persistence, local document storage, and the production React build served by ASP.NET Core.

Platform-specific prerequisite installation may differ, but application behavior and persisted data contracts must remain platform-neutral. Containers and cloud infrastructure remain outside the MVP.

**Rationale:** Cross-platform support makes the portfolio demonstration usable by more evaluators while preserving the PRD's local, self-contained architecture.

**Consequences:**

- Filesystem paths, process invocation, startup scripts, and storage locations must not assume Windows path syntax.
- Automated tests must not depend on an operating-system-specific OCR API.
- Setup documentation must provide prerequisite instructions for all three operating-system families.

**Rejected alternatives:**

- Windows-only implementation: rejected because it unnecessarily limits evaluation and would couple OCR to one operating system.
- Container-based portability: rejected because containers are outside the MVP scope.

## DEC-002: OCR engine and integration boundary

**Status:** Accepted

**Decision:** Tesseract 5 is the default local OCR engine. The application will call the `tesseract` executable through a replaceable backend adapter and use the English language data set for the MVP.

Tesseract is a documented native prerequisite and is not bundled with the application. Its executable path must be configurable, with `tesseract` on `PATH` as the default. Production-style startup must run a preflight check and fail with an actionable configuration message when the configured executable or English language data is unavailable. Automated tests will replace the adapter with deterministic test implementations.

**Rationale:** Tesseract provides a mature, local, cross-platform OCR path without adding a cloud dependency or a separately managed Python service.

**Consequences:**

- Scanned invoice processing depends on a correctly installed Tesseract 5 runtime.
- Process execution must use structured arguments, timeouts, cancellation, and safe application-managed temporary paths.
- Platform-specific installation commands belong in the future `README.md`.
- PDF page rendering, the OCR usability threshold, and concrete backend package choices remain architecture decisions.

**Rejected alternatives:**

- PaddleOCR sidecar: rejected because its Python runtime, model downloads, and service lifecycle add avoidable MVP complexity.
- Bundled Tesseract binaries: rejected because maintaining native packages for every target platform expands release and licensing work.
- Cloud OCR: rejected because it adds another external provider and weakens the local-first demonstration.
- Operating-system OCR APIs: rejected because they conflict with the cross-platform decision.

## DEC-003: AI extraction provider and model

**Status:** Accepted

**Decision:** OpenAI is the first supported AI extraction provider. The backend will use the Responses API with `gpt-5.6-terra` as the configurable default model, `low` reasoning effort, and strict Structured Outputs against the invoice extraction JSON Schema.

Only normalized document text will be sent to the provider in the MVP. The source PDF will not be uploaded to the AI provider. Provider responses must pass schema validation before any values are mapped into the domain model or persisted.

The API credential, model identifier, and provider settings must come from environment-specific configuration or user secrets. The OpenAI provider remains behind a backend interface and is replaced by deterministic implementations in automated tests.

[Official OpenAI documentation](https://developers.openai.com/api/docs/models/gpt-5.6-terra) describes GPT-5.6 Terra as balancing intelligence and cost and lists the Responses API, `low` reasoning effort, and Structured Outputs as supported.

**Rationale:** This choice favors credible extraction quality while retaining explicit schema enforcement, replaceability, and a reasonable cost profile for a single-user demonstration.

**Consequences:**

- Real invoice extraction requires internet access, an OpenAI API key, and available account capacity.
- Setup documentation must disclose that normalized invoice text is sent to OpenAI.
- Missing provider configuration must produce an actionable startup error in the real-provider profile.
- Provider timeouts, transport failures, refusals, and invalid structured responses must produce controlled processing failures without exposing prompts, invoice text, credentials, or raw provider responses.
- Changing the default model must be a configuration or recorded decision change, not a scattered code edit.

**Rejected alternatives:**

- A local model through Ollama: rejected because it adds model installation and hardware variability and makes extraction quality less predictable for the initial demonstration.
- A quality-first flagship model: rejected because the MVP does not yet have evidence that the additional cost improves its defined extraction fields.
- A lower-cost model as the initial default: rejected until representative invoice fixtures demonstrate comparable extraction reliability.
- Direct PDF or image input to the AI provider: rejected because the PRD defines native text extraction with OCR fallback before AI extraction.

## DEC-004: PDF size and page limits

**Status:** Accepted

**Decision:** The default maximum upload size is 20 MiB (`20 * 1024 * 1024` bytes), and the default maximum document length is 25 pages. Both values must be configurable from one backend configuration section and must not be duplicated in domain, API, or frontend code.

The backend is authoritative. It must reject an oversized or over-page-limit document before OCR or AI work begins and must not create a misleading reviewable invoice record. The frontend may mirror the configured limits only as convenience guidance.

**Rationale:** These limits comfortably cover representative invoices while keeping synchronous processing, memory use, and local demonstration time bounded.

**Consequences:**

- Size violations use the PRD-required `413` Problem Details response.
- Page-limit violations use a `400` Problem Details response; the stable extension code will be fixed in the API contract.
- Boundary tests must cover exactly 20 MiB versus one byte over and exactly 25 pages versus 26 pages.

**Rejected alternatives:**

- 10 MiB and 10 pages: rejected as unnecessarily restrictive for attachments and invoices with supporting pages.
- 50 MiB and 50 pages: rejected because it increases the risk of slow synchronous processing without an MVP use case.
- Unbounded configurable defaults: rejected because the demonstration needs safe behavior without additional setup.

## DEC-005: Encrypted and password-protected PDFs

**Status:** Accepted

**Decision:** The MVP will reject every encrypted or password-protected PDF, including documents that permit some operations without a password. The backend will return a `400` Problem Details response with the stable error code `PDF_ENCRYPTED`. Rejection occurs during integrity inspection, before an invoice record is created and before OCR or AI processing.

The application will not request, accept, retain, or attempt passwords.

**Rationale:** Uniform rejection keeps credential handling and partial PDF permission behavior out of the MVP while giving reviewers a precise remediation path.

**Consequences:** The user-facing error must instruct the reviewer to upload an unprotected copy without revealing parser details or local paths.

**Rejected alternatives:**

- Password entry in the MVP: rejected because it adds sensitive-input handling and new UI, API, and lifecycle states.
- Attempting extraction from partially accessible encrypted documents: rejected because behavior varies across PDF implementations and would make upload outcomes inconsistent.
- Deferring password entry while accepting and retaining failed records: rejected because encryption is detectable during initial document validation and should not create a processing record.

## DEC-006: Low-confidence reviewer acknowledgement

**Status:** Accepted

**Decision:** The MVP will not add a per-field confirmation control, a confirm-all control, or dedicated confidence-acknowledgement state. An explicit successful Save or Approve action is sufficient evidence that the reviewer acted on the displayed record, and the existing audit history records that action.

An unchanged field retains its original confidence and provenance, so its `LOW_EXTRACTION_CONFIDENCE` warning may remain visible through approval. A manual correction changes the current value source to `reviewer` while preserving the original extracted value and confidence. Low confidence remains a warning and never becomes an approval-blocking financial rule.

**Rationale:** The review workspace already keeps the source visible, records reviewer actions, and permits approval with warnings. Additional confirmation controls would add ceremony without strengthening deterministic financial validation.

**Consequences:**

- Approval needs no confidence-specific request field or persisted acknowledgement entity.
- Save and approval audit events provide the durable evidence of reviewer action.
- The UI must keep confidence visually distinct from validation and must not imply that Save improves extraction confidence.

**Rejected alternatives:**

- Confirming each uncertain field: rejected because it increases interaction cost and expands persistence and API contracts.
- One confirm-all action: rejected because it adds a new workflow gate that the PRD does not require.
- Promoting low confidence to an error: rejected because AI confidence is not a deterministic financial validation result.

## DEC-007: Initial currency set and acceptance fixtures

**Status:** Accepted

**Decision:** USD, EUR, GBP, and MKD are the initial allowed currencies and the required acceptance-fixture set. All four use a reconciliation tolerance of `0.01` in their respective currency. Currency values are normalized to uppercase ISO 4217 codes and stored separately from decimal monetary values.

The allowed-code list and tolerance mapping must be configurable in one backend-owned location. A missing, unrecognized, or disabled currency produces the PRD-defined `CURRENCY_INVALID` error. The frontend must consume backend results and must not maintain an independent allowed-currency or tolerance policy.

**Rationale:** The selected set covers the expected two-decimal demonstration behavior, including the project's local context, without claiming support for minor-unit rules that are not represented in acceptance fixtures.

**Consequences:**

- Automated fixtures must include valid examples for all four currencies and at least one unsupported or malformed code.
- Enabling another currency requires adding its tolerance and representative tests before it is added to the configured allowed list.
- Zero-decimal and three-decimal currencies are outside MVP acceptance coverage.

**Rejected alternatives:**

- USD and EUR only: rejected because GBP and MKD add useful two-decimal coverage at minimal complexity.
- JPY and KWD in the MVP: rejected because zero- and three-decimal behavior was explicitly deferred.
- Treating every syntactically valid ISO code as enabled: rejected because the MVP must not claim untested minor-unit behavior.

## Deferred implementation decisions

The following choices remain intentionally deferred to the architecture and contract documents because they do not change the decisions above:

- Native PDF text extraction and page-rendering packages.
- The measurable native-text usability threshold that triggers OCR.
- Concrete backend interface and DTO names.
- OpenAI provider retry, timeout, and cancellation values.
- The invoice extraction JSON Schema and API wire shapes.
- Platform-specific Tesseract installation commands and startup-script details.

## PRD open-decision coverage

| PRD section 22.2 question | Resolution |
| --- | --- |
| Default local OCR engine | DEC-002: Tesseract 5 command-line adapter |
| Initial AI provider and model | DEC-003: OpenAI Responses API with configurable `gpt-5.6-terra` default |
| Maximum PDF size and page count | DEC-004: 20 MiB and 25 pages |
| Encrypted PDF behavior | DEC-005: reject uniformly; no password handling |
| Low-confidence acknowledgement | DEC-006: explicit Save or Approve action; no dedicated confirmation state |
| Currency acceptance fixtures | DEC-007: USD, EUR, GBP, and MKD; two-decimal behavior only |

All six open questions have an accepted MVP decision. None of these decisions changes the governing product boundary: AI proposes extracted values, deterministic backend rules establish financial validity, and the reviewer makes the final decision.
