import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AuditEventType, ConfidenceBand, DecisionKind, DocumentIntegrityStatus, FieldSource, InvoiceStatus, ValidationSeverity, type InvoiceDetailDto } from '../../api/generated/client';
import * as invoiceApiModule from '../../api/invoiceApi';
import { assertNoSeriousAccessibilityViolations } from '../../test/accessibility';
import { CompletedInvoiceRecord, isTerminalInvoice } from './CompletedInvoiceRecord';

function field(value: string | null, changes: Record<string, unknown> = {}) {
  return { value, originalValue: value, originalSource: FieldSource.AiInference, currentSource: FieldSource.AiInference, confidence: 0.94, confidenceBand: ConfidenceBand.High, differsFromOriginal: false, lastCorrectedAt: null, ...changes };
}

const completedInvoice = {
  id: 'invoice-complete', status: InvoiceStatus.Approved, draftVersion: 3, lastValidatedVersion: 3, createdAt: '2026-09-15T10:00:00Z', updatedAt: '2026-09-15T10:05:00Z',
  document: { originalFilename: 'INV-COMPLETE.pdf', mediaType: 'application/pdf', byteLength: 1500, sha256: 'a'.repeat(64), pageCount: 2, integrityStatus: DocumentIntegrityStatus.Available }, documentTextSource: 'nativeText',
  fields: {
    supplier: { name: field('Northwind Supplies', { originalValue: 'Northwind Supply', currentSource: FieldSource.Reviewer, differsFromOriginal: true }), registrationId: field('001') },
    reference: { invoiceNumber: field('INV-001'), purchaseOrderNumber: field(null) },
    datesAndTerms: { invoiceDate: field('2026-09-01'), dueDate: field('2026-10-01'), paymentTerms: field('Net 30'), normalizedPaymentTermsDays: 30 },
    amounts: { currency: field('USD'), subtotal: field('10.00'), taxAmount: field('0.00'), total: field('10.00') },
  }, reviewNotes: 'Confirmed against document.', summary: { extractedFieldCount: 10, warningCount: 1, errorCount: 0, manualCorrectionCount: 1 },
  currentValidation: { id: 'validation-001', draftVersion: 3, validatedAt: '2026-09-15T10:04:00Z', warningCount: 1, errorCount: 0, results: [{ code: 'LOW_EXTRACTION_CONFIDENCE', severity: ValidationSeverity.Warning, message: 'Review supplier spelling.', fields: ['supplierName'], data: {} }] },
  corrections: [{ id: 'correction-1', auditEventId: '3', field: 'supplierName', previousValue: 'Northwind Supply', newValue: 'Northwind Supplies', draftVersion: 2, occurredAt: '2026-09-15T10:02:00Z' }], processingFailure: null,
  decision: { kind: DecisionKind.Approved, decidedAt: '2026-09-15T10:05:00Z', rejectionReason: null },
} as unknown as InvoiceDetailDto;

const firstHistory = { items: [
  { id: '1', invoiceId: 'invoice-complete', type: AuditEventType.InvoiceUploaded, actor: 'reviewer', occurredAt: '2026-09-15T10:00:00Z', draftVersion: 1, details: { document: completedInvoice.document } },
  { id: '2', invoiceId: 'invoice-complete', type: AuditEventType.DraftSaved, actor: 'reviewer', occurredAt: '2026-09-15T10:02:00Z', draftVersion: 2, details: { isNoOp: false, changes: completedInvoice.corrections } },
] as never[], page: 1, pageSize: 2, totalItems: 3, totalPages: 2, hasPreviousPage: false, hasNextPage: true };

function renderRecord(invoice = completedInvoice) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={client}><CompletedInvoiceRecord invoice={invoice} /></QueryClientProvider>);
}

afterEach(() => vi.restoreAllMocks());

describe('completed invoice record', () => {
  it('has no serious automated accessibility violations', async () => {
    vi.spyOn(invoiceApiModule.invoiceApi, 'getHistory').mockResolvedValue(firstHistory as never);
    const { container } = renderRecord();
    await screen.findByRole('list', { name: 'Audit events' });
    await assertNoSeriousAccessibilityViolations(container);
  });
  it('renders approved records as complete immutable evidence, retaining warnings and originals', async () => {
    vi.spyOn(invoiceApiModule.invoiceApi, 'getHistory').mockResolvedValue(firstHistory as never);
    renderRecord();
    await screen.findByRole('list', { name: 'Audit events' });
    expect(screen.getByRole('heading', { name: 'Approved invoice' })).toBeInTheDocument();
    expect(screen.getByText('Original extracted value: Northwind Supply')).toBeInTheDocument();
    expect(screen.getByText(/LOW_EXTRACTION_CONFIDENCE/)).toBeInTheDocument();
    expect(screen.getByText('Confirmed against document.')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Open source PDF: INV-COMPLETE.pdf' })).toHaveAttribute('href', '/api/invoices/invoice-complete/document');
    expect(screen.queryAllByRole('textbox')).toHaveLength(0);
    expect(screen.queryByRole('button', { name: /approve|reject|save|revalidate/i })).not.toBeInTheDocument();
  });

  it('shows rejection reason and ordered paginated audit history', async () => {
    const getHistory = vi.spyOn(invoiceApiModule.invoiceApi, 'getHistory');
    getHistory.mockImplementation(async (_id, page) => page.page === 2
      ? { items: [{ id: '3', invoiceId: 'invoice-complete', type: AuditEventType.InvoiceRejected, actor: 'reviewer', occurredAt: '2026-09-15T10:05:00Z', draftVersion: 3, details: { decidedAt: '2026-09-15T10:05:00Z', rejectionReason: 'Duplicate invoice.' } }] as never[], page: 2, pageSize: 2, totalItems: 3, totalPages: 2, hasPreviousPage: true, hasNextPage: false }
      : firstHistory as never);
    renderRecord({ ...completedInvoice, status: InvoiceStatus.Rejected, decision: { kind: DecisionKind.Rejected, decidedAt: '2026-09-15T10:05:00Z', rejectionReason: 'Duplicate invoice.' } });
    await screen.findByRole('list', { name: 'Audit events' });
    expect(screen.getByText('Duplicate invoice.')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Next history page' }));
    await screen.findByText(/Rejected at/);
    expect(getHistory).toHaveBeenLastCalledWith('invoice-complete', { page: 2 });
    expect(screen.getByRole('button', { name: 'Next history page' })).toBeDisabled();
  });

  it('handles unavailable source documents and export failures without exposing an unsafe action', async () => {
    vi.spyOn(invoiceApiModule.invoiceApi, 'getHistory').mockResolvedValue(firstHistory as never);
    vi.spyOn(invoiceApiModule, 'downloadInvoiceExport').mockRejectedValue(new Error('network'));
    renderRecord({ ...completedInvoice, document: { ...completedInvoice.document, integrityStatus: DocumentIntegrityStatus.Missing } });
    await screen.findByText(/Source PDF is unavailable/);
    expect(screen.queryByRole('link', { name: /Open source PDF/ })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Download JSON export' }));
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('JSON export could not be downloaded'));
  });

  it('does not render for an editable invoice', () => {
    expect(isTerminalInvoice(InvoiceStatus.ReviewRequired)).toBe(false);
    expect(isTerminalInvoice(InvoiceStatus.Approved)).toBe(true);
    vi.spyOn(invoiceApiModule.invoiceApi, 'getHistory');
    renderRecord({ ...completedInvoice, status: InvoiceStatus.ReviewRequired, decision: null });
    expect(screen.queryByLabelText('Completed invoice record')).not.toBeInTheDocument();
  });
});
