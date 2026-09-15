import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ConfidenceBand, FieldSource, InvoiceStatus, ValidationSeverity, type InvoiceDetailDto } from '../../api/generated/client';
import { invoiceApi } from '../../api/invoiceApi';
import { confidenceLabel, formatDate, formatMoney, InvoiceRoute } from './InvoiceRoute';

vi.mock('./PdfReviewPanel', () => ({ PdfReviewPanel: () => <section aria-label="Source invoice">PDF</section> }));

function field(value: string | null, overrides: Record<string, unknown> = {}) {
  return { value, originalValue: value, originalSource: FieldSource.AiInference, currentSource: FieldSource.AiInference, confidence: 0.95, confidenceBand: ConfidenceBand.High, differsFromOriginal: false, lastCorrectedAt: null, ...overrides };
}

const invoice = {
  id: 'invoice-001', status: InvoiceStatus.ReviewRequired, draftVersion: 2, lastValidatedVersion: 2,
  createdAt: '2026-09-15T10:00:00Z', updatedAt: '2026-09-15T10:01:00Z',
  document: { originalFilename: 'INV-001.pdf', mediaType: 'application/pdf', byteLength: 100, sha256: 'a'.repeat(64), pageCount: 1, integrityStatus: 'available' }, documentTextSource: 'nativeText',
  fields: {
    supplier: { name: field('Northwind', { originalValue: 'Northwind Supply', currentSource: FieldSource.Reviewer, confidence: 0.75, confidenceBand: ConfidenceBand.Medium, differsFromOriginal: true, lastCorrectedAt: '2026-09-15T10:01:00Z' }), registrationId: field('001') },
    reference: { invoiceNumber: field('INV-001'), purchaseOrderNumber: field(null, { confidence: null, confidenceBand: ConfidenceBand.Unknown }) },
    datesAndTerms: { invoiceDate: field('2026-09-01'), dueDate: field(null), paymentTerms: field('Net 30'), normalizedPaymentTermsDays: 30 },
    amounts: { currency: field('USD'), subtotal: field('10.00'), taxAmount: field('0.00'), total: field('10.00') },
  }, reviewNotes: null,
  summary: { extractedFieldCount: 10, warningCount: 1, errorCount: 1, manualCorrectionCount: 2 },
  currentValidation: { id: 'validation-001', draftVersion: 2, validatedAt: '2026-09-15T10:01:00Z', warningCount: 1, errorCount: 1, results: [
    { code: 'REQUIRED_FIELD_MISSING', severity: ValidationSeverity.Error, message: 'A required value is missing.', fields: ['invoiceNumber'], data: { missingField: 'invoiceNumber' } },
    { code: 'LOW_EXTRACTION_CONFIDENCE', severity: ValidationSeverity.Warning, message: 'Confirm the supplier name.', fields: ['supplierName'], data: { field: 'supplierName', confidence: 0.75, confidenceBand: 'medium' } },
  ] }, corrections: [], processingFailure: null, decision: null,
} as unknown as InvoiceDetailDto;

function renderRoute(detail = invoice) {
  vi.spyOn(invoiceApi, 'getInvoice').mockResolvedValue(detail);
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter([{ path: '/invoices/:invoiceId', element: <InvoiceRoute /> }], { initialEntries: ['/invoices/invoice-001'] });
  return render(<QueryClientProvider client={queryClient}><RouterProvider router={router} /></QueryClientProvider>);
}

afterEach(() => vi.restoreAllMocks());

describe('invoice review foundation', () => {
  it('preserves exact draft values and presents every editable group with metadata', async () => {
    renderRoute();
    await screen.findByDisplayValue('001');
    expect(screen.getByLabelText('Subtotal')).toHaveValue('10.00');
    expect(screen.getByLabelText('Tax amount')).toHaveValue('0.00');
    expect(screen.getByDisplayValue('INV-001')).toBeInTheDocument();
    expect(screen.getByLabelText('Supplier name')).toHaveValue('Northwind');
    expect(screen.getByText('Corrected from: Northwind Supply')).toBeInTheDocument();
    expect(screen.getByText('medium confidence (75%)')).toBeInTheDocument();
    expect(screen.getByText('unknown confidence (no score)')).toBeInTheDocument();
    expect(screen.getByText('Source: Reviewer')).toBeInTheDocument();
    expect(screen.getByText(/Normalized payment-term days: 30/)).toBeInTheDocument();
    expect(screen.getByText(/Draft version: 2/)).toBeInTheDocument();
  });

  it('keeps validation severity distinct from confidence and displays backend summary counts', async () => {
    renderRoute();
    await screen.findByText('Blocking error:');
    expect(screen.getByText('Warning:', { exact: false })).toBeInTheDocument();
    expect(screen.getByLabelText('Review summary')).toHaveTextContent('Extracted fields10Warnings1Blocking errors1Manual corrections2');
  });

  it('renders a safe persisted processing failure when there are no extracted fields', async () => {
    renderRoute({ ...invoice, status: InvoiceStatus.ProcessingFailed, fields: null, currentValidation: null, processingFailure: { stage: 'aiExtraction', code: 'AI_TIMEOUT', message: 'Extraction timed out. Try another invoice.', failedAt: '2026-09-15T10:02:00Z' } });
    await screen.findByRole('alert');
    expect(screen.getByText('Extraction timed out. Try another invoice.')).toBeInTheDocument();
    expect(screen.getByText(/Stage: aiExtraction/)).toBeInTheDocument();
    expect(screen.getByText('Fields are unavailable')).toBeInTheDocument();
  });

  it('formats nulls, zero money, and date-only values without converting their meaning', () => {
    expect(formatMoney(null)).toBe('Not provided');
    expect(formatMoney('0.00')).toBe('0.00');
    expect(formatDate('2026-09-01')).toBe('01.09.2026');
    expect(formatDate(null)).toBe('Not provided');
    expect(confidenceLabel(field(null, { confidence: null, confidenceBand: ConfidenceBand.Unknown }))).toBe('unknown confidence (no score)');
  });

  it('shows a recoverable route error when the detail request fails', async () => {
    vi.spyOn(invoiceApi, 'getInvoice').mockRejectedValue(new Error('unavailable'));
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const router = createMemoryRouter([{ path: '/invoices/:invoiceId', element: <InvoiceRoute /> }], { initialEntries: ['/invoices/invoice-001'] });
    render(<QueryClientProvider client={queryClient}><RouterProvider router={router} /></QueryClientProvider>);
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('Invoice review could not load'));
  });
});
