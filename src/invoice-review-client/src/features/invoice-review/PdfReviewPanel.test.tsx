import { act, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { invoiceApi } from '../../api/invoiceApi';
import { PdfReviewPanel } from './PdfReviewPanel';

let documentProps: Record<string, unknown> = {};
vi.mock('react-pdf', () => ({
  pdfjs: { GlobalWorkerOptions: {} },
  Document: (props: Record<string, unknown>) => { documentProps = props; return <div data-testid="pdf-document">{props.children as React.ReactNode}</div>; },
  Page: ({ pageNumber }: { pageNumber: number }) => <div data-testid={`page-${pageNumber}`} />,
}));

afterEach(() => { vi.restoreAllMocks(); documentProps = {}; });

describe('PdfReviewPanel', () => {
  it('loads the ranged document endpoint using local PDF.js assets', () => {
    vi.spyOn(invoiceApi, 'documentUrl').mockReturnValue('/api/invoices/invoice-001/document');
    render(<PdfReviewPanel invoiceId="invoice-001" documentName="INV-001.pdf" />);
    expect(invoiceApi.documentUrl).toHaveBeenCalledWith('invoice-001');
    expect(documentProps.file).toBe('/api/invoices/invoice-001/document');
    expect(documentProps.options).toMatchObject({ cMapUrl: expect.stringContaining('pdfjs/cmaps/'), standardFontDataUrl: expect.stringContaining('pdfjs/standard_fonts/') });
    expect((documentProps.options as { cMapUrl: string }).cMapUrl).not.toMatch(/cdn/i);
  });

  it('supports keyboard-operable pagination, zoom, and persistent panel sizing', async () => {
    const user = userEvent.setup();
    const { rerender } = render(<PdfReviewPanel invoiceId="invoice-001" documentName="INV-001.pdf" />);
    (documentProps.onLoadSuccess as ({ numPages }: { numPages: number }) => void)({ numPages: 3 });
    await screen.findByText('Page 1 of 3');
    await user.click(screen.getByRole('button', { name: 'Next page' }));
    expect(screen.getByText('Page 2 of 3')).toBeInTheDocument();
    expect(screen.getByTestId('page-3')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Zoom in' }));
    expect(screen.getByText('110%')).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('PDF panel width'), { target: { value: '49' } });
    expect(screen.getByLabelText('PDF panel width')).toHaveValue('49');
    rerender(<PdfReviewPanel invoiceId="invoice-001" documentName="INV-001.pdf" />);
    expect(screen.getByText('Page 2 of 3')).toBeInTheDocument();
    expect(screen.getByText('110%')).toBeInTheDocument();
    expect(screen.getByLabelText('PDF panel width')).toHaveValue('49');
  });

  it('shows safe unavailable and load failure states without requesting an unavailable document', () => {
    const url = vi.spyOn(invoiceApi, 'documentUrl');
    const { rerender } = render(<PdfReviewPanel invoiceId="invoice-001" documentName="INV-001.pdf" documentIntegrityStatus="missing" />);
    expect(screen.getByText('Source invoice unavailable')).toBeInTheDocument();
    expect(url).not.toHaveBeenCalled();
    rerender(<PdfReviewPanel invoiceId="invoice-001" documentName="INV-001.pdf" />);
    act(() => { (documentProps.onLoadError as () => void)(); });
    expect(screen.getByRole('alert')).toHaveTextContent('PDF could not load');
  });
});
