import {
  ConfidenceBand,
  DocumentIntegrityStatus,
  DocumentTextSource,
  FieldSource,
  InvoiceDocumentDtoMediaType,
  InvoiceStatus,
  type InvoiceDetailDto,
  type TextFieldDto,
} from '../api/generated/client';

function field(value: string | null): TextFieldDto {
  return {
    value,
    originalValue: value,
    originalSource: FieldSource.AiInference,
    currentSource: FieldSource.AiInference,
    confidence: value === null ? null : 0.96,
    confidenceBand: value === null ? ConfidenceBand.Unknown : ConfidenceBand.High,
    differsFromOriginal: false,
    lastCorrectedAt: null,
  };
}

export function buildInvoice(overrides: Partial<InvoiceDetailDto> = {}): InvoiceDetailDto {
  return {
    id: 'invoice-001',
    status: InvoiceStatus.ReviewRequired,
    draftVersion: 1,
    lastValidatedVersion: null,
    createdAt: '2026-09-15T08:00:00.000Z',
    updatedAt: '2026-09-15T08:00:00.000Z',
    document: {
      originalFilename: 'invoice-001.pdf',
      mediaType: InvoiceDocumentDtoMediaType.Application_pdf,
      byteLength: 1024,
      sha256: 'a'.repeat(64),
      pageCount: 1,
      integrityStatus: DocumentIntegrityStatus.Available,
    },
    documentTextSource: DocumentTextSource.NativeText,
    fields: {
      supplier: { name: field('Northwind Supplies'), registrationId: field('001') },
      reference: { invoiceNumber: field('INV-001'), purchaseOrderNumber: field(null) },
      datesAndTerms: { invoiceDate: field('2026-09-01'), dueDate: field('2026-10-01'), paymentTerms: field('Net 30'), normalizedPaymentTermsDays: 30 },
      amounts: { currency: field('USD'), subtotal: field('100.00'), taxAmount: field('20.00'), total: field('120.00') },
    },
    reviewNotes: null,
    summary: { extractedFieldCount: 10, warningCount: 0, errorCount: 0, manualCorrectionCount: 0 },
    currentValidation: null,
    corrections: [],
    processingFailure: null,
    decision: null,
    ...overrides,
  } as InvoiceDetailDto;
}

export function emptyQueue() {
  return {
    items: [],
    summary: {
      totalInvoiceCount: 0, processingCount: 0, reviewRequiredCount: 0, readyForApprovalCount: 0,
      approvedCount: 0, rejectedCount: 0, processingFailedCount: 0, pendingReviewCount: 0,
      warningInvoiceCount: 0, errorInvoiceCount: 0,
    },
    page: 1, pageSize: 25, totalItems: 0, totalPages: 0, hasPreviousPage: false, hasNextPage: false,
  };
}
