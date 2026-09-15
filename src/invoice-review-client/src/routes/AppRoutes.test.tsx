import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { invoiceApi } from '../api/invoiceApi';
import { buildInvoice, emptyQueue } from '../test/invoiceBuilders';
import { AppRoutes } from './AppRoutes';

vi.mock('../features/invoice-review/PdfReviewPanel', () => ({ PdfReviewPanel: () => <section aria-label="Source invoice">PDF preview</section> }));

function renderRoutes(entry: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter([{ path: '*', element: <AppRoutes /> }], { initialEntries: [entry] });
  render(<QueryClientProvider client={queryClient}><RouterProvider router={router} /></QueryClientProvider>);
}

afterEach(() => vi.restoreAllMocks());

describe('application routes', () => {
  it('renders the connected invoice queue and upload action', async () => {
    vi.spyOn(invoiceApi, 'listInvoices').mockResolvedValue(emptyQueue());
    renderRoutes('/');

    expect(await screen.findByRole('heading', { name: 'Invoice queue' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Upload invoice' })).toBeEnabled();
    expect(screen.getByRole('navigation', { name: 'Primary navigation' })).toBeInTheDocument();
  });

  it('loads the invoice workspace through the detail adapter', async () => {
    vi.spyOn(invoiceApi, 'getInvoice').mockResolvedValue(buildInvoice());
    renderRoutes('/invoices/invoice-001');

    expect(await screen.findByRole('form', { name: 'Invoice draft' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Invoice review' })).toBeInTheDocument();
    expect(screen.getByLabelText('Source invoice')).toBeInTheDocument();
    expect(screen.getByDisplayValue('INV-001')).toBeInTheDocument();
  });
});
