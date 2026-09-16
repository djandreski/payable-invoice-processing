import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { useForm } from 'react-hook-form';
import { createMemoryRouter, Link, RouterProvider, useLocation } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { InvoiceStatus, type InvoiceDetailDto, type InvoiceDraftInputDto } from '../../api/generated/client';
import { InvoiceApiError, invoiceApi } from '../../api/invoiceApi';
import { DraftSaveWorkflow, shouldBlockDraftNavigation } from './DraftSaveWorkflow';

const draft: InvoiceDraftInputDto = { supplier: { name: 'Northwind', registrationId: null }, reference: { invoiceNumber: 'INV-1', purchaseOrderNumber: null }, datesAndTerms: { invoiceDate: '2026-09-01', dueDate: null, paymentTerms: null }, amounts: { currency: 'USD', subtotal: '10.00', taxAmount: '0.00', total: '10.00' }, reviewNotes: null };
const invoice = { id: 'invoice-1', draftVersion: 2, status: InvoiceStatus.ReviewRequired, fields: { supplier: { name: { value: 'Northwind' }, registrationId: { value: null } }, reference: { invoiceNumber: { value: 'INV-1' }, purchaseOrderNumber: { value: null } }, datesAndTerms: { invoiceDate: { value: '2026-09-01' }, dueDate: { value: null }, paymentTerms: { value: null } }, amounts: { currency: { value: 'USD' }, subtotal: { value: '10.00' }, taxAmount: { value: '0.00' }, total: { value: '10.00' } } }, reviewNotes: null } as unknown as InvoiceDetailDto;

function Harness() {
  const form = useForm<InvoiceDraftInputDto>({ defaultValues: draft });
  const location = useLocation();
  return <form><input aria-label="Supplier name" {...form.register('supplier.name')} /><DraftSaveWorkflow invoice={invoice} form={form} /><Link to="/other">Leave review</Link><span data-testid="location">{location.pathname}</span></form>;
}
function renderWorkflow() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter([{ path: '*', element: <Harness /> }], { initialEntries: ['/invoices/invoice-1'] });
  return render(<QueryClientProvider client={queryClient}><RouterProvider router={router} /></QueryClientProvider>);
}

afterEach(() => vi.restoreAllMocks());

describe('draft save workflow', () => {
  it('sends the complete draft and current expected version, then resets only after success', async () => {
    const saved = { ...invoice, draftVersion: 3, fields: { ...invoice.fields, supplier: { ...invoice.fields!.supplier, name: { value: 'Contoso' } } } } as InvoiceDetailDto;
    const saveDraft = vi.spyOn(invoiceApi, 'saveDraft').mockResolvedValue(saved);
    renderWorkflow();
    fireEvent.change(screen.getByLabelText('Supplier name'), { target: { value: 'Contoso' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
    await waitFor(() => expect(saveDraft).toHaveBeenCalledWith('invoice-1', { expectedVersion: 2, draft: { ...draft, supplier: { name: 'Contoso', registrationId: null } } }));
    await waitFor(() => expect(screen.getByText('All changes are saved.')).toBeInTheDocument());
  });

  it('saves an unchanged complete draft and applies the returned readiness and correction state to the cache', async () => {
    const saved = { ...invoice, status: InvoiceStatus.ReadyForApproval, draftVersion: 2, fields: { ...invoice.fields, supplier: { ...invoice.fields!.supplier, name: { value: 'Northwind', originalValue: 'Extracted Northwind', differsFromOriginal: true } } } } as InvoiceDetailDto;
    const saveDraft = vi.spyOn(invoiceApi, 'saveDraft').mockResolvedValue(saved);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const router = createMemoryRouter([{ path: '*', element: <Harness /> }], { initialEntries: ['/invoices/invoice-1'] });
    render(<QueryClientProvider client={queryClient}><RouterProvider router={router} /></QueryClientProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
    await waitFor(() => expect(saveDraft).toHaveBeenCalledWith('invoice-1', { expectedVersion: 2, draft }));
    await waitFor(() => expect(queryClient.getQueryData(['invoices', 'detail', 'invoice-1'])).toEqual(saved));
    const cached = queryClient.getQueryData(['invoices', 'detail', 'invoice-1']) as InvoiceDetailDto;
    expect(cached.status).toBe(InvoiceStatus.ReadyForApproval);
    expect(cached.fields!.supplier.name.originalValue).toBe('Extracted Northwind');
  });

  it('preserves dirty input and offers an explicit refetch after a conflict', async () => {
    vi.spyOn(invoiceApi, 'saveDraft').mockRejectedValue(new InvoiceApiError({ status: 409, code: 'INVOICE_VERSION_CONFLICT', title: 'Conflict', detail: 'Version conflict.', fields: null, currentVersion: 4, correlationId: null }));
    renderWorkflow();
    fireEvent.change(screen.getByLabelText('Supplier name'), { target: { value: 'Contoso' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
    await screen.findByText(/Your edits have been kept/);
    await waitFor(() => expect(document.activeElement).toBe(screen.getByRole('alert')));
    expect(screen.getByLabelText('Supplier name')).toHaveValue('Contoso');
    expect(screen.getByText('Current server version: 4')).toBeInTheDocument();
    const getInvoice = vi.spyOn(invoiceApi, 'getInvoice').mockResolvedValue({ ...invoice, draftVersion: 4 } as InvoiceDetailDto);
    fireEvent.click(screen.getByRole('button', { name: 'Refetch current record' }));
    await waitFor(() => expect(getInvoice).toHaveBeenCalledWith('invoice-1'));
  });

  it('keeps the draft editable and exposes field-specific validation errors', async () => {
    vi.spyOn(invoiceApi, 'saveDraft').mockRejectedValue(new InvoiceApiError({ status: 400, code: 'REQUEST_VALIDATION_FAILED', title: 'Invalid request', detail: 'Draft is invalid.', fields: { 'draft.supplier.name': ['Supplier name is invalid.'] }, currentVersion: null, correlationId: null }));
    renderWorkflow();
    fireEvent.change(screen.getByLabelText('Supplier name'), { target: { value: 'Contoso' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
    await waitFor(() => expect(screen.getByText('Draft is invalid.')).toBeInTheDocument());
    expect(screen.getByLabelText('Draft field errors')).toHaveTextContent('Supplier name is invalid.');
    expect(screen.getByLabelText('Supplier name')).toHaveValue('Contoso');
  });

  it('prevents duplicate saves while a save is active', async () => {
    let finish: ((saved: InvoiceDetailDto) => void) | undefined;
    const pending = new Promise<InvoiceDetailDto>((resolve) => { finish = resolve; });
    const saveDraft = vi.spyOn(invoiceApi, 'saveDraft').mockReturnValue(pending);
    renderWorkflow();
    fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Saving draft…' })).toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: 'Saving draft…' }));
    expect(saveDraft).toHaveBeenCalledTimes(1);
    finish!(invoice);
  });

  it('registers an unload warning only while the draft is dirty', () => {
    const add = vi.spyOn(window, 'addEventListener');
    renderWorkflow();
    expect(add).not.toHaveBeenCalledWith('beforeunload', expect.any(Function));
    fireEvent.change(screen.getByLabelText('Supplier name'), { target: { value: 'Contoso' } });
    expect(add).toHaveBeenCalledWith('beforeunload', expect.any(Function));
  });

  it('exposes a router-level dirty-navigation predicate without taking ownership of routing', () => {
    expect(shouldBlockDraftNavigation(true)).toBe(true);
    expect(shouldBlockDraftNavigation(true, true)).toBe(false);
    expect(shouldBlockDraftNavigation(false)).toBe(false);
  });

  it('requires an explicit choice before a dirty route transition', async () => {
    renderWorkflow();
    fireEvent.change(screen.getByLabelText('Supplier name'), { target: { value: 'Contoso' } });
    fireEvent.click(screen.getByRole('link', { name: 'Leave review' }));
    expect(await screen.findByRole('dialog', { name: 'Discard unsaved changes?' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Keep editing' }));
    expect(screen.getByTestId('location')).toHaveTextContent('/invoices/invoice-1');
    fireEvent.click(screen.getByRole('link', { name: 'Leave review' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Discard and continue' }));
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/other'));
  });
});
