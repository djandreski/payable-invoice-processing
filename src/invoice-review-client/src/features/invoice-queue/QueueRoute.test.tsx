import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { invoiceApi } from '../../api/invoiceApi';
import { InvoiceStatus, type InvoiceQueuePageDto } from '../../api/generated/client';
import { QueueRoute } from './QueueRoute';

vi.mock('../invoice-upload/UploadInvoiceDialog', () => ({ UploadInvoiceDialog: () => <button type="button">Upload invoice</button> }));

const summary = { totalInvoiceCount: 1, processingCount: 0, reviewRequiredCount: 0, readyForApprovalCount: 1, approvedCount: 0, rejectedCount: 0, processingFailedCount: 0, pendingReviewCount: 1, warningInvoiceCount: 1, errorInvoiceCount: 0 };
const page: InvoiceQueuePageDto = { items: [{ id: 'inv-1', status: InvoiceStatus.ReadyForApproval, supplierName: 'Northwind', invoiceNumber: 'INV-001', invoiceDate: '2026-09-01', total: '1180.00', currency: 'EUR', draftVersion: 2, warningCount: 1, errorCount: 0, exceptionCount: 1, processingFailure: null, createdAt: '2026-09-01T10:00:00Z', updatedAt: '2026-09-02T10:00:00Z' }], summary, page: 2, pageSize: 25, totalItems: 26, totalPages: 2, hasPreviousPage: true, hasNextPage: false };

function renderQueue(entry = '/'): void {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<QueryClientProvider client={queryClient}><MemoryRouter initialEntries={[entry]}><QueueRoute /></MemoryRouter></QueryClientProvider>);
}

describe('QueueRoute', () => {
  it('restores filters from the route and renders formatted, color-independent queue data', async () => {
    const list = vi.spyOn(invoiceApi, 'listInvoices').mockResolvedValue(page);
    renderQueue('/?search=%20Northwind%20&status=readyForApproval&page=2');

    expect(await screen.findByText('Northwind')).toBeInTheDocument();
    expect(list).toHaveBeenCalledWith(expect.objectContaining({ search: 'Northwind', statuses: [InvoiceStatus.ReadyForApproval], page: 2 }));
    expect(screen.getByDisplayValue('Northwind')).toBeInTheDocument();
    expect(screen.getAllByText('Ready for approval')).toHaveLength(2);
    expect(screen.getByText('1 exception')).toBeInTheDocument();
    expect(screen.getByRole('cell', { name: /€/ })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Northwind' })).toHaveAttribute('href', '/invoices/inv-1');
  });

  it('applies trimmed searches, multi-status filters, clearing, and deterministic pagination', async () => {
    vi.spyOn(invoiceApi, 'listInvoices').mockResolvedValue(page);
    const user = userEvent.setup();
    renderQueue('/');
    await screen.findByText('Northwind');

    await user.clear(screen.getByLabelText('Search supplier or invoice number'));
    await user.type(screen.getByLabelText('Search supplier or invoice number'), '  Acme  ');
    await user.click(screen.getByRole('button', { name: 'Search' }));
    await waitFor(() => expect(invoiceApi.listInvoices).toHaveBeenLastCalledWith(expect.objectContaining({ search: 'Acme', page: 1 })));

    const statusSelect = screen.getByLabelText('Status filters') as HTMLSelectElement;
    statusSelect.options[1].selected = true;
    statusSelect.options[2].selected = true;
    fireEvent.change(statusSelect);
    await waitFor(() => expect(invoiceApi.listInvoices).toHaveBeenLastCalledWith(expect.objectContaining({ statuses: [InvoiceStatus.ReviewRequired, InvoiceStatus.ReadyForApproval], page: 1 })));
    await screen.findByText('Northwind');
    await user.click(screen.getByRole('button', { name: 'Previous page' }));
    await waitFor(() => expect(invoiceApi.listInvoices).toHaveBeenLastCalledWith(expect.objectContaining({ page: 1 })));
    await user.click(screen.getByRole('button', { name: 'Clear filters' }));
    await waitFor(() => expect(invoiceApi.listInvoices).toHaveBeenLastCalledWith(expect.objectContaining({ page: 1 })));
    const finalFilters = vi.mocked(invoiceApi.listInvoices).mock.calls.at(-1)?.[0];
    expect(finalFilters).not.toHaveProperty('statuses');
    expect(finalFilters).not.toHaveProperty('search');
  });

  it('distinguishes no invoices, no results, and processing failures', async () => {
    const list = vi.spyOn(invoiceApi, 'listInvoices');
    list.mockResolvedValueOnce({ ...page, items: [], totalItems: 0, totalPages: 0, hasPreviousPage: false, summary: { ...summary, totalInvoiceCount: 0 } });
    renderQueue('/');
    expect(await screen.findByText('Upload an invoice to begin review.')).toBeInTheDocument();

    list.mockResolvedValueOnce({ ...page, items: [], totalItems: 0, totalPages: 0, hasPreviousPage: false });
    renderQueue('/?search=missing');
    expect(await screen.findByText('Try changing or clearing the current filters.')).toBeInTheDocument();

    list.mockResolvedValueOnce({ ...page, items: [{ ...page.items[0], status: InvoiceStatus.ProcessingFailed, processingFailure: { stage: 'ocr', code: 'OCR_FAILED', message: 'Safe failure', occurredAt: '2026-09-02T10:00:00Z' } }], summary: { ...summary, processingFailedCount: 1 } });
    renderQueue('/?status=processingFailed');
    expect(await screen.findByText('Processing failed: OCR_FAILED')).toBeInTheDocument();
  });

  it('does not display stale queue rows after an API error and offers retry', async () => {
    const list = vi.spyOn(invoiceApi, 'listInvoices').mockRejectedValue(new Error('offline'));
    renderQueue('/');
    expect(await screen.findByRole('alert')).toHaveTextContent('We couldn’t load the invoice queue.');
    expect(screen.queryByText('Northwind')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument();
  });
});
