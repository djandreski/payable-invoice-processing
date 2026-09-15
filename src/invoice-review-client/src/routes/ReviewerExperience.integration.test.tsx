import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { InvoiceStatus, ValidationTrigger } from '../api/generated/client';
import { invoiceApi } from '../api/invoiceApi';
import { buildInvoice, emptyQueue } from '../test/invoiceBuilders';
import { AppRoutes } from './AppRoutes';

vi.mock('../features/invoice-review/PdfReviewPanel', () => ({ PdfReviewPanel: ({ invoiceId }: { invoiceId: string }) => <section aria-label="Source invoice">PDF {invoiceId}</section> }));

function renderExperience(entry = '/') {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const router = createMemoryRouter([{ path: '*', element: <AppRoutes /> }], { initialEntries: [entry] });
  render(<QueryClientProvider client={queryClient}><RouterProvider router={router} /></QueryClientProvider>);
  return { queryClient, router };
}

afterEach(() => vi.restoreAllMocks());

describe('integrated reviewer experience', () => {
  it('invalidates the queue and opens the common detail route after upload', async () => {
    const created = buildInvoice();
    vi.spyOn(invoiceApi, 'listInvoices').mockResolvedValue(emptyQueue());
    vi.spyOn(invoiceApi, 'upload').mockResolvedValue(created);
    vi.spyOn(invoiceApi, 'getInvoice').mockResolvedValue(created);
    const user = userEvent.setup();
    const { router } = renderExperience();

    await user.click(await screen.findByRole('button', { name: 'Upload invoice' }));
    await user.upload(screen.getByLabelText('Invoice PDF'), new File(['%PDF'], 'invoice.pdf', { type: 'application/pdf' }));
    await user.click(screen.getAllByRole('button', { name: 'Upload invoice' }).at(-1)!);

    await waitFor(() => expect(router.state.location.pathname).toBe('/invoices/invoice-001'));
    expect(await screen.findByRole('form', { name: 'Invoice draft' })).toBeInTheDocument();
    expect(screen.getByLabelText('Source invoice')).toHaveTextContent('invoice-001');
  });

  it('keeps form and server state separate across save, validate, and terminal approval', async () => {
    const initial = buildInvoice();
    const saved = buildInvoice({ status: InvoiceStatus.ReviewRequired, draftVersion: 2 });
    const validated = buildInvoice({
      status: InvoiceStatus.ReadyForApproval,
      draftVersion: 2,
      lastValidatedVersion: 2,
      currentValidation: {
        id: 'validation-2', draftVersion: 2, validatedAt: '2026-09-15T08:05:00.000Z',
        warningCount: 0, errorCount: 0, results: [], trigger: ValidationTrigger.Explicit,
      } as never,
    });
    const approved = buildInvoice({
      status: InvoiceStatus.Approved,
      draftVersion: 2,
      lastValidatedVersion: 2,
      currentValidation: validated.currentValidation,
      decision: { kind: 'approved', decidedAt: '2026-09-15T08:06:00.000Z', rejectionReason: null },
    });
    vi.spyOn(invoiceApi, 'getInvoice').mockResolvedValue(initial);
    vi.spyOn(invoiceApi, 'saveDraft').mockResolvedValue(saved);
    vi.spyOn(invoiceApi, 'validate').mockResolvedValue(validated);
    vi.spyOn(invoiceApi, 'approve').mockResolvedValue(approved);
    vi.spyOn(invoiceApi, 'getHistory').mockResolvedValue({ items: [], page: 1, pageSize: 50, totalItems: 0, totalPages: 0, hasPreviousPage: false, hasNextPage: false });
    const user = userEvent.setup();
    renderExperience('/invoices/invoice-001');

    const supplier = await screen.findByLabelText('Supplier name');
    await user.clear(supplier);
    await user.type(supplier, 'Contoso');
    expect(screen.getByRole('button', { name: 'Revalidate' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Approve' })).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Save draft' }));
    await waitFor(() => expect(invoiceApi.saveDraft).toHaveBeenCalledWith('invoice-001', expect.objectContaining({ expectedVersion: 1 })));

    await user.click(await screen.findByRole('button', { name: 'Revalidate' }));
    await waitFor(() => expect(invoiceApi.validate).toHaveBeenCalledWith('invoice-001', { expectedVersion: 2 }));
    const approve = await screen.findByRole('button', { name: 'Approve' });
    expect(approve).toBeEnabled();
    await user.click(approve);
    await user.click(await screen.findByRole('button', { name: 'Confirm approval' }));

    expect(await screen.findByRole('heading', { name: 'Approved invoice' })).toBeInTheDocument();
    expect(screen.queryByRole('form', { name: 'Invoice draft' })).not.toBeInTheDocument();
  });
});
