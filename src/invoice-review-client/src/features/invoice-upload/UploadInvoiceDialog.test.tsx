import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { InvoiceApiError, invoiceQueryKeys, type InvoiceApi } from '../../api/invoiceApi';
import { InvoiceStatus, ProcessingFailureCode, ProcessingStage, type InvoiceDetailDto } from '../../api/generated/client';
import { UploadInvoiceDialog } from './UploadInvoiceDialog';

const reviewableInvoice = { id: 'invoice-42', status: InvoiceStatus.ReviewRequired, processingFailure: null } as InvoiceDetailDto;
const failedInvoice = {
  id: 'invoice-failed', status: InvoiceStatus.ProcessingFailed,
  processingFailure: { code: ProcessingFailureCode.AI_UNAVAILABLE, stage: ProcessingStage.AiExtraction, message: 'Extraction could not be completed safely.', failedAt: '2026-01-01T00:00:00Z' },
} as InvoiceDetailDto;

function Location() { return <p data-testid="location">{useLocation().pathname}</p>; }

function renderDialog(upload = vi.fn().mockResolvedValue(reviewableInvoice)) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/']}>
        <Routes><Route path="*" element={<><UploadInvoiceDialog api={{ upload } as Pick<InvoiceApi, 'upload'>} /><Location /></>} /></Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return { queryClient, upload };
}

describe('UploadInvoiceDialog', () => {
  it('supports replacement and explains empty or multiple file selections', async () => {
    const user = userEvent.setup();
    renderDialog();
    await user.click(screen.getByRole('button', { name: 'Upload invoice' }));
    const input = screen.getByLabelText('Invoice PDF');
    const first = new File(['one'], 'one.pdf', { type: 'application/pdf' });
    const replacement = new File(['two'], 'two.pdf', { type: 'application/pdf' });
    await user.upload(input, first);
    expect(screen.getByText('Selected: one.pdf')).toBeInTheDocument();
    await user.upload(input, replacement);
    expect(screen.getByText('Selected: two.pdf')).toBeInTheDocument();
    fireEvent.change(input, { target: { files: [] } });
    expect(screen.getByRole('alert')).toHaveTextContent('Choose one PDF');
    fireEvent.change(input, { target: { files: [first, replacement] } });
    expect(screen.getByRole('alert')).toHaveTextContent('only one PDF');
  });

  it('shows progress, prevents duplicate submission, invalidates the queue, and navigates on success', async () => {
    let resolveUpload!: (invoice: InvoiceDetailDto) => void;
    const upload = vi.fn(() => new Promise<InvoiceDetailDto>((resolve) => { resolveUpload = resolve; }));
    const { queryClient } = renderDialog(upload);
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'Upload invoice' }));
    await user.upload(screen.getByLabelText('Invoice PDF'), new File(['pdf'], 'invoice.pdf', { type: 'application/pdf' }));
    await user.click(screen.getByRole('button', { name: 'Upload invoice' }));
    expect(screen.getByRole('status')).toHaveTextContent('Uploading and processing');
    expect(screen.getByRole('button', { name: 'Uploading…' })).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Uploading…' }));
    expect(upload).toHaveBeenCalledTimes(1);
    resolveUpload(reviewableInvoice);
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/invoices/invoice-42'));
    expect(invalidate).toHaveBeenCalledWith({ queryKey: invoiceQueryKeys.queues() });
  });

  it('renders safe server problem details and keeps the selection available', async () => {
    const upload = vi.fn().mockRejectedValue(new InvoiceApiError({ status: 413, title: 'File too large', detail: 'The selected file exceeds the allowed size.', code: 'PDF_SIZE_LIMIT_EXCEEDED', fields: null, currentVersion: null, correlationId: 'correlation-42' }));
    renderDialog(upload);
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'Upload invoice' }));
    await user.upload(screen.getByLabelText('Invoice PDF'), new File(['pdf'], 'large.pdf', { type: 'application/pdf' }));
    await user.click(screen.getByRole('button', { name: 'Upload invoice' }));
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('File too large'));
    expect(screen.getByRole('alert')).toHaveTextContent('PDF_SIZE_LIMIT_EXCEEDED');
    expect(screen.getByText('Selected: large.pdf')).toBeInTheDocument();
  });

  it('navigates a processing-failed creation to its invoice record', async () => {
    const { upload } = renderDialog(vi.fn().mockResolvedValue(failedInvoice));
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'Upload invoice' }));
    await user.upload(screen.getByLabelText('Invoice PDF'), new File(['pdf'], 'invoice.pdf', { type: 'application/pdf' }));
    await user.click(screen.getByRole('button', { name: 'Upload invoice' }));
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/invoices/invoice-failed'));
    expect(upload).toHaveBeenCalledOnce();
  });
});
