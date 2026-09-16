import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { FormProvider, useForm } from 'react-hook-form';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { InvoiceStatus, type InvoiceDetailDto, type InvoiceDraftInputDto } from '../../api/generated/client';
import { InvoiceApiError, invoiceApi, invoiceQueryKeys } from '../../api/invoiceApi';
import { assertNoSeriousAccessibilityViolations } from '../../test/accessibility';
import { canApproveInvoice, canRejectInvoice, DecisionWorkflow } from './DecisionWorkflow';

const draft: InvoiceDraftInputDto = { supplier: { name: 'Northwind', registrationId: null }, reference: { invoiceNumber: 'INV-1', purchaseOrderNumber: null }, datesAndTerms: { invoiceDate: '2026-09-01', dueDate: null, paymentTerms: null }, amounts: { currency: 'USD', subtotal: '10.00', taxAmount: '0.00', total: '10.00' }, reviewNotes: null };
const invoice = { id: 'invoice-1', draftVersion: 2, status: InvoiceStatus.ReadyForApproval, fields: {}, currentValidation: { warningCount: 1, errorCount: 0, results: [] } } as unknown as InvoiceDetailDto;

function Harness({ detail = invoice }: { detail?: InvoiceDetailDto }) {
  const form = useForm<InvoiceDraftInputDto>({ defaultValues: draft });
  return <FormProvider {...form}><input aria-label="Invoice number" {...form.register('reference.invoiceNumber')} /><DecisionWorkflow invoice={detail} /></FormProvider>;
}

function renderWorkflow(detail = invoice) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const rendered = render(<QueryClientProvider client={queryClient}><Harness detail={detail} /></QueryClientProvider>);
  return { queryClient, ...rendered };
}

afterEach(() => vi.restoreAllMocks());

describe('approval and rejection workflow', () => {
  it('has no serious automated accessibility violations in decision dialogs', async () => {
    const { container } = renderWorkflow();
    fireEvent.click(screen.getByRole('button', { name: 'Approve' }));
    await assertNoSeriousAccessibilityViolations(container);
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    fireEvent.click(screen.getByRole('button', { name: 'Reject' }));
    await assertNoSeriousAccessibilityViolations(container);
  });
  it('gates approval from the server status while allowing warnings-only approval', () => {
    renderWorkflow();
    expect(screen.getByRole('button', { name: 'Approve' })).toBeEnabled();
    expect(canApproveInvoice(InvoiceStatus.ReadyForApproval)).toBe(true);
    expect(canApproveInvoice(InvoiceStatus.ReviewRequired)).toBe(false);
    expect(canApproveInvoice(InvoiceStatus.Approved)).toBe(false);
    expect(canRejectInvoice(InvoiceStatus.ReviewRequired)).toBe(true);
    expect(canRejectInvoice(InvoiceStatus.ReadyForApproval)).toBe(true);
    expect(canRejectInvoice(InvoiceStatus.ProcessingFailed)).toBe(false);
    renderWorkflow({ ...invoice, status: InvoiceStatus.ReviewRequired });
    expect(screen.getAllByRole('button', { name: 'Approve' })[1]).toBeDisabled();
  });

  it('blocks both decisions while the draft is dirty', () => {
    renderWorkflow();
    fireEvent.change(screen.getByLabelText('Invoice number'), { target: { value: 'INV-2' } });
    expect(screen.getByRole('button', { name: 'Approve' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Reject' })).toBeDisabled();
    expect(screen.getByText('Save your draft before making a decision.')).toBeInTheDocument();
  });

  it('requires confirmation and restores focus to the approval trigger when cancelled', async () => {
    renderWorkflow();
    const trigger = screen.getByRole('button', { name: 'Approve' });
    trigger.focus();
    fireEvent.click(trigger);
    expect(await screen.findByRole('dialog', { name: 'Approve invoice?' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(document.activeElement).toBe(trigger);
  });

  it('requires a non-blank, trimmed rejection reason before calling the API', async () => {
    const reject = vi.spyOn(invoiceApi, 'reject').mockResolvedValue({ ...invoice, status: InvoiceStatus.Rejected } as InvoiceDetailDto);
    renderWorkflow();
    fireEvent.click(screen.getByRole('button', { name: 'Reject' }));
    fireEvent.change(screen.getByLabelText('Rejection reason'), { target: { value: '   ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Confirm rejection' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Enter a reason');
    expect(reject).not.toHaveBeenCalled();
    fireEvent.change(screen.getByLabelText('Rejection reason'), { target: { value: '  Duplicate invoice  ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Confirm rejection' }));
    await waitFor(() => expect(reject).toHaveBeenCalledWith('invoice-1', { expectedVersion: 2, reason: 'Duplicate invoice' }));
  });

  it('preserves decision context and exposes explicit recovery for blocked and stale decisions', async () => {
    const approve = vi.spyOn(invoiceApi, 'approve').mockRejectedValue(new InvoiceApiError({ status: 409, code: 'APPROVAL_BLOCKED', title: 'Blocked', detail: 'Blocked', fields: { 'draft.amounts.total': ['Total does not reconcile.'] }, currentVersion: 2, correlationId: null }));
    const getInvoice = vi.spyOn(invoiceApi, 'getInvoice').mockResolvedValue({ ...invoice, draftVersion: 3 } as InvoiceDetailDto);
    renderWorkflow();
    fireEvent.click(screen.getByRole('button', { name: 'Approve' }));
    fireEvent.click(screen.getByRole('button', { name: 'Confirm approval' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Approval is blocked');
    expect(screen.getByText('Total does not reconcile.')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Refetch current record' }));
    await waitFor(() => expect(getInvoice).toHaveBeenCalledWith('invoice-1'));
    await waitFor(() => expect(approve).toHaveBeenCalledTimes(1));
  });

  it('prevents duplicate confirmations and updates the authoritative terminal detail cache on success', async () => {
    let resolveApproval: ((value: InvoiceDetailDto) => void) | undefined;
    const approval = new Promise<InvoiceDetailDto>((resolve) => { resolveApproval = resolve; });
    const approve = vi.spyOn(invoiceApi, 'approve').mockReturnValue(approval);
    const { queryClient } = renderWorkflow();
    fireEvent.click(screen.getByRole('button', { name: 'Approve' }));
    const confirm = screen.getByRole('button', { name: 'Confirm approval' });
    fireEvent.click(confirm);
    fireEvent.click(confirm);
    await waitFor(() => expect(approve).toHaveBeenCalledTimes(1));
    resolveApproval?.({ ...invoice, status: InvoiceStatus.Approved, decision: { kind: 'approved', decidedAt: '2026-09-15T10:02:00.000Z', rejectionReason: null } } as InvoiceDetailDto);
    await waitFor(() => expect(queryClient.getQueryData(invoiceQueryKeys.detail('invoice-1'))).toMatchObject({ status: InvoiceStatus.Approved }));
  });
});
