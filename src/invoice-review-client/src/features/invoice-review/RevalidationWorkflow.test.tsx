import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { FormProvider, useForm } from 'react-hook-form';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { InvoiceStatus, ValidationSeverity, type InvoiceDetailDto, type InvoiceDraftInputDto } from '../../api/generated/client';
import { InvoiceApiError, invoiceApi } from '../../api/invoiceApi';
import { canRevalidateInvoice, firstBlockingField, RevalidationWorkflow } from './RevalidationWorkflow';

const draft: InvoiceDraftInputDto = { supplier: { name: 'Northwind', registrationId: null }, reference: { invoiceNumber: 'INV-1', purchaseOrderNumber: null }, datesAndTerms: { invoiceDate: '2026-09-01', dueDate: null, paymentTerms: null }, amounts: { currency: 'USD', subtotal: '10.00', taxAmount: '0.00', total: '10.00' }, reviewNotes: null };
const invoice = { id: 'invoice-1', draftVersion: 2, status: InvoiceStatus.ReviewRequired, fields: {}, currentValidation: null } as unknown as InvoiceDetailDto;

function Harness({ detail = invoice }: { detail?: InvoiceDetailDto }) {
  const form = useForm<InvoiceDraftInputDto>({ defaultValues: draft });
  return <FormProvider {...form}><input id="reference.invoiceNumber" aria-label="Invoice number" {...form.register('reference.invoiceNumber')} /><RevalidationWorkflow invoice={detail} /></FormProvider>;
}
function renderWorkflow(detail = invoice) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<QueryClientProvider client={queryClient}><Harness detail={detail} /></QueryClientProvider>);
  return queryClient;
}

afterEach(() => vi.restoreAllMocks());

describe('revalidation workflow', () => {
  it('submits the saved version, updates the detail cache, and focuses the first blocking field', async () => {
    const validated = { ...invoice, status: InvoiceStatus.ReviewRequired, currentValidation: { results: [
      { code: 'REQUIRED_FIELD_MISSING', severity: ValidationSeverity.Error, fields: ['invoiceNumber'] },
      { code: 'DUE_DATE_BEFORE_INVOICE_DATE', severity: ValidationSeverity.Error, fields: ['dueDate', 'invoiceNumber'] },
    ] } } as unknown as InvoiceDetailDto;
    const validate = vi.spyOn(invoiceApi, 'validate').mockResolvedValue(validated);
    const queryClient = renderWorkflow();
    fireEvent.click(screen.getByRole('button', { name: 'Revalidate' }));
    await waitFor(() => expect(validate).toHaveBeenCalledWith('invoice-1', { expectedVersion: 2 }));
    await waitFor(() => expect(queryClient.getQueryData(['invoices', 'detail', 'invoice-1'])).toEqual(validated));
    await waitFor(() => expect(document.activeElement).toBe(screen.getByLabelText('Invoice number')));
  });

  it('prevents revalidation while a draft is dirty and while the server status is terminal', () => {
    renderWorkflow();
    fireEvent.change(screen.getByLabelText('Invoice number'), { target: { value: 'INV-2' } });
    expect(screen.getByRole('button', { name: 'Revalidate' })).toBeDisabled();
    expect(screen.getByText('Save your draft before revalidating.')).toBeInTheDocument();
    expect(canRevalidateInvoice(InvoiceStatus.ReadyForApproval)).toBe(true);
    expect(canRevalidateInvoice(InvoiceStatus.Approved)).toBe(false);
  });

  it('preserves user context and offers an explicit refetch after a stale validation conflict', async () => {
    vi.spyOn(invoiceApi, 'validate').mockRejectedValue(new InvoiceApiError({ status: 409, code: 'VALIDATION_STALE', title: 'Stale', detail: 'Stale validation.', fields: null, currentVersion: 3, correlationId: null }));
    renderWorkflow();
    fireEvent.click(screen.getByRole('button', { name: 'Revalidate' }));
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Your unsaved edits have been kept');
    await waitFor(() => expect(document.activeElement).toBe(alert));
    expect(screen.getByText('Current server version: 3')).toBeInTheDocument();
    const getInvoice = vi.spyOn(invoiceApi, 'getInvoice').mockResolvedValue({ ...invoice, draftVersion: 3 } as InvoiceDetailDto);
    fireEvent.click(screen.getByRole('button', { name: 'Refetch current record' }));
    await waitFor(() => expect(getInvoice).toHaveBeenCalledWith('invoice-1'));
  });

  it('uses server validation fields for stable linking without calculating rules', () => {
    expect(firstBlockingField([
      { code: 'LOW_EXTRACTION_CONFIDENCE', severity: ValidationSeverity.Warning, fields: ['supplierName'] },
      { code: 'REQUIRED_FIELD_MISSING', severity: ValidationSeverity.Error, fields: ['invoiceNumber'] },
    ] as never)).toBe('reference.invoiceNumber');
  });
});
