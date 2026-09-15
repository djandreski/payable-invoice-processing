import { QueryClient } from '@tanstack/react-query';
import { describe, expect, it, vi } from 'vitest';
import {
  InvoiceApi,
  InvoiceApiError,
  applyInvoiceMutation,
  applyUploadMutation,
  createCorrelationFetch,
  downloadInvoiceExport,
  invoiceDetailToDraft,
  invoiceQueryKeys,
} from './invoiceApi';
import { Client, type InvoiceDetailDto, type InvoiceProblemDetails, InvoiceStatus } from './generated/client';

const detail = {
  id: 'a/b', status: InvoiceStatus.ReviewRequired, draftVersion: 2, lastValidatedVersion: null,
  createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', document: {}, documentTextSource: null,
  fields: {
    supplier: { name: { value: 'Acme' }, registrationId: { value: '001' } },
    reference: { invoiceNumber: { value: 'INV-01' }, purchaseOrderNumber: { value: null } },
    datesAndTerms: { invoiceDate: { value: '2026-01-01' }, dueDate: { value: null }, paymentTerms: { value: '30' } },
    amounts: { currency: { value: 'USD' }, subtotal: { value: '10.00' }, taxAmount: { value: '0.00' }, total: { value: '10.00' } },
  }, reviewNotes: null, summary: {}, currentValidation: null, corrections: [], processingFailure: null, decision: null,
} as unknown as InvoiceDetailDto;

describe('invoice API adapters', () => {
  it('builds repeated queue statuses and correlation headers through the generated client', async () => {
    const fetcher = vi.fn().mockResolvedValue(new Response(JSON.stringify({ items: [] }), { status: 200 }));
    const api = new InvoiceApi(new Client('https://api.example', createCorrelationFetch(fetcher)));

    await api.listInvoices({ search: ' Acme ', statuses: [InvoiceStatus.ReviewRequired, InvoiceStatus.ReadyForApproval], page: 2, pageSize: 20 });

    expect(fetcher.mock.calls[0][0]).toBe('https://api.example/api/invoices?search=%20Acme%20&status=reviewRequired&status=readyForApproval&page=2&pageSize=20');
    expect(new Headers(fetcher.mock.calls[0][1].headers).get('X-Correlation-ID')).toBeTruthy();
  });

  it('preserves Problem Details conflict data', async () => {
    const problem: InvoiceProblemDetails = { type: 'urn:test', title: 'Changed', status: 409, detail: 'Refresh', instance: '/x', code: 'INVOICE_VERSION_CONFLICT', correlationId: 'corr-1', fields: { expectedVersion: ['stale'] }, currentVersion: 7 };
    const api = new InvoiceApi({ getInvoice: vi.fn().mockRejectedValue(problem) } as unknown as Client);

    await expect(api.getInvoice('id')).rejects.toMatchObject<Partial<InvoiceApiError>>({ status: 409, code: 'INVOICE_VERSION_CONFLICT', currentVersion: 7, correlationId: 'corr-1', fields: { expectedVersion: ['stale'] } });
  });

  it('maps generated detail values into the complete form draft without changing values', () => {
    expect(invoiceDetailToDraft(detail)).toEqual({ supplier: { name: 'Acme', registrationId: '001' }, reference: { invoiceNumber: 'INV-01', purchaseOrderNumber: null }, datesAndTerms: { invoiceDate: '2026-01-01', dueDate: null, paymentTerms: '30' }, amounts: { currency: 'USD', subtotal: '10.00', taxAmount: '0.00', total: '10.00' }, reviewNotes: null });
    expect(invoiceDetailToDraft({ ...detail, fields: null })).toBeNull();
  });

  it('keeps document responses binary and uses an encoded document URL', async () => {
    const blob = new Blob(['pdf'], { type: 'application/pdf' });
    const api = new InvoiceApi({ getInvoiceDocument: vi.fn().mockResolvedValue({ data: blob, status: 206 }) } as unknown as Client);
    await expect(api.getDocument('a/b')).resolves.toMatchObject({ data: blob, status: 206 });
    expect(api.documentUrl('a/b')).toBe('/api/invoices/a%2Fb/document');
  });

  it('downloads exports as an attachment while retaining correlation behavior', async () => {
    const fetcher = vi.fn().mockResolvedValue(new Response('{"schemaVersion":"1.0"}', {
      status: 200,
      headers: { 'Content-Disposition': 'attachment; filename="invoice-a.json"' },
    }));
    await expect(downloadInvoiceExport('a', fetcher, 'https://api.example')).resolves.toMatchObject({ fileName: 'invoice-a.json' });
    expect(new Headers(fetcher.mock.calls[0][1].headers).get('X-Correlation-ID')).toBeTruthy();
  });

  it('applies the defined cache effects for upload and every invoice mutation', async () => {
    const queryClient = new QueryClient();
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    await applyUploadMutation(queryClient, detail);
    expect(queryClient.getQueryData(invoiceQueryKeys.detail('a/b'))).toBe(detail);
    expect(invalidate).toHaveBeenCalledWith({ queryKey: invoiceQueryKeys.queues() });

    invalidate.mockClear();
    await applyInvoiceMutation(queryClient, detail);
    expect(invalidate).toHaveBeenCalledWith({ queryKey: invoiceQueryKeys.queues() });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: invoiceQueryKeys.histories() });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: invoiceQueryKeys.exports() });
  });
});
