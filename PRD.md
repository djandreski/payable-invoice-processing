# Product Requirements Document: Invoice Review Assistant

| Document attribute | Value |
| --- | --- |
| Product | Invoice Review Assistant |
| Release | MVP / portfolio demonstration |
| Status | Draft for implementation |
| Platform | Local web application |
| Primary users | Accounts-payable reviewers |
| Last updated | September 13, 2026 |

## 1. Executive summary

Invoice Review Assistant is a local accounts-payable application for reviewing PDF invoices. It combines AI-assisted data extraction with deterministic financial checks and an explicit human approval workflow.

Users upload an invoice, inspect the source document beside the extracted fields, correct uncertain or incorrect values, re-run validation, and approve or reject the invoice. The application stores the source PDF, final structured record, validation results, reviewer decisions, and field-level correction history locally.

The MVP is intended to demonstrate a production-minded financial workflow without the operational complexity of cloud hosting, authentication, distributed processing, or third-party accounting integrations.

## 2. Product vision

Create a credible financial operations workspace that helps an accounts-payable reviewer turn an unstructured supplier invoice into a validated, traceable, approved record with less manual data entry and without surrendering financial control to AI.

The product should communicate:

- Accuracy: totals and required information are checked consistently.
- Control: the reviewer makes the final decision, and the backend enforces it.
- Traceability: extracted values, corrections, validations, and decisions are recorded.
- Efficiency: the source document and editable record are reviewed in one workspace.
- Calm professionalism: dense financial information remains readable and actionable.

## 3. Problem statement

Accounts-payable staff often receive invoices as PDFs and must manually transfer supplier, reference, date, and amount information into structured systems. This work is repetitive and error-prone. Extraction tools can reduce data entry, but their output may be incomplete or wrong, while visual confidence scores alone do not establish whether an invoice is financially valid.

Reviewers need a workflow that:

1. Accelerates initial data capture.
2. Keeps the source invoice visible during review.
3. Separates uncertain extraction from deterministic financial errors.
4. Prevents approval while blocking problems remain.
5. Records what changed, why a decision was made, and when it happened.

## 4. Goals and success measures

### 4.1 Product goals

- Accept a PDF invoice and create a reviewable invoice record.
- Extract the defined invoice fields and retain confidence metadata.
- Identify missing, inconsistent, duplicate, and financially invalid information using backend rules.
- Let a reviewer correct extracted values without losing their originals.
- Enforce valid approval and rejection transitions on the server.
- Preserve a complete local audit history and source document.
- Deliver a polished, responsive desktop-first interface suitable for a professional portfolio demonstration.

### 4.2 MVP success measures

The MVP is successful when:

- A reviewer can complete the happy-path workflow—upload, review, correct, revalidate, and approve—without leaving the application.
- Every uploaded valid PDF receives either a reviewable record or a clear processing error.
- All approval-blocking rules are evaluated by the backend and cannot be bypassed through client-side changes.
- Every manual field correction records the original value, new value, and timestamp.
- Every validation and decision event is visible in invoice history.
- Duplicate supplier/invoice-number combinations are surfaced before approval.
- The application survives restart without losing invoice records, PDFs, corrections, or history.
- Automated tests cover the core validation and workflow rules plus one complete end-to-end approval journey.

### 4.3 Initial operational metrics

Because the MVP runs locally and has no production telemetry service, the application should expose enough structured logs and persisted data to evaluate:

- Number of invoices by status.
- Number of invoices with warnings or blocking errors.
- Number and percentage of manually corrected extracted fields.
- Number of rejected and approved invoices.
- Processing failures by stage: upload, PDF extraction, OCR, AI extraction, parsing, or persistence.

No target accuracy or time-saved claim is part of the MVP until it has been measured against a representative invoice set.

## 5. Users and primary use cases

### 5.1 Primary persona: accounts-payable reviewer

The reviewer receives supplier invoices, verifies the extracted information against the source, resolves exceptions, and makes the final approval or rejection decision. The reviewer values speed, precise financial formatting, clear error severity, and a reliable record of changes.

### 5.2 Secondary persona: portfolio evaluator or developer

The evaluator runs the application locally, uploads representative invoices, examines the end-to-end workflow, and reviews the architecture, validation behavior, and test coverage.

### 5.3 Core jobs to be done

- When I receive a PDF invoice, I want its key fields prefilled so that I do not enter everything manually.
- When an extracted value is uncertain, I want to compare it directly with the PDF so that I can correct it quickly.
- When amounts or dates are inconsistent, I want a precise explanation so that I know what must be resolved.
- When I change a value, I want the original retained so that the review remains auditable.
- When an invoice is ready, I want to approve it confidently, knowing that blocking checks have passed.
- When an invoice should not proceed, I want to reject it with a reason and preserve that decision.

## 6. Scope

### 6.1 In scope for MVP

- Upload of one PDF invoice at a time.
- File-type, file-size, and basic PDF integrity checks.
- Secure local storage of source PDFs.
- Native PDF text extraction with OCR fallback when usable text is unavailable.
- AI-assisted extraction into a defined invoice schema.
- Field-level extraction confidence and source classification.
- Server-side deterministic validation.
- Duplicate detection against locally stored invoices.
- Searchable and filterable invoice queue.
- Two-panel PDF and structured-data review workspace.
- Manual draft edits and field-level correction tracking.
- Explicit revalidation.
- Approval and rejection with backend-enforced status transitions.
- Completed invoice view, history, source PDF access, and JSON export.
- SQLite persistence and structured local logs.
- OpenAPI documentation and a generated TypeScript client where practical.
- A single startup path for the finished local demonstration.

### 6.2 Out of scope for MVP

- User accounts, authentication, authorization roles, or multi-user concurrency.
- Cloud deployment, containers, distributed storage, or hosted databases.
- Email inbox monitoring or automated invoice ingestion.
- Purchase-order, goods-receipt, or contract matching.
- Supplier master-data management or bank-detail verification.
- Tax-jurisdiction-specific compliance decisions.
- Foreign-exchange conversion.
- Posting or synchronization with an ERP or accounting system.
- Batch upload, background queues, message brokers, schedulers, or SignalR updates.
- Mobile-specific workflows.
- Model training, autonomous approval, or AI-generated financial rules.
- Editing or annotating the source PDF.

## 7. Product principles

1. **AI proposes; rules verify; humans decide.** Extraction confidence must never substitute for deterministic validation or reviewer approval.
2. **The source remains visible.** Review decisions should be made with immediate access to the original PDF.
3. **Financial rules live on the server.** The client can guide the user but cannot establish an approvable state.
4. **Corrections do not erase history.** Original extracted values and later reviewer changes remain traceable.
5. **Exceptions are specific and actionable.** Messages identify the affected field, severity, and required resolution.
6. **Local-first means durable and self-contained.** The application must retain data across restarts without external infrastructure.

## 8. Invoice data model

### 8.1 Core invoice fields

The extraction and review experience must support at least the following fields:

| Group | Field | Required for approval | Notes |
| --- | --- | --- | --- |
| Supplier | Supplier name | Yes | Trimmed, human-readable name |
| Supplier | Supplier tax or registration ID | No | Preserve as text, including leading zeroes |
| Reference | Invoice number | Yes | Preserve punctuation and leading zeroes |
| Reference | Purchase-order number | No | Informational in MVP |
| Dates | Invoice date | Yes | Stored as a date without time-zone conversion |
| Dates | Due date | No | Required only when payment terms cannot determine it |
| Dates | Payment terms | No | Free text plus normalized days when confidently identified |
| Amounts | Currency | Yes | ISO 4217 code when it can be normalized |
| Amounts | Subtotal / net amount | Yes | Decimal monetary value |
| Amounts | Tax amount | Yes | Zero is valid |
| Amounts | Total / gross amount | Yes | Decimal monetary value |
| Review | Review notes | No | Reviewer-entered notes |
| Decision | Rejection reason | Required on rejection | Reviewer-entered explanation |

The data model may retain additional extraction metadata, but additional fields must not be presented as required unless a corresponding backend rule exists.

### 8.2 Field provenance

For every extractable field, the backend should retain:

- Current value.
- Original extracted value.
- Value source: native text, OCR, AI inference, or reviewer.
- Confidence score when supplied by the extraction provider.
- Whether the current value differs from the original.
- Most recent correction timestamp.

The MVP may use an overall field confidence category in the UI:

- High: `>= 0.90`
- Medium: `>= 0.70` and `< 0.90`
- Low: `< 0.70`
- Unknown: the provider did not return a usable score

These thresholds are presentation defaults, not financial validation rules, and should be configurable rather than duplicated across frontend components.

### 8.3 Money handling

- Monetary values must use decimal types, never binary floating-point types.
- The application must preserve the extracted currency separately from amounts.
- Validation calculations must use a currency-aware tolerance. The MVP default is `0.01` for two-decimal currencies and should be configurable.
- The UI must display monetary values with tabular numerals and consistent currency formatting.

## 9. Invoice lifecycle

### 9.1 Statuses

| Status | Meaning |
| --- | --- |
| Processing | The document is being stored, read, and extracted. |
| Review required | Extraction completed and the invoice is available for review. |
| Ready for approval | The latest draft has been validated and has no blocking errors. |
| Approved | A reviewer approved the latest validated version. Terminal in the MVP. |
| Rejected | A reviewer rejected the invoice with a reason. Terminal in the MVP. |
| Processing failed | A stage of document processing failed and the error is available for inspection. |

Warnings do not prevent `Ready for approval`; blocking errors do. An approved or rejected record is read-only in the MVP.

### 9.2 State transitions

```text
Upload
  └── Processing
        ├── Processing failed
        └── Review required
              ├── Save edit → Review required
              ├── Validate with errors → Review required
              ├── Validate without errors → Ready for approval
              ├── Reject → Rejected
              └── Ready for approval
                    ├── Save edit → Review required
                    ├── Approve → Approved
                    └── Reject → Rejected
```

Transition requirements:

- Every draft edit invalidates the prior validation result and returns the record to `Review required`.
- Approval is allowed only from `Ready for approval` and only when validation corresponds to the current persisted draft version.
- Rejection is allowed from `Review required` or `Ready for approval` and requires a non-empty reason.
- Terminal records cannot be edited, validated again, approved again, or rejected again in the MVP.
- The backend must reject illegal transitions with a consistent Problem Details response.

## 10. End-to-end user journey

### 10.1 Upload and processing

1. The reviewer selects a PDF from the invoice queue.
2. The client performs convenience checks and sends the file to the backend.
3. The backend validates the upload, stores it, creates an invoice record, and starts synchronous processing within the request.
4. The application extracts native text. If insufficient usable text is found, it invokes OCR.
5. The normalized document text is sent to the configured AI extraction provider.
6. The provider response is validated against the expected schema.
7. The backend stores extracted values and confidence metadata, runs initial validation, and returns the created invoice.
8. The UI opens the review workspace or provides a clear route to it.

If processing fails after the PDF has been accepted, the record should remain visible as `Processing failed` with a safe, actionable error. Internal provider responses, file paths, and stack traces must not be exposed to the client.

### 10.2 Review and correction

1. The reviewer sees the PDF on the left and grouped fields on the right.
2. Low- and medium-confidence fields and validation exceptions are visually discoverable without obscuring the document.
3. The reviewer edits one or more values and may enter review notes.
4. Saving persists a new draft version and records changed fields.
5. The reviewer selects **Revalidate**.
6. The backend validates the current persisted draft and returns the new status and results.
7. The UI updates the review summary and focuses the first blocking error when appropriate.

### 10.3 Approval

1. Approval is visibly enabled only when the invoice is `Ready for approval`.
2. The reviewer confirms approval.
3. The backend verifies the current version and status again before committing the decision.
4. The invoice becomes read-only and displays its decision timestamp, final values, validation result, corrections, and history.

### 10.4 Rejection

1. The reviewer selects **Reject**.
2. The UI requires a rejection reason.
3. The backend validates the transition and reason.
4. The invoice becomes read-only and displays the reason, timestamp, final draft, and history.

## 11. Functional requirements

### 11.1 Upload and document handling

| ID | Requirement | Acceptance criteria |
| --- | --- | --- |
| FR-001 | The system shall accept one invoice PDF per upload. | A valid PDF creates exactly one invoice record and returns its identifier. |
| FR-002 | The backend shall validate extension, declared content type, file signature, non-empty content, and configured maximum size. | A failing file is rejected with a `400` or `413` Problem Details response and creates no misleading reviewable invoice. |
| FR-003 | The system shall store the original PDF under an application-managed identifier rather than trusting the uploaded filename as a path. | A stored document can be retrieved only through its invoice document endpoint; traversal-style filenames cannot escape storage. |
| FR-004 | The system shall preserve the original display filename as metadata. | The completed record shows or exports the original filename without using it as the storage key. |
| FR-005 | The system shall return the stored PDF for inline viewing. | The document endpoint returns the correct PDF with an inline-safe content disposition. |

### 11.2 Extraction

| ID | Requirement | Acceptance criteria |
| --- | --- | --- |
| FR-010 | The system shall attempt native PDF text extraction first. | A text-based invoice can be processed without invoking OCR. |
| FR-011 | The system shall use OCR when extracted text is absent or below a configured usability threshold. | A supported scanned invoice reaches AI extraction when OCR succeeds. |
| FR-012 | The system shall send normalized document text to a configurable AI extraction provider. | Provider-specific behavior is isolated behind a backend interface. |
| FR-013 | The system shall validate AI output before mapping it into the domain model. | Malformed or schema-invalid responses produce a controlled processing failure and do not persist fabricated field values. |
| FR-014 | The system shall retain available confidence and provenance metadata per field. | The invoice detail response can distinguish confidence and current value source for each supported field. |
| FR-015 | The system shall distinguish a missing extracted value from a zero monetary value. | A tax amount of zero is retained as zero; an absent amount is represented as missing. |

### 11.3 Invoice queue

| ID | Requirement | Acceptance criteria |
| --- | --- | --- |
| FR-020 | The landing page shall show counts for pending review, warnings, errors, and approved invoices. | Counts are derived from persisted backend state and update after upload or decision actions. |
| FR-021 | The queue shall show a compact row for each invoice. | Each row includes supplier, invoice number, invoice date, total, currency, status, exception count, and last-updated time when available. |
| FR-022 | The reviewer shall be able to search by supplier name or invoice number. | Search is case-insensitive and returns matching records without loading document bodies. |
| FR-023 | The reviewer shall be able to filter by status. | Applying and clearing a status filter updates the visible queue and preserves an understandable empty state. |
| FR-024 | The queue shall provide loading, no-invoice, no-search-result, and API-error states. | Each state presents a clear next action and does not display stale data as current. |

### 11.4 Review workspace

| ID | Requirement | Acceptance criteria |
| --- | --- | --- |
| FR-030 | The review page shall present a two-panel desktop layout with PDF preview and invoice fields. | Both panels can be inspected without navigating away; narrower windows remain usable through a responsive stacked or tabbed fallback. |
| FR-031 | Fields shall be grouped into supplier, reference, dates and terms, amounts, and review information. | All fields in the core invoice schema appear in a predictable group. |
| FR-032 | The UI shall display confidence without implying that confidence is validation. | Low or unknown confidence is visible at field level; validation severity uses a separate visual treatment and label. |
| FR-033 | The reviewer shall be able to edit all non-system invoice fields before a terminal decision. | Valid edits can be saved and reloaded with no loss of precision or formatting-significant identifiers. |
| FR-034 | Corrected fields shall disclose the original extracted value. | After save, a changed field is marked as corrected and its original value is accessible in context. |
| FR-035 | The page shall display inline validation messages and an aggregate review summary. | The summary shows extracted field count, warning count, blocking error count, and manual correction count based on backend data. |
| FR-036 | The reviewer shall be able to revalidate the current saved draft. | Revalidation refreshes results and status without re-running document extraction. |
| FR-037 | Unsaved changes shall not be silently discarded. | Navigation or decision attempts with dirty form state prompt the reviewer or save through an explicit action. |

### 11.5 Drafts, decisions, and completed records

| ID | Requirement | Acceptance criteria |
| --- | --- | --- |
| FR-040 | Saving a draft shall persist changes and a monotonically increasing version. | The API returns the saved version, and subsequent reads return the same values. |
| FR-041 | The backend shall prevent lost updates. | Updating a stale version returns a conflict response and does not overwrite the newer draft. |
| FR-042 | Any saved business-field edit shall invalidate prior validation for approval purposes. | A previously ready invoice returns to `Review required` after an amount, date, supplier, reference, or currency change. |
| FR-043 | Approval shall be enforced by the backend. | Approval of an unvalidated, stale, errored, rejected, or already approved invoice fails with a Problem Details response. |
| FR-044 | Rejection shall require a reason. | Empty or whitespace-only reasons are rejected; a successful rejection stores the reason and time. |
| FR-045 | The completed record shall show final values, status, latest validation, corrections, decision details, notes, history, and source-document access. | An approved or rejected invoice can be fully inspected after application restart. |
| FR-046 | The reviewer shall be able to export the structured invoice record as JSON. | Export contains stable field names, final values, currency, status, validation summary, corrections, and decision metadata, but not the PDF bytes. |

### 11.6 Audit history

| ID | Requirement | Acceptance criteria |
| --- | --- | --- |
| FR-050 | The system shall append an audit event for material lifecycle actions. | Upload, extraction completion/failure, draft save, validation, approval, and rejection each create a timestamped event. |
| FR-051 | Draft-save events shall identify changed fields without erasing prior values. | History can show field name, previous value, and new value for each manual correction. |
| FR-052 | Audit timestamps shall use UTC in persistence and include an offset or explicit UTC marker in API output. | Events sort consistently and display in the local UI without ambiguity. |
| FR-053 | Audit events shall be immutable through the public API. | No MVP endpoint can edit or delete a history event. |

## 12. Validation requirements

Validation results must be returned as structured data containing at least:

- Stable rule code.
- Severity: warning or error.
- Human-readable message.
- Related field or fields when applicable.
- Current validation timestamp.
- Invoice draft version that was validated.

### 12.1 Initial rule set

| Rule code | Rule | Severity | Expected behavior |
| --- | --- | --- | --- |
| `REQUIRED_FIELD_MISSING` | A field required for approval is absent. | Error | Identify each missing required field. |
| `AMOUNT_RECONCILIATION_FAILED` | Subtotal plus tax does not equal total within the configured currency tolerance. | Error | Show expected and actual totals without changing the values automatically. |
| `NEGATIVE_AMOUNT_UNEXPECTED` | Subtotal, tax, or total is negative. | Warning | Flag for review; do not assume credit-note handling in the MVP. |
| `DUE_DATE_BEFORE_INVOICE_DATE` | Due date precedes invoice date. | Error | Relate the result to both date fields. |
| `PAYMENT_TERMS_MISMATCH` | A normalized payment term and explicit due date disagree with the invoice date. | Warning | Report the calculated due date and supplied due date. |
| `POSSIBLE_DUPLICATE_INVOICE` | Another non-failed record has the same normalized supplier name and invoice number. | Error | Include the matching invoice identifier and status. Do not compare an invoice to itself during revalidation. |
| `CURRENCY_INVALID` | Currency is absent or cannot be normalized to an allowed ISO code. | Error | Require reviewer correction before approval. |
| `LOW_EXTRACTION_CONFIDENCE` | A populated required field has low or unknown extraction confidence and has not been confirmed or corrected by a reviewer. | Warning | Highlight the field but do not block approval solely because of AI confidence. |
| `INVOICE_DATE_IN_FUTURE` | Invoice date is later than the local current date. | Warning | Allow approval after reviewer consideration. |

### 12.2 Rule behavior

- Validation rules must be deterministic and testable without calling OCR or AI services.
- Rule execution order must not affect the final result set.
- Revalidation replaces the prior current result set while retaining a history event for the run.
- The backend determines severity and approval eligibility.
- Warnings may remain when an invoice is approved; errors may not.
- Duplicate matching must normalize surrounding whitespace and letter casing but retain original display values.
- The MVP must not infer tax compliance, supplier legitimacy, or payment authorization from the rule results.

## 13. API requirements

The initial REST API consists of:

```text
POST   /api/invoices
GET    /api/invoices
GET    /api/invoices/{id}
GET    /api/invoices/{id}/document
PUT    /api/invoices/{id}/draft
POST   /api/invoices/{id}/validate
POST   /api/invoices/{id}/approve
POST   /api/invoices/{id}/reject
GET    /api/invoices/{id}/history
```

### 13.1 Endpoint behavior

- `POST /api/invoices` accepts multipart form data containing one PDF, processes it, and returns the created invoice detail. If typical processing time approaches infrastructure timeouts, the implementation should preserve the API contract behind a future asynchronous extension rather than introducing a queue prematurely.
- `GET /api/invoices` supports search, status filter, pagination, and deterministic sorting. The default sort is most recently updated first.
- `GET /api/invoices/{id}` returns the review or completed-record representation, including fields, confidence, corrections, current validation, status, and version.
- `GET /api/invoices/{id}/document` streams the source PDF.
- `PUT /api/invoices/{id}/draft` accepts editable values, review notes, and expected version; it returns the updated record and version.
- `POST /api/invoices/{id}/validate` validates the latest persisted draft and returns its new status and structured results.
- `POST /api/invoices/{id}/approve` accepts the expected draft version and confirms the final decision.
- `POST /api/invoices/{id}/reject` accepts the expected draft version and rejection reason.
- `GET /api/invoices/{id}/history` returns audit events in a documented, deterministic order.

### 13.2 Contract and error standards

- API contracts must remain separate from EF Core persistence entities.
- ASP.NET Core must publish an OpenAPI definition.
- The TypeScript client and shared API types should be generated from OpenAPI where practical.
- Validation, domain, conflict, unsupported-media, not-found, and unexpected errors must use consistent Problem Details responses.
- Problem Details extensions may include a stable error code, affected fields, and current record version.
- Error responses must not expose stack traces, provider secrets, raw AI prompts, or application-local storage paths.

## 14. User experience requirements

### 14.1 Global visual direction

- Use deep navy or ink-colored navigation with warm white and light slate work surfaces.
- Use a restrained blue accent, emerald for approved states, amber for warnings, and red only for blocking errors or destructive emphasis.
- Prefer thin borders, subtle shadows, compact tables, and modest corner radii.
- Use tabular numerals for amounts and dates where appropriate.
- Maintain strong hierarchy without oversized headings or excessive whitespace.
- Avoid decorative gradients, glass effects, chatbot patterns, marketing illustrations, and distracting motion.
- Present AI as field provenance and extraction assistance, not as the product's visual identity.

### 14.2 Invoice queue

The landing screen must include:

- Summary cards for pending review, warnings, errors, and approved invoices.
- Supplier or invoice-number search.
- Status filters.
- Compact invoice table.
- Prominent upload action.
- Visible exception counts.
- Loading, empty, no-results, and failure states.

The queue should prioritize scan speed. Status, supplier, reference, total, and exceptions should be legible without opening a record.

### 14.3 Review workspace

The desktop layout must allocate substantial space to both the PDF and the form. The user should be able to resize panels if practical, and the chosen PDF zoom/page state should remain stable while editing.

The right panel must include:

- Supplier and reference fields.
- Invoice dates and payment terms.
- Monetary totals and currency.
- Separate confidence and validation indicators.
- Original-value disclosure for corrected fields.
- Persistent summary of extracted fields, warnings, errors, and corrections.
- Save, revalidate, reject, and approve actions with clear enabled and disabled states.

### 14.4 Accessibility and interaction

- All primary actions and form controls must be keyboard accessible.
- Every input must have a programmatically associated label.
- Status and validation must not rely on color alone.
- Focus must move predictably after validation, modal confirmation, and errors.
- Error summaries should link or move focus to affected fields.
- Contrast should meet WCAG 2.2 AA for normal text and essential controls.
- Loading actions must prevent accidental duplicate submission and communicate progress.
- Destructive or terminal decisions must require explicit confirmation.

## 15. Non-functional requirements

### 15.1 Performance

- Queue queries should return the first page within 500 ms on a typical development machine with 1,000 local invoice records, excluding cold startup.
- Draft save, validation, approval, and rejection should complete within 500 ms under the same local conditions, excluding external provider work.
- PDF pages and queue rows should render incrementally so large documents or lists do not freeze the interface.
- AI and OCR operations must use configurable timeouts and cancellation tokens.

### 15.2 Reliability and data integrity

- Database and document identifiers must remain consistent; a failed write must not leave an invoice falsely presented as complete.
- Approval must be committed atomically with its status and audit event.
- Draft updates must use optimistic concurrency.
- Application restart must preserve all committed records and documents.
- Schema evolution must use EF Core migrations.

### 15.3 Security and privacy

- The API must validate all input independently of the client.
- Development CORS must allow only the configured frontend origin.
- Production demonstration mode should serve the client and API from the same local origin.
- Uploaded content must never be executed.
- Stored filenames and paths must be generated by the application.
- API keys and provider configuration must be loaded from environment-specific configuration or user secrets and excluded from source control.
- Logs must avoid raw invoice text, full extracted records, and secrets by default.
- Document responses should include appropriate content-type and anti-sniffing headers.

Authentication is intentionally out of scope. The application must clearly be positioned as a single-user local demonstration and must not be exposed to an untrusted network in its MVP configuration.

### 15.4 Observability and diagnostics

- Use structured logging with a request or correlation identifier.
- Log processing-stage start, completion, duration, and safe failure category.
- Log status transitions and invoice identifiers without logging sensitive invoice content.
- Expose OpenAPI documentation in development.
- Startup failures caused by missing provider or storage configuration must be actionable.

### 15.5 Maintainability

- Domain validation and workflow rules must be independent of controllers, EF Core, OCR, and AI providers.
- External document and AI services must be accessed through interfaces suitable for test doubles.
- Frontend server state must use TanStack Query; editable draft state must use React Hook Form.
- Frontend components must not duplicate financial rules.
- Features should be grouped cohesively rather than accumulated in generic utility folders.

## 16. Technical constraints and architecture

### 16.1 Required stack

Frontend:

- React and TypeScript.
- Vite.
- React Router.
- TanStack Query.
- React Hook Form.
- Zod for client-side input-shape and usability validation.
- Tailwind CSS.
- A small set of accessible headless components.
- PDF.js-based document preview.

Backend:

- .NET 10.
- ASP.NET Core controller-based Web API.
- Entity Framework Core and SQLite.
- Built-in dependency injection.
- Problem Details.
- OpenAPI.
- Structured logging.

### 16.2 Logical architecture

```text
React + TypeScript
        │
        │ REST API / JSON
        ▼
ASP.NET Core Web API
        │
        ├── Invoice workflow and validation
        ├── PDF text extraction and OCR
        ├── AI extraction provider
        ├── Local document storage
        └── EF Core → SQLite
```

Suggested solution structure:

```text
InvoiceReviewAssistant.sln

src/
  InvoiceReviewAssistant.Api/
  InvoiceReviewAssistant.Core/
  InvoiceReviewAssistant.Infrastructure/
  invoice-review-client/

tests/
  InvoiceReviewAssistant.Core.Tests/
  InvoiceReviewAssistant.IntegrationTests/
  invoice-review-client tests/
```

### 16.3 Local execution

During development, Vite and ASP.NET Core run separately, with CORS restricted to the configured Vite origin.

For the finished demonstration:

- ASP.NET Core serves the React production build.
- Browser and API use one local origin.
- SQLite and PDFs stay on the local machine.
- One documented startup script launches the complete application.
- No cloud resources or containers are required.

## 17. Analytics and telemetry

No external analytics service is required. Product events needed for the local portfolio demonstration should be derivable from the audit history and structured logs.

The application must not send invoice contents to telemetry. Sending document text to the configured AI provider is part of processing and must be clearly documented in setup instructions, including which provider is used and how credentials are configured.

## 18. Testing and quality requirements

### 18.1 Backend unit tests

Tests must cover:

- Amount reconciliation within and outside tolerance.
- Required-field checks, including zero versus missing amounts.
- Due-date consistency.
- Payment-term mismatch.
- Duplicate detection and self-exclusion during revalidation.
- Status-transition rules.
- Approval restrictions for stale, invalid, and terminal records.
- Preservation of original values across one or more corrections.
- Currency normalization and invalid currencies.
- Invalid AI response handling.

### 18.2 Backend integration tests

Tests must cover:

- Valid PDF upload and retrieval.
- Invalid extension, signature, content type, empty file, and excessive size.
- Invoice persistence across API operations.
- Draft optimistic-concurrency conflict.
- Problem Details shape for representative failures.
- Approval transaction persistence, including audit history.

External OCR and AI providers should be replaced with deterministic test implementations in automated integration tests.

### 18.3 Frontend tests

Tests must cover:

- Queue rendering, filtering, empty state, and API failure state.
- Review field population and monetary formatting.
- Confidence and validation messages as distinct concepts.
- Editing and saving extracted values.
- Original-value display for corrections.
- Dirty-form navigation protection.
- Approval disabled or rejected when the invoice is not ready.
- Rejection-reason validation.

### 18.4 End-to-end test

At least one automated end-to-end scenario must cover:

```text
Upload → Review → Correct → Save → Revalidate → Approve → Inspect history
```

The scenario must verify that the corrected value is final, the original value remains visible, validation corresponds to the latest version, and the approved invoice is read-only.

## 19. Release acceptance criteria

The MVP is ready for demonstration when all of the following are true:

- A fresh local setup can be completed from documented prerequisites and configuration.
- One command or script launches the production-style local application.
- At least one text PDF and one scanned PDF can be processed through the intended extraction paths.
- The queue accurately summarizes and filters persisted invoices.
- The review workspace displays the correct source PDF beside editable extracted fields.
- Manual corrections retain original values and create audit events.
- All defined validation rules return structured, field-related results where applicable.
- Blocking errors prevent approval through both the UI and direct API requests.
- A valid current draft can be approved, and an invoice can be rejected with a reason.
- Approved and rejected records remain accessible and read-only after restart.
- JSON export and source PDF access work for completed records.
- Core, integration, frontend, and end-to-end test suites pass.
- No provider secret, invoice document, database, or generated local data is committed to source control.
- The interface follows the specified financial-operations visual direction and meets the listed accessibility requirements.

## 20. Delivery phases

### Phase 1: foundation

- Create solution and frontend structure.
- Configure SQLite, EF Core migrations, OpenAPI, Problem Details, and local storage.
- Define domain models, API contracts, statuses, and audit events.
- Establish generated TypeScript client workflow.

### Phase 2: ingestion and extraction

- Implement secure PDF upload and retrieval.
- Add native text extraction and OCR fallback.
- Add AI provider abstraction, schema validation, and deterministic test provider.
- Persist extracted fields, confidence, and processing failures.

### Phase 3: validation and workflow

- Implement validation rules and structured results.
- Add draft versioning and correction history.
- Enforce state transitions, approval, rejection, and optimistic concurrency.
- Complete backend unit and integration coverage.

### Phase 4: reviewer experience

- Build invoice queue, summary counts, search, and filtering.
- Build two-panel review workspace and PDF preview.
- Add editable groups, confidence, inline validation, original values, and review summary.
- Build completed-record, history, PDF access, and JSON export views.

### Phase 5: polish and demonstration readiness

- Add responsive and accessible interaction states.
- Add end-to-end coverage and representative test invoices.
- Verify production build served by ASP.NET Core.
- Add the single startup script and setup documentation.
- Perform visual, error-state, restart-persistence, and secret-handling reviews.

## 21. Risks and mitigations

| Risk | Impact | Mitigation |
| --- | --- | --- |
| AI output varies across invoice layouts. | Incorrect or missing fields. | Validate provider output, expose confidence and provenance, retain the PDF, require human review, and use deterministic test fixtures. |
| OCR quality is poor for scans. | Extraction failure or low accuracy. | Preserve processing-stage errors, surface low confidence, and allow complete manual correction. |
| Synchronous processing is slow. | Upload request may feel stalled or time out. | Show a clear processing state, enforce timeouts and cancellation, measure typical samples, and preserve a path to later asynchronous processing. |
| Duplicate logic creates false positives. | Valid invoices may be blocked. | Show the matched record, normalize narrowly, and let reviewers reject or correct data; refine rules only with evidence. |
| Local files and database drift apart. | Missing documents or orphaned records. | Use generated storage IDs, transactional metadata updates where possible, integrity checks, and explicit failure states. |
| No authentication is misunderstood as production-ready. | Unsafe exposure of invoice data. | Bind to local interfaces by default, document the single-user boundary, and avoid claims of production deployment readiness. |
| Client and server contracts drift. | Runtime failures and inconsistent behavior. | Generate the TypeScript client from OpenAPI and verify generation in the build or CI workflow. |

## 22. Assumptions and open decisions

### 22.1 Assumptions used in this PRD

- The MVP is a single-user, local application.
- English-language invoices and Latin-script OCR are the initial demonstration baseline.
- Each uploaded PDF represents one invoice.
- A reviewer may approve an invoice with warnings but never with blocking errors.
- Credit notes are not a first-class workflow; negative amounts generate a warning.
- The extraction provider and OCR engine will be selected during implementation and accessed through replaceable interfaces.
- File-size and text-usability thresholds will be configuration values, not hard-coded product policy.

### 22.2 Decisions to make before extraction implementation

- Which OCR engine will be the default local implementation?
- Which AI extraction provider and model will be supported first?
- What maximum PDF size and page count should the demonstration accept?
- Should encrypted PDFs be rejected uniformly, or should password entry be supported in a later release?
- Does a low-confidence field need an explicit reviewer confirmation control, or is saving/approving the reviewed record sufficient acknowledgement?
- Which currencies beyond common two-decimal currencies must be included in acceptance fixtures?

These decisions should not alter the core boundary: extraction providers propose structured values, while the backend owns financial rules and the human owns the final decision.

## 23. Future extensions

Potential post-MVP capabilities include:

- Asynchronous batch processing and progress updates.
- Authentication, roles, and named reviewers.
- Purchase-order and goods-receipt matching.
- Supplier master-data checks and configurable approval policies.
- ERP or accounting-system export and synchronization.
- Email or watched-folder ingestion.
- Credit-note-specific workflows.
- Configurable tax and jurisdiction rules.
- Multi-currency reporting and exchange-rate handling.
- Operational dashboards and production telemetry.

These are intentionally excluded from the MVP until the core review, validation, and audit workflow is proven.
