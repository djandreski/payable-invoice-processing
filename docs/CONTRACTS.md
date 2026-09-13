# Invoice Review Assistant: Contracts

| Document attribute | Value |
| --- | --- |
| Status | Accepted for MVP implementation |
| Applies to | Invoice Review Assistant MVP |
| Contract version | HTTP v1, extraction schema `1.0`, export schema `1.0` |
| Requirements source | [`PRD.md`](../PRD.md) |
| Implementation decisions | [`docs/DECISIONS.md`](DECISIONS.md) |
| Architecture | [`ARCHITECTURE.md`](../ARCHITECTURE.md) |

## 1. Purpose and authority

This document is the implementation authority for:

- Public HTTP routes, parameters, request bodies, response bodies, and status codes.
- JSON naming, primitive serialization, nullability, and enum values.
- Pagination, deterministic ordering, and queue-summary semantics.
- Optimistic-concurrency request and conflict behavior.
- Problem Details extensions and stable HTTP error codes.
- Audit-event wire types and their event-specific details.
- The internal `invoice_extraction_v1` JSON Schema supplied to the AI extraction provider.
- The terminal invoice JSON export format.

`PRD.md` remains authoritative for product behavior. `docs/DECISIONS.md` remains authoritative for accepted implementation choices. `ARCHITECTURE.md` remains authoritative for dependency, domain, persistence, transaction, storage, provider, runtime, and frontend boundaries. This document makes those decisions concrete at the contract boundary and must not be used to weaken them.

API DTOs are separate from EF Core entities and Core domain objects. The generated TypeScript client is generated from OpenAPI; handwritten frontend code must not recreate these wire types.

## 2. General wire rules

### 2.1 HTTP and JSON

- JSON request and response bodies use UTF-8 and `application/json`.
- Problem responses use `application/problem+json`.
- JSON property names use `camelCase`.
- Enum values are strings. Integer enum values are rejected.
- Enum values use lower camel case except the already-established validation, Problem Details, and processing-failure codes, which use uppercase snake case.
- UUIDs use lowercase RFC 4122 `D` format: `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`.
- Unknown JSON properties are rejected with `400 REQUEST_VALIDATION_FAILED`.
- JSON property-name matching is case-sensitive.
- Every declared response property is emitted. A missing nullable value is serialized as explicit `null`.
- Collections are never `null`. An empty collection is serialized as `[]`.
- Request objects must contain every declared property, including nullable business values. Omission is a malformed request; `null` is an intentional absent value.
- Response examples are normative for names, nesting, primitive representation, and nullability. Human-readable messages are illustrative unless stated otherwise.

The API does not use URL versioning in the MVP. The route prefix is `/api`.

### 2.2 Primitive representations

| Contract type | JSON type | Exact representation |
| --- | --- | --- |
| `Uuid` | string | Lowercase RFC 4122 `D` format |
| `SequenceId` | string | Unsigned base-10 integer with no leading zeroes except `"0"` |
| `Date` | string | `yyyy-MM-dd`, parsed as `DateOnly` |
| `UtcTimestamp` | string | `yyyy-MM-ddTHH:mm:ss.fffZ`; always UTC |
| `Money` | string | Invariant two-decimal value matching `^-?(?:0\|[1-9]\d{0,26})\.\d{2}$` and fitting C# `decimal` |
| `Confidence` | number or null | Inclusive range `0` through `1`; binary floating point is acceptable because this is metadata, not money |
| `DraftVersion` | integer | C# `int`, minimum `1` |
| Count | integer | C# `int`, minimum `0` |

Money never uses JSON numbers, exponent notation, digit separators, currency symbols, or a leading plus sign. Negative values are structurally valid so the deterministic `NEGATIVE_AMOUNT_UNEXPECTED` rule can report them. `-0.00` is normalized to `0.00` in responses.

All four initially supported currencies have two-decimal behavior. This contract must be revised before enabling a currency whose minor-unit behavior is not two decimals.

### 2.3 Input normalization

The API performs the following normalization after structural deserialization and before a use case runs:

- Trim leading and trailing whitespace from draft strings and rejection reasons.
- Convert a blank draft string to `null`. A required-for-approval value may therefore be saved as `null` and later reported by deterministic validation; it is not an HTTP shape error.
- Preserve punctuation, internal whitespace, casing, and leading zeroes in supplier registration IDs, invoice numbers, and purchase-order numbers.
- Normalize a non-blank currency candidate to invariant uppercase. Unsupported or unrecognized values remain draft data and produce `CURRENCY_INVALID` during invoice validation.
- Do not alter review-note or rejection-reason internal whitespace.
- Reject a blank rejection reason with `400 REJECTION_REASON_REQUIRED`.
- Reject malformed dates, money, enum values, UUIDs, versions, query integers, and object shapes with `400 REQUEST_VALIDATION_FAILED`.

## 3. Enum catalog

These values are closed for HTTP v1. Adding or renaming a value is a contract change.

### 3.1 Invoice and field enums

| Enum | Values |
| --- | --- |
| `InvoiceStatus` | `processing`, `reviewRequired`, `readyForApproval`, `approved`, `rejected`, `processingFailed` |
| `InvoiceFieldKey` | `supplierName`, `supplierRegistrationId`, `invoiceNumber`, `purchaseOrderNumber`, `invoiceDate`, `dueDate`, `paymentTerms`, `currency`, `subtotal`, `taxAmount`, `total`, `reviewNotes` |
| `FieldSource` | `nativeText`, `ocr`, `aiInference`, `reviewer` |
| `DocumentTextSource` | `nativeText`, `ocr` |
| `ConfidenceBand` | `high`, `medium`, `low`, `unknown` |
| `DecisionKind` | `approved`, `rejected` |
| `DocumentIntegrityStatus` | `available`, `missing`, `corrupt` |

`reviewNotes` is a correction/audit field key but is not an extractable field. `normalizedPaymentTermsDays` is derived, read-only, and is not an `InvoiceFieldKey`.

Confidence bands are derived by the backend:

- `high`: confidence greater than or equal to `0.90`.
- `medium`: confidence greater than or equal to `0.70` and less than `0.90`.
- `low`: confidence less than `0.70`.
- `unknown`: confidence is `null`.

Confidence bands are presentation metadata, not approval rules.

### 3.2 Validation enums

`ValidationSeverity` contains `warning` and `error`.

`ValidationCode` contains exactly:

- `REQUIRED_FIELD_MISSING`
- `AMOUNT_RECONCILIATION_FAILED`
- `NEGATIVE_AMOUNT_UNEXPECTED`
- `DUE_DATE_BEFORE_INVOICE_DATE`
- `PAYMENT_TERMS_MISMATCH`
- `POSSIBLE_DUPLICATE_INVOICE`
- `CURRENCY_INVALID`
- `LOW_EXTRACTION_CONFIDENCE`
- `INVOICE_DATE_IN_FUTURE`

`ValidationTrigger` contains `initial`, `explicit`, and `approval`.

### 3.3 Processing and query enums

| Enum | Values |
| --- | --- |
| `ProcessingStage` | `upload`, `pdfExtraction`, `ocr`, `aiExtraction`, `parsing`, `persistence`, `startupRecovery` |
| `AuditActor` | `system`, `reviewer` |
| `InvoiceSort` | `updatedAtDesc`, `updatedAtAsc`, `createdAtDesc`, `createdAtAsc` |

The stable persisted processing-failure codes are:

| Code | Normal stage | Meaning |
| --- | --- | --- |
| `PDF_EXTRACTION_FAILED` | `pdfExtraction` | Native extraction and the usable fallback path could not produce document text |
| `PDF_RENDER_FAILED` | `ocr` | A PDF page could not be rasterized for OCR |
| `OCR_UNAVAILABLE` | `ocr` | The configured OCR executable or language data was unavailable while processing |
| `OCR_PAGE_TIMEOUT` | `ocr` | One page exceeded its OCR deadline |
| `OCR_DOCUMENT_TIMEOUT` | `ocr` | The whole document exceeded its OCR deadline |
| `OCR_FAILED` | `ocr` | OCR failed for a classified, reviewer-safe reason not covered above |
| `AI_TIMEOUT` | `aiExtraction` | AI extraction exceeded its overall deadline |
| `AI_UNAVAILABLE` | `aiExtraction` | The provider could not be reached or returned a transient service failure after the configured retry |
| `AI_REFUSED` | `aiExtraction` | The provider returned a refusal instead of an extraction object |
| `AI_RESPONSE_INCOMPLETE` | `aiExtraction` | The provider response ended before a complete output object was produced |
| `AI_RESPONSE_INVALID` | `parsing` | Output was malformed, schema-invalid, or failed post-schema semantic checks |
| `PROCESS_INTERRUPTED` | `startupRecovery` | Startup reconciliation found an unfinished `processing` record |
| `PROCESSING_FAILED` | varies | Safe fallback for an otherwise classified post-acceptance processing failure |

These codes are data on a persisted `processingFailed` invoice returned with `201 Created` after durable acceptance. They are distinct from HTTP Problem Details codes.

## 4. Shared response DTOs

### 4.1 Field wrappers

OpenAPI publishes three concrete schemas rather than an open generic:

- `TextFieldDto`: `value` and `originalValue` are JSON strings or `null`.
- `DateFieldDto`: `value` and `originalValue` are `Date` or `null`.
- `MoneyFieldDto`: `value` and `originalValue` are `Money` or `null`.

Every wrapper has the same remaining properties:

| Property | Type | Nullable | Meaning |
| --- | --- | --- | --- |
| `value` | type-specific | yes | Current persisted draft value |
| `originalValue` | type-specific | yes | Immutable original extraction value |
| `originalSource` | `FieldSource` | no | Source assigned when extraction completed |
| `currentSource` | `FieldSource` | no | `reviewer` after a manual change; otherwise the original source |
| `confidence` | `Confidence` | yes | Provider confidence retained after corrections |
| `confidenceBand` | `ConfidenceBand` | no | Backend-derived band |
| `differsFromOriginal` | boolean | no | Canonical comparison of current and original values |
| `lastCorrectedAt` | `UtcTimestamp` | yes | Most recent actual reviewer correction |

For the initial AI pipeline, `originalSource` and initial `currentSource` are `aiInference`. `nativeText` and `ocr` remain valid `FieldSource` values for provider-neutral compatibility, while `documentTextSource` separately records how the text supplied to the AI provider was produced.

### 4.2 InvoiceFieldsDto

`InvoiceFieldsDto` is grouped as follows:

| Group/property | Type |
| --- | --- |
| `supplier.name` | `TextFieldDto` |
| `supplier.registrationId` | `TextFieldDto` |
| `reference.invoiceNumber` | `TextFieldDto` |
| `reference.purchaseOrderNumber` | `TextFieldDto` |
| `datesAndTerms.invoiceDate` | `DateFieldDto` |
| `datesAndTerms.dueDate` | `DateFieldDto` |
| `datesAndTerms.paymentTerms` | `TextFieldDto` |
| `datesAndTerms.normalizedPaymentTermsDays` | integer or `null`; minimum `0` |
| `amounts.currency` | `TextFieldDto` |
| `amounts.subtotal` | `MoneyFieldDto` |
| `amounts.taxAmount` | `MoneyFieldDto` |
| `amounts.total` | `MoneyFieldDto` |

`normalizedPaymentTermsDays` is derived by the backend from `paymentTerms`. The client displays it but cannot submit it.

### 4.3 InvoiceDocumentDto

| Property | Type | Nullable |
| --- | --- | --- |
| `originalFilename` | string | no |
| `mediaType` | string, always `application/pdf` | no |
| `byteLength` | integer | no |
| `sha256` | 64-character lowercase hexadecimal string | no |
| `pageCount` | integer | no |
| `integrityStatus` | `DocumentIntegrityStatus` | no |

The document DTO never exposes a storage key or local path.

### 4.4 ProcessingFailureDto

| Property | Type | Nullable |
| --- | --- | --- |
| `stage` | `ProcessingStage` | no |
| `code` | processing-failure code | no |
| `message` | reviewer-safe string | no |
| `failedAt` | `UtcTimestamp` | no |

`message` must not contain exception text, command lines, prompts, document text, provider output, credentials, or paths.

### 4.5 InvoiceDecisionDto

| Property | Type | Nullable |
| --- | --- | --- |
| `kind` | `DecisionKind` | no |
| `decidedAt` | `UtcTimestamp` | no |
| `rejectionReason` | string | yes |

`rejectionReason` is `null` when `kind` is `approved` and non-null when `kind` is `rejected`.

### 4.6 FieldCorrectionDto

| Property | Type | Nullable |
| --- | --- | --- |
| `id` | `SequenceId` | no |
| `auditEventId` | `SequenceId` | no |
| `field` | `InvoiceFieldKey` | no |
| `previousValue` | string | yes |
| `newValue` | string | yes |
| `draftVersion` | `DraftVersion` | no |
| `occurredAt` | `UtcTimestamp` | no |

Correction values use the canonical wire form for their field: dates and money remain strings, identifiers retain significant formatting, and `null` remains distinct from `"0.00"`. Corrections are ordered by `occurredAt` and then numeric `id` ascending.

### 4.7 InvoiceSummaryDto

| Property | Type | Meaning |
| --- | --- | --- |
| `extractedFieldCount` | count | Number of the eleven extractable fields whose immutable `originalValue` is non-null |
| `warningCount` | count | Warning results in the current validation run, or zero when no current run exists |
| `errorCount` | count | Error results in the current validation run, or zero when no current run exists |
| `manualCorrectionCount` | count | Total persisted field corrections, including review-note changes |

## 5. Validation DTOs

### 5.1 ValidationRunDto

| Property | Type | Nullable |
| --- | --- | --- |
| `id` | `Uuid` | no |
| `draftVersion` | `DraftVersion` | no |
| `validatedAt` | `UtcTimestamp` | no |
| `warningCount` | count | no |
| `errorCount` | count | no |
| `results` | `ValidationResultDto[]` | no |

Results are ordered by severity (`error` before `warning`), configured rule order, first related field key, and then deterministic data key. A result contains:

- `code`: the discriminating `ValidationCode`.
- `severity`: fixed by the PRD rule table.
- `message`: reviewer-facing text.
- `fields`: ordered `InvoiceFieldKey[]`.
- `data`: the exact rule-specific object below.

### 5.2 Rule-specific result data

| `code` | `severity` | `data` |
| --- | --- | --- |
| `REQUIRED_FIELD_MISSING` | `error` | `{ "missingField": InvoiceFieldKey }`; emit one result per missing field |
| `AMOUNT_RECONCILIATION_FAILED` | `error` | `{ "currency": string, "subtotal": Money, "taxAmount": Money, "expectedTotal": Money, "actualTotal": Money, "difference": Money, "tolerance": Money }` where difference is actual minus expected |
| `NEGATIVE_AMOUNT_UNEXPECTED` | `warning` | `{ "field": InvoiceFieldKey, "amount": Money }`; emit one result per negative amount |
| `DUE_DATE_BEFORE_INVOICE_DATE` | `error` | `{ "invoiceDate": Date, "dueDate": Date }` |
| `PAYMENT_TERMS_MISMATCH` | `warning` | `{ "invoiceDate": Date, "dueDate": Date, "normalizedPaymentTermsDays": integer, "calculatedDueDate": Date }` |
| `POSSIBLE_DUPLICATE_INVOICE` | `error` | `{ "matches": DuplicateInvoiceMatchDto[] }` |
| `CURRENCY_INVALID` | `error` | `{ "value": string or null, "allowedCurrencies": string[] }` |
| `LOW_EXTRACTION_CONFIDENCE` | `warning` | `{ "field": InvoiceFieldKey, "confidence": Confidence, "confidenceBand": "low" or "unknown" }`; emit one result per affected field |
| `INVOICE_DATE_IN_FUTURE` | `warning` | `{ "invoiceDate": Date, "currentLocalDate": Date }` |

`DuplicateInvoiceMatchDto` contains:

| Property | Type |
| --- | --- |
| `invoiceId` | `Uuid` |
| `status` | `InvoiceStatus` |

Duplicate matches are ordered by `invoiceId` ascending. Validation messages are not used as programmatic identifiers; clients use `code`, `fields`, and `data`.

## 6. InvoiceDetailDto

| Property | Type | Nullable |
| --- | --- | --- |
| `id` | `Uuid` | no |
| `status` | `InvoiceStatus` | no |
| `draftVersion` | `DraftVersion` | no |
| `lastValidatedVersion` | `DraftVersion` | yes |
| `createdAt` | `UtcTimestamp` | no |
| `updatedAt` | `UtcTimestamp` | no |
| `document` | `InvoiceDocumentDto` | no |
| `documentTextSource` | `DocumentTextSource` | yes |
| `fields` | `InvoiceFieldsDto` | yes |
| `reviewNotes` | string | yes |
| `summary` | `InvoiceSummaryDto` | no |
| `currentValidation` | `ValidationRunDto` | yes |
| `corrections` | `FieldCorrectionDto[]` | no |
| `processingFailure` | `ProcessingFailureDto` | yes |
| `decision` | `InvoiceDecisionDto` | yes |

Nullability invariants:

- `fields` is `null` while `status` is `processing` and when no valid extraction object was persisted for `processingFailed`.
- Successful extraction produces non-null `fields` and an initial `currentValidation`.
- A changed draft save clears `currentValidation` and `lastValidatedVersion` until explicit validation succeeds.
- `processingFailure` is non-null only for `processingFailed`.
- `decision` is non-null only for `approved` and `rejected`.
- `reviewNotes` may be non-null only after reviewer input; it has no extraction confidence.

Representative response:

~~~json
{
  "id": "8fe6c23b-b0b8-4bc9-9028-171b7a581e93",
  "status": "readyForApproval",
  "draftVersion": 2,
  "lastValidatedVersion": 2,
  "createdAt": "2026-09-13T08:12:14.120Z",
  "updatedAt": "2026-09-13T08:18:02.451Z",
  "document": {
    "originalFilename": "INV-1042.pdf",
    "mediaType": "application/pdf",
    "byteLength": 248120,
    "sha256": "4e9d90b2df4d2c8e08baf9e620c9fe482b91cc45665a4fefbc9360c15b32d4f1",
    "pageCount": 2,
    "integrityStatus": "available"
  },
  "documentTextSource": "nativeText",
  "fields": {
    "supplier": {
      "name": {
        "value": "Northwind Supplies",
        "originalValue": "Northwind Supply",
        "originalSource": "aiInference",
        "currentSource": "reviewer",
        "confidence": 0.84,
        "confidenceBand": "medium",
        "differsFromOriginal": true,
        "lastCorrectedAt": "2026-09-13T08:16:10.005Z"
      },
      "registrationId": {
        "value": null,
        "originalValue": null,
        "originalSource": "aiInference",
        "currentSource": "aiInference",
        "confidence": null,
        "confidenceBand": "unknown",
        "differsFromOriginal": false,
        "lastCorrectedAt": null
      }
    },
    "reference": {
      "invoiceNumber": {
        "value": "INV-1042",
        "originalValue": "INV-1042",
        "originalSource": "aiInference",
        "currentSource": "aiInference",
        "confidence": 0.64,
        "confidenceBand": "low",
        "differsFromOriginal": false,
        "lastCorrectedAt": null
      },
      "purchaseOrderNumber": {
        "value": "PO-0041",
        "originalValue": "PO-0041",
        "originalSource": "aiInference",
        "currentSource": "aiInference",
        "confidence": 0.94,
        "confidenceBand": "high",
        "differsFromOriginal": false,
        "lastCorrectedAt": null
      }
    },
    "datesAndTerms": {
      "invoiceDate": {
        "value": "2026-09-01",
        "originalValue": "2026-09-01",
        "originalSource": "aiInference",
        "currentSource": "aiInference",
        "confidence": 0.97,
        "confidenceBand": "high",
        "differsFromOriginal": false,
        "lastCorrectedAt": null
      },
      "dueDate": {
        "value": "2026-10-01",
        "originalValue": "2026-10-01",
        "originalSource": "aiInference",
        "currentSource": "aiInference",
        "confidence": 0.91,
        "confidenceBand": "high",
        "differsFromOriginal": false,
        "lastCorrectedAt": null
      },
      "paymentTerms": {
        "value": "Net 30",
        "originalValue": "Net 30",
        "originalSource": "aiInference",
        "currentSource": "aiInference",
        "confidence": 0.93,
        "confidenceBand": "high",
        "differsFromOriginal": false,
        "lastCorrectedAt": null
      },
      "normalizedPaymentTermsDays": 30
    },
    "amounts": {
      "currency": {
        "value": "EUR",
        "originalValue": "EUR",
        "originalSource": "aiInference",
        "currentSource": "aiInference",
        "confidence": 0.99,
        "confidenceBand": "high",
        "differsFromOriginal": false,
        "lastCorrectedAt": null
      },
      "subtotal": {
        "value": "1000.00",
        "originalValue": "1000.00",
        "originalSource": "aiInference",
        "currentSource": "aiInference",
        "confidence": 0.97,
        "confidenceBand": "high",
        "differsFromOriginal": false,
        "lastCorrectedAt": null
      },
      "taxAmount": {
        "value": "180.00",
        "originalValue": "180.00",
        "originalSource": "aiInference",
        "currentSource": "aiInference",
        "confidence": 0.96,
        "confidenceBand": "high",
        "differsFromOriginal": false,
        "lastCorrectedAt": null
      },
      "total": {
        "value": "1180.00",
        "originalValue": "1180.00",
        "originalSource": "aiInference",
        "currentSource": "aiInference",
        "confidence": 0.98,
        "confidenceBand": "high",
        "differsFromOriginal": false,
        "lastCorrectedAt": null
      }
    }
  },
  "reviewNotes": "Supplier name checked against the PDF.",
  "summary": {
    "extractedFieldCount": 10,
    "warningCount": 1,
    "errorCount": 0,
    "manualCorrectionCount": 2
  },
  "currentValidation": {
    "id": "d03eef91-bba6-468a-9e27-25c1cf16f405",
    "draftVersion": 2,
    "validatedAt": "2026-09-13T08:18:02.451Z",
    "warningCount": 1,
    "errorCount": 0,
    "results": [
      {
        "code": "LOW_EXTRACTION_CONFIDENCE",
        "severity": "warning",
        "message": "Invoice number was extracted with low confidence.",
        "fields": ["invoiceNumber"],
        "data": {
          "field": "invoiceNumber",
          "confidence": 0.64,
          "confidenceBand": "low"
        }
      }
    ]
  },
  "corrections": [
    {
      "id": "17",
      "auditEventId": "23",
      "field": "supplierName",
      "previousValue": "Northwind Supply",
      "newValue": "Northwind Supplies",
      "draftVersion": 2,
      "occurredAt": "2026-09-13T08:16:10.005Z"
    },
    {
      "id": "18",
      "auditEventId": "23",
      "field": "reviewNotes",
      "previousValue": null,
      "newValue": "Supplier name checked against the PDF.",
      "draftVersion": 2,
      "occurredAt": "2026-09-13T08:16:10.005Z"
    }
  ],
  "processingFailure": null,
  "decision": null
}
~~~

The example intentionally shows that confidence metadata is retained after correction. A corrected field does not receive a higher provider-confidence value.

## 7. Request DTOs

### 7.1 SaveInvoiceDraftRequest

`SaveInvoiceDraftRequest` is a complete replacement of editable draft values:

~~~json
{
  "expectedVersion": 2,
  "draft": {
    "supplier": {
      "name": "Northwind Supplies",
      "registrationId": null
    },
    "reference": {
      "invoiceNumber": "INV-1042",
      "purchaseOrderNumber": "PO-0041"
    },
    "datesAndTerms": {
      "invoiceDate": "2026-09-01",
      "dueDate": "2026-10-01",
      "paymentTerms": "Net 30"
    },
    "amounts": {
      "currency": "EUR",
      "subtotal": "1000.00",
      "taxAmount": "180.00",
      "total": "1180.00"
    },
    "reviewNotes": "Supplier name checked against the PDF."
  }
}
~~~

Every displayed draft member is required in the JSON object and has a nullable value:

- Supplier name and registration ID: string or `null`.
- Invoice and purchase-order number: string or `null`.
- Invoice date and due date: `Date` or `null`.
- Payment terms: string or `null`.
- Currency: string or `null`.
- Subtotal, tax amount, and total: `Money` or `null`.
- Review notes: string or `null`.

The request excludes status, provenance, confidence, original values, normalized payment-term days, validation, decision, timestamps, audit data, document data, and duplicate-normalization keys.

### 7.2 Versioned action requests

`ValidateInvoiceRequest`:

~~~json
{
  "expectedVersion": 2
}
~~~

`ApproveInvoiceRequest`:

~~~json
{
  "expectedVersion": 2
}
~~~

`RejectInvoiceRequest`:

~~~json
{
  "expectedVersion": 2,
  "reason": "Duplicate invoice received from the supplier."
}
~~~

## 8. Queue and pagination contracts

### 8.1 InvoiceQueueItemDto

| Property | Type | Nullable |
| --- | --- | --- |
| `id` | `Uuid` | no |
| `status` | `InvoiceStatus` | no |
| `supplierName` | string | yes |
| `invoiceNumber` | string | yes |
| `invoiceDate` | `Date` | yes |
| `total` | `Money` | yes |
| `currency` | string | yes |
| `draftVersion` | `DraftVersion` | no |
| `warningCount` | count | no |
| `errorCount` | count | no |
| `exceptionCount` | count | no |
| `processingFailure` | `ProcessingFailureDto` | yes |
| `createdAt` | `UtcTimestamp` | no |
| `updatedAt` | `UtcTimestamp` | no |

`exceptionCount` is `warningCount + errorCount`. A processing failure is represented separately and does not add to `exceptionCount`.

### 8.2 InvoiceQueueSummaryDto

| Property | Meaning |
| --- | --- |
| `totalInvoiceCount` | All persisted invoices |
| `processingCount` | Status `processing` |
| `reviewRequiredCount` | Status `reviewRequired` |
| `readyForApprovalCount` | Status `readyForApproval` |
| `approvedCount` | Status `approved` |
| `rejectedCount` | Status `rejected` |
| `processingFailedCount` | Status `processingFailed` |
| `pendingReviewCount` | `reviewRequiredCount + readyForApprovalCount` |
| `warningInvoiceCount` | Invoices whose current validation contains at least one warning |
| `errorInvoiceCount` | Invoices whose current validation contains at least one error |

All values are non-negative integers. Summary counts are global and ignore the request's `search` and `status` filters. An invoice with both warning and error results contributes to both corresponding counts. `processingFailed` invoices are counted only in `processingFailedCount` unless they also have a retained current validation.

### 8.3 Page envelopes

`InvoiceQueuePageDto` contains:

- `items: InvoiceQueueItemDto[]`
- `summary: InvoiceQueueSummaryDto`
- `page: integer`
- `pageSize: integer`
- `totalItems: integer`
- `totalPages: integer`
- `hasPreviousPage: boolean`
- `hasNextPage: boolean`

`AuditHistoryPageDto` has the same pagination members and `items: AuditEventDto[]`, but no `summary`.

Rules:

- `page` is one-based and defaults to `1`.
- Queue `pageSize` defaults to `25`.
- History `pageSize` defaults to `50`.
- `pageSize` must be from `1` through `100`.
- Invalid values return `400 REQUEST_VALIDATION_FAILED`.
- When `totalItems` is zero, `totalPages` is zero and both navigation flags are `false`.
- A valid page beyond `totalPages` returns `200` with `items: []` and the requested page number.

### 8.4 Queue query

`GET /api/invoices` accepts:

| Parameter | Type | Required | Default |
| --- | --- | --- | --- |
| `search` | string | no | none |
| `status` | repeated `InvoiceStatus` | no | all statuses |
| `page` | integer | no | `1` |
| `pageSize` | integer | no | `25` |
| `sort` | `InvoiceSort` | no | `updatedAtDesc` |

Example:

`GET /api/invoices?search=northwind&status=reviewRequired&status=readyForApproval&page=1&pageSize=25&sort=updatedAtDesc`

Search is trimmed and case-insensitive. It matches supplier name, supplier registration ID, invoice number, or purchase-order number. It never searches PDF bytes, extracted document text, review notes, rejection reasons, or audit data.

Queue ordering is:

- `updatedAtDesc`: `updatedAt` descending, then invoice `id` descending.
- `updatedAtAsc`: `updatedAt` ascending, then invoice `id` ascending.
- `createdAtDesc`: `createdAt` descending, then invoice `id` descending.
- `createdAtAsc`: `createdAt` ascending, then invoice `id` ascending.

Representative queue response:

~~~json
{
  "items": [
    {
      "id": "8fe6c23b-b0b8-4bc9-9028-171b7a581e93",
      "status": "readyForApproval",
      "supplierName": "Northwind Supplies",
      "invoiceNumber": "INV-1042",
      "invoiceDate": "2026-09-01",
      "total": "1180.00",
      "currency": "EUR",
      "draftVersion": 2,
      "warningCount": 1,
      "errorCount": 0,
      "exceptionCount": 1,
      "processingFailure": null,
      "createdAt": "2026-09-13T08:12:14.120Z",
      "updatedAt": "2026-09-13T08:18:02.451Z"
    }
  ],
  "summary": {
    "totalInvoiceCount": 5,
    "processingCount": 0,
    "reviewRequiredCount": 1,
    "readyForApprovalCount": 1,
    "approvedCount": 2,
    "rejectedCount": 0,
    "processingFailedCount": 1,
    "pendingReviewCount": 2,
    "warningInvoiceCount": 1,
    "errorInvoiceCount": 1
  },
  "page": 1,
  "pageSize": 25,
  "totalItems": 1,
  "totalPages": 1,
  "hasPreviousPage": false,
  "hasNextPage": false
}
~~~

The example's `totalItems` reflects the active filter, while `summary.totalInvoiceCount` and the other summary values remain global.

## 9. Route contract

| Method and route | Request | Success | Expected failures |
| --- | --- | --- | --- |
| `POST /api/invoices` | `multipart/form-data` with exactly one part named `file` | `201 InvoiceDetailDto` and `Location: /api/invoices/{id}` | `400`, `413`, `500` |
| `GET /api/invoices` | Queue query | `200 InvoiceQueuePageDto` | `400`, `500` |
| `GET /api/invoices/{id}` | none | `200 InvoiceDetailDto` | `400`, `404`, `500` |
| `GET /api/invoices/{id}/document` | optional Range header | `200` or `206` PDF stream | `400`, `404`, `416`, `500` |
| `PUT /api/invoices/{id}/draft` | `SaveInvoiceDraftRequest` | `200 InvoiceDetailDto` | `400`, `404`, `409`, `500` |
| `POST /api/invoices/{id}/validate` | `ValidateInvoiceRequest` | `200 InvoiceDetailDto` | `400`, `404`, `409`, `500` |
| `POST /api/invoices/{id}/approve` | `ApproveInvoiceRequest` | `200 InvoiceDetailDto` | `400`, `404`, `409`, `500` |
| `POST /api/invoices/{id}/reject` | `RejectInvoiceRequest` | `200 InvoiceDetailDto` | `400`, `404`, `409`, `500` |
| `GET /api/invoices/{id}/history` | `page` and `pageSize` | `200 AuditHistoryPageDto` | `400`, `404`, `500` |
| `GET /api/invoices/{id}/export` | none | `200 InvoiceExportV1Dto` attachment | `400`, `404`, `409`, `500` |

### 9.1 Upload

- `file` is the only accepted multipart file part.
- No part, an empty part, or more than one file returns `400`.
- Pre-acceptance failures create no invoice.
- After durable acceptance, successful extraction and a controlled post-acceptance processing failure both return `201` with the persisted `InvoiceDetailDto`.
- A `processingFailed` creation response includes non-null `processingFailure` and does not use Problem Details.

### 9.2 Document response

- The content type is `application/pdf`.
- The disposition is inline and uses a sanitized display filename.
- `Accept-Ranges: bytes` is supported for React-PDF/PDF.js.
- A satisfiable range returns `206 Partial Content`.
- An unsatisfiable range returns `416 Range Not Satisfiable` using the framework range response, not a JSON Problem Details body.
- A missing document relationship returns `404 DOCUMENT_NOT_FOUND`.
- A referenced file that is missing or corrupt returns `500 DOCUMENT_UNAVAILABLE` and updates integrity state as defined by the architecture.

### 9.3 History

History is always ordered by `occurredAt` ascending and then numeric event `id` ascending. Clients cannot select another history order.

### 9.4 Export

Export is available only when status is `approved` or `rejected`. Other states return `409 INVOICE_STATE_CONFLICT`. The response uses:

- `Content-Type: application/json; charset=utf-8`
- `Content-Disposition: attachment; filename="invoice-{invoiceId}.json"`

## 10. Draft versions and conflicts

`DraftVersion` starts at `1` when the invoice is created. It is never zero or negative.

| Operation | Requires `expectedVersion` | Changes version |
| --- | --- | --- |
| Changed draft save | yes | increments by exactly one |
| No-op draft save | yes | no |
| Explicit validation | yes | no |
| Approval | yes | no |
| Rejection | yes | no |

A review-note change is a draft change. A save whose canonical values all match current persisted values is a no-op: it appends a `draftSaved` audit event, but it does not add corrections, increment the version, or invalidate current validation.

Conflict behavior:

- An expected version that differs from the current version returns `409 INVOICE_VERSION_CONFLICT` and makes no mutation.
- Validation first checks the supplied expected version. If another write occurs after the snapshot is captured but before validation can commit, it returns `409 VALIDATION_STALE` and persists no validation run.
- An operation disallowed by the current status returns `409 INVOICE_STATE_CONFLICT`.
- Approval re-runs deterministic validation in its transaction. If new blocking errors exist, the new validation run, audit event, and resulting `reviewRequired` status are persisted; the response is `409 APPROVAL_BLOCKED`.
- Rejection performs no validation.
- Every invoice-related `409` includes non-null `currentVersion`.
- Clients must preserve unsaved form state, refetch, and ask the reviewer to reconcile. They must not silently merge or automatically retry.

For a structurally valid mutation request, the application checks resource existence, expected version, allowed lifecycle state, and then operation-specific domain conditions in that order. Automatic JSON/model validation can return `400` before route resource lookup.

## 11. Problem Details

### 11.1 InvoiceProblemDetails

Every JSON error has this exact shape:

| Property | Type | Nullable | Stability |
| --- | --- | --- | --- |
| `type` | URI string | no | Stable per `code` |
| `title` | string | no | Human-facing; wording may evolve |
| `status` | integer | no | HTTP status |
| `detail` | string | no | Safe human-facing explanation; wording may evolve |
| `instance` | string | no | Request path, excluding sensitive query content |
| `code` | string | no | Stable machine-readable code |
| `correlationId` | string | no | Also returned in `X-Correlation-ID` |
| `fields` | object mapping field path to string array | yes | Field paths are stable |
| `currentVersion` | `DraftVersion` | yes | Populated for invoice-related `409` responses |

`fields` uses camel-case dotted paths such as `draft.supplier.name`, `draft.amounts.total`, `expectedVersion`, `reason`, or `file`. It is `null` when the problem is not field-specific. Validation-result `fields` remain `InvoiceFieldKey[]` and are a separate concept.

Problem `type` is `urn:invoice-review-assistant:problem:{lower-kebab-code}`. For example, `INVOICE_VERSION_CONFLICT` uses `urn:invoice-review-assistant:problem:invoice-version-conflict`.

Example:

~~~json
{
  "type": "urn:invoice-review-assistant:problem:invoice-version-conflict",
  "title": "The invoice has changed",
  "status": 409,
  "detail": "Refresh the invoice and reconcile your unsaved changes before trying again.",
  "instance": "/api/invoices/8fe6c23b-b0b8-4bc9-9028-171b7a581e93/draft",
  "code": "INVOICE_VERSION_CONFLICT",
  "correlationId": "01k4yr8d1ax3yb4jvq7w9tzj10",
  "fields": null,
  "currentVersion": 3
}
~~~

### 11.2 Stable HTTP error catalog

| Status | Code | Use |
| ---| --- | --- |
| `400` | `REQUEST_VALIDATION_FAILED` | Malformed JSON, unknown property, invalid route/query/body value, multiple upload files, or other DTO/model error |
| `400` | `PDF_FILE_REQUIRED` | Multipart upload contains no `file` part |
| `400` | `PDF_EMPTY` | Uploaded file contains zero bytes |
| `400` | `PDF_TYPE_INVALID` | Extension or declared media type is not accepted as PDF |
| `400` | `PDF_SIGNATURE_INVALID` | File does not have the required PDF signature |
| `400` | `PDF_INVALID` | PDF structure or integrity inspection fails |
| `400` | `PDF_ENCRYPTED` | PDF is encrypted or password protected |
| `400` | `PDF_PAGE_LIMIT_EXCEEDED` | PDF exceeds the configured page limit |
| `400` | `REJECTION_REASON_REQUIRED` | Rejection reason is missing, blank, or whitespace-only |
| `404` | `INVOICE_NOT_FOUND` | Invoice ID does not exist |
| `404` | `DOCUMENT_NOT_FOUND` | Invoice exists but has no document relationship |
| `409` | `INVOICE_VERSION_CONFLICT` | Supplied expected version is not current |
| `409` | `INVOICE_STATE_CONFLICT` | Lifecycle state does not allow the requested operation |
| `409` | `VALIDATION_STALE` | Draft changed after validation captured its snapshot |
| `409` | `APPROVAL_BLOCKED` | Approval's fresh validation found blocking errors |
| `413` | `PDF_SIZE_LIMIT_EXCEEDED` | Upload exceeds the configured 20 MiB default |
| `500` | `UNEXPECTED_ERROR` | Unclassified server failure |
| `500` | `PERSISTENCE_FAILED` | Persistence prevented a trustworthy result |
| `500` | `STORAGE_FAILED` | Document storage operation failed |
| `500` | `DOCUMENT_UNAVAILABLE` | Referenced PDF is missing, unreadable, or corrupt |

Declared content type, extension, and signature errors use `400`, not `415`. `fields` is normally keyed by `file` for upload failures and `reason` for rejection-reason failure.

For `APPROVAL_BLOCKED`, `fields` contains each affected draft field path and its current blocking messages. The client refetches `InvoiceDetailDto` to obtain the complete persisted validation run.

## 12. Audit contracts

### 12.1 AuditEventDto common envelope

Every history item contains:

| Property | Type | Nullable |
| --- | --- | --- |
| `id` | `SequenceId` | no |
| `invoiceId` | `Uuid` | no |
| `type` | `AuditEventType` | no |
| `actor` | `AuditActor` | no |
| `occurredAt` | `UtcTimestamp` | no |
| `draftVersion` | `DraftVersion` | no |
| `details` | event-specific object | no |

`type` is the discriminator. Event and correction IDs are decimal strings on the wire so generated JavaScript clients never lose 64-bit integer precision.

### 12.2 AuditEventType and details

| `type` | `actor` | Exact `details` |
| --- | --- | --- |
| `invoiceUploaded` | `reviewer` | `{ "document": InvoiceDocumentDto }` |
| `extractionCompleted` | `system` | `{ "documentTextSource": DocumentTextSource, "extractedFieldCount": integer }` |
| `extractionFailed` | `system` | `{ "failure": ProcessingFailureDto }` |
| `draftSaved` | `reviewer` | `{ "isNoOp": boolean, "changes": FieldCorrectionDto[] }` |
| `validationCompleted` | see below | `{ "trigger": ValidationTrigger, "validationRunId": Uuid, "warningCount": integer, "errorCount": integer, "resultingStatus": InvoiceStatus }` |
| `invoiceApproved` | `reviewer` | `{ "decidedAt": UtcTimestamp }` |
| `invoiceRejected` | `reviewer` | `{ "decidedAt": UtcTimestamp, "rejectionReason": string }` |
| `documentIntegrityChanged` | `system` | `{ "previousStatus": DocumentIntegrityStatus, "currentStatus": DocumentIntegrityStatus }` |

The `AuditEventType` enum therefore contains exactly:

- `invoiceUploaded`
- `extractionCompleted`
- `extractionFailed`
- `draftSaved`
- `validationCompleted`
- `invoiceApproved`
- `invoiceRejected`
- `documentIntegrityChanged`

For `validationCompleted`:

- `initial` and `approval` triggers use actor `system`.
- `explicit` uses actor `reviewer`.
- A blocked approval produces `validationCompleted` but no `invoiceApproved` event.
- A successful approval produces `validationCompleted` followed by `invoiceApproved` in deterministic event-ID order.

A startup-interrupted invoice uses `extractionFailed` with stage `startupRecovery` and code `PROCESS_INTERRUPTED`. Integrity reconciliation emits an event only when the persisted integrity status actually changes.

Representative draft-save event:

~~~json
{
  "id": "23",
  "invoiceId": "8fe6c23b-b0b8-4bc9-9028-171b7a581e93",
  "type": "draftSaved",
  "actor": "reviewer",
  "occurredAt": "2026-09-13T08:16:10.005Z",
  "draftVersion": 2,
  "details": {
    "isNoOp": false,
    "changes": [
      {
        "id": "17",
        "auditEventId": "23",
        "field": "supplierName",
        "previousValue": "Northwind Supply",
        "newValue": "Northwind Supplies",
        "draftVersion": 2,
        "occurredAt": "2026-09-13T08:16:10.005Z"
      },
      {
        "id": "18",
        "auditEventId": "23",
        "field": "reviewNotes",
        "previousValue": null,
        "newValue": "Supplier name checked against the PDF.",
        "draftVersion": 2,
        "occurredAt": "2026-09-13T08:16:10.005Z"
      }
    ]
  }
}
~~~

## 13. InvoiceExportV1Dto

The export envelope is:

| Property | Type |
| --- | --- |
| `schemaVersion` | string literal `1.0` |
| `invoice` | `InvoiceDetailDto` |
| `auditHistory` | complete `AuditEventDto[]` |

~~~json
{
  "schemaVersion": "1.0",
  "invoice": {
    "...": "the complete InvoiceDetailDto"
  },
  "auditHistory": [
    {
      "...": "complete ordered AuditEventDto values"
    }
  ]
}
~~~

The illustrative ellipses above are documentation shorthand and never appear in an actual export.

The export:

- Is available only for `approved` and `rejected` invoices.
- Contains the complete, unpaginated history in normal deterministic history order.
- Has no generated-at timestamp, so the same persisted state has the same semantic export content.
- Includes final values, original values, provenance, confidence, validation, corrections, document metadata, review notes, decision metadata, and audit history.
- Never includes PDF bytes, storage keys, paths, normalized document text, prompts, raw provider responses, credentials, or logs.

## 14. AI extraction contract

### 14.1 Provider envelope

The Responses API structured-output format is named `invoice_extraction_v1` and uses `strict: true`. Only the value returned as structured output is validated and mapped; provider metadata is not part of the extraction DTO.

The schema is a root object, every property is required, optional business values use a union with `null`, and every object disallows additional properties. These requirements match the [official OpenAI Structured Outputs guidance](https://developers.openai.com/api/docs/guides/structured-outputs).

### 14.2 Canonical invoice_extraction_v1 schema

~~~json
{
  "type": "object",
  "properties": {
    "schemaVersion": {
      "type": "string",
      "enum": ["1.0"]
    },
    "supplier": {
      "type": "object",
      "properties": {
        "name": {
          "$ref": "#/$defs/nullableTextField"
        },
        "registrationId": {
          "$ref": "#/$defs/nullableTextField"
        }
      },
      "required": ["name", "registrationId"],
      "additionalProperties": false
    },
    "reference": {
      "type": "object",
      "properties": {
        "invoiceNumber": {
          "$ref": "#/$defs/nullableTextField"
        },
        "purchaseOrderNumber": {
          "$ref": "#/$defs/nullableTextField"
        }
      },
      "required": ["invoiceNumber", "purchaseOrderNumber"],
      "additionalProperties": false
    },
    "datesAndTerms": {
      "type": "object",
      "properties": {
        "invoiceDate": {
          "$ref": "#/$defs/nullableDateField"
        },
        "dueDate": {
          "$ref": "#/$defs/nullableDateField"
        },
        "paymentTerms": {
          "$ref": "#/$defs/nullableTextField"
        }
      },
      "required": ["invoiceDate", "dueDate", "paymentTerms"],
      "additionalProperties": false
    },
    "amounts": {
      "type": "object",
      "properties": {
        "currency": {
          "$ref": "#/$defs/nullableCurrencyField"
        },
        "subtotal": {
          "$ref": "#/$defs/nullableMoneyField"
        },
        "taxAmount": {
          "$ref": "#/$defs/nullableMoneyField"
        },
        "total": {
          "$ref": "#/$defs/nullableMoneyField"
        }
      },
      "required": ["currency", "subtotal", "taxAmount", "total"],
      "additionalProperties": false
    }
  },
  "required": [
    "schemaVersion",
    "supplier",
    "reference",
    "datesAndTerms",
    "amounts"
  ],
  "additionalProperties": false,
  "$defs": {
    "nullableTextField": {
      "type": "object",
      "properties": {
        "value": {
          "type": ["string", "null"]
        },
        "confidence": {
          "type": ["number", "null"],
          "minimum": 0,
          "maximum": 1
        }
      },
      "required": ["value", "confidence"],
      "additionalProperties": false
    },
    "nullableDateField": {
      "type": "object",
      "properties": {
        "value": {
          "type": ["string", "null"],
          "format": "date"
        },
        "confidence": {
          "type": ["number", "null"],
          "minimum": 0,
          "maximum": 1
        }
      },
      "required": ["value", "confidence"],
      "additionalProperties": false
    },
    "nullableCurrencyField": {
      "type": "object",
      "properties": {
        "value": {
          "type": ["string", "null"],
          "pattern": "^[A-Z]{3}$"
        },
        "confidence": {
          "type": ["number", "null"],
          "minimum": 0,
          "maximum": 1
        }
      },
      "required": ["value", "confidence"],
      "additionalProperties": false
    },
    "nullableMoneyField": {
      "type": "object",
      "properties": {
        "value": {
          "type": ["string", "null"],
          "pattern": "^-?(?:0|[1-9]\\d{0,26})\\.\\d{2}$"
        },
        "confidence": {
          "type": ["number", "null"],
          "minimum": 0,
          "maximum": 1
        }
      },
      "required": ["value", "confidence"],
      "additionalProperties": false
    }
  }
}
~~~

The schema intentionally excludes:

- `reviewNotes` and rejection reason.
- `normalizedPaymentTermsDays`, which the backend derives from payment-terms text.
- Source and provenance, which the backend assigns.
- Status, versions, validation, duplicate keys, decisions, document metadata, and audit data.

### 14.3 Post-schema semantic checks

After strict provider output is received, JsonSchema.Net validates the same canonical schema with date-format validation enabled. Before domain mapping, the adapter also enforces:

- A `null` value must have `confidence: null`.
- A non-null value may have a numeric confidence or `null`.
- A non-null text value must contain at least one non-whitespace character.
- Dates must parse exactly as `DateOnly`.
- Money must parse exactly as invariant C# `decimal` without changing its value.
- Currency remains a three-letter candidate; the deterministic currency rule decides whether it is enabled.
- The schema version must be exactly `1.0`.

Successful mapping assigns `aiInference` as field source, preserves the provider confidence, derives normalized payment-term days, and stores `nativeText` or `ocr` separately as the document-text source.

A refusal, incomplete response, malformed JSON, schema violation, semantic violation, or unmappable value persists a safe `processingFailed` record using `AI_REFUSED`, `AI_RESPONSE_INCOMPLETE`, or `AI_RESPONSE_INVALID` as appropriate. It does not persist partial or fabricated invoice fields and does not expose raw provider output.

## 15. Contract verification

Implementation is conformant only when automated checks cover:

| Area | Required checks |
| --- | --- |
| Serialization | Every enum value, lowercase UUIDs, sequence IDs as strings, exact dates/timestamps, two-decimal money, explicit nulls, empty arrays, and unknown-member rejection |
| OpenAPI | DTO required/nullable flags, string-enum schemas, money/date formats, discriminators, multipart file name, response codes, and a committed OpenAPI snapshot |
| Generated client | NSwag generation produces no uncommitted diff and presentation code does not import persistence/domain types |
| Queue | Default and maximum page sizes, invalid page inputs, out-of-range empty page, repeated statuses, all sort orders with UUID tie-breakers, search fields, and global unfiltered summary counts |
| Drafts | Full replacement, explicit clearing with `null`, canonical no-op, review-note change, version increment, and original-value retention |
| Concurrency | Stale save, validate, approve, and reject; validation race; illegal state; blocked approval's persisted validation; no automatic merge |
| Validation | Each discriminated data object, severity, related fields, money/date representation, duplicate ordering, and stable result ordering |
| Problem Details | Every catalog code, content type, stable extensions, field paths, correlation header, safe text, and `currentVersion` rules |
| Audit | Every event type and details schema, actor/trigger mapping, decimal-string IDs, no-op changes, approval event order, deterministic pagination, and immutability |
| Upload/document | Missing file, multiple files, 20 MiB boundary, PDF checks, `201 processingFailed` behavior, inline/range streaming, and unavailable-document mapping |
| Export | Terminal-only access, complete unpaginated history, stable schema version, explicit nulls, and absence of bytes, paths, text, prompts, provider output, and secrets |
| Extraction | Complete and all-null objects, missing/extra properties, wrong types, invalid dates/money/currency/confidence, blank text, value/confidence invariant, refusal, incomplete output, and malformed output |

Use ASP.NET Core integration tests through `WebApplicationFactory` for HTTP behavior, SQLite-backed tests for ordering and concurrency, schema-fixture tests against JsonSchema.Net, and a deterministic fake extraction provider for application journeys.

## 16. Contract evolution

- Additive nullable response fields still require updating this document, the OpenAPI snapshot, and the generated client.
- A property rename, removal, type change, enum rename, nullability change, money/date format change, or semantic change requires a new HTTP contract version.
- Extraction schema changes require a new schema name and `schemaVersion`; do not silently mutate `invoice_extraction_v1`.
- Export changes require a new export `schemaVersion`.
- Human-readable validation and Problem Details wording may improve without a version change; clients must use stable codes and structured data.
- Authentication, asynchronous processing, failed-invoice retry, deletion, batch upload, and ERP/accounting-system exports remain outside HTTP v1.
