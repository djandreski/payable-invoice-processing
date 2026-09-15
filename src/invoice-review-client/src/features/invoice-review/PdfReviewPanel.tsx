import { useState } from 'react';
import { Document, Page, pdfjs } from 'react-pdf';
import { invoiceApi } from '../../api/invoiceApi';
import './PdfReviewPanel.css';

// Vite emits this worker with the application bundle. CMaps and standard fonts
// are copied from pdfjs-dist into public/pdfjs so the viewer never reaches a CDN.
pdfjs.GlobalWorkerOptions.workerSrc = new URL('pdfjs-dist/build/pdf.worker.min.mjs', import.meta.url).toString();

const localPdfAssets = `${import.meta.env.BASE_URL}pdfjs/`;
const maxDevicePixelRatio = 2;

export type PdfReviewPanelProps = {
  invoiceId: string;
  documentName: string;
  /** Pass the server-provided document integrity state; unavailable files are never requested. */
  documentIntegrityStatus?: string | null;
};

function clampPage(page: number, pageCount: number): number {
  return Math.max(1, Math.min(page, pageCount));
}

export function PdfReviewPanel({ invoiceId, documentName, documentIntegrityStatus = 'available' }: PdfReviewPanelProps) {
  const [pageCount, setPageCount] = useState<number | null>(null);
  const [page, setPage] = useState(1);
  const [zoom, setZoom] = useState(1);
  const [panelWidth, setPanelWidth] = useState(48);
  const [loadError, setLoadError] = useState(false);
  const pixelDensity = Math.min(globalThis.devicePixelRatio || 1, maxDevicePixelRatio);
  const documentUnavailable = documentIntegrityStatus !== 'available';
  const bufferedPage = pageCount && page < pageCount ? page + 1 : pageCount && page > 1 ? page - 1 : null;

  if (documentUnavailable) {
    return <section className="pdf-review-panel pdf-review-panel--unavailable" aria-label="Source invoice">
      <h2>Source invoice unavailable</h2>
      <p>The stored document cannot be opened. You can still inspect the saved review record.</p>
    </section>;
  }

  const documentUrl = invoiceApi.documentUrl(invoiceId);

  return <section className="pdf-review-panel" aria-label="Source invoice" style={{ '--pdf-panel-width': `${panelWidth}%` } as React.CSSProperties}>
    <header className="pdf-review-panel__header">
      <div><h2>Source invoice</h2><p title={documentName}>{documentName}</p></div>
      <a className="text-link" href={documentUrl} target="_blank" rel="noreferrer">Open PDF</a>
    </header>
    <div className="pdf-review-panel__controls" aria-label="PDF controls">
      <button type="button" className="button button--secondary" onClick={() => setPage((current) => clampPage(current - 1, pageCount ?? 1))} disabled={page <= 1}>Previous page</button>
      <span aria-live="polite">Page {page}{pageCount ? ` of ${pageCount}` : ''}</span>
      <button type="button" className="button button--secondary" onClick={() => setPage((current) => clampPage(current + 1, pageCount ?? current))} disabled={!pageCount || page >= pageCount}>Next page</button>
      <button type="button" className="button button--secondary" onClick={() => setZoom((current) => Math.max(.5, Number((current - .1).toFixed(1))))} disabled={zoom <= .5}>Zoom out</button>
      <span aria-live="polite">{Math.round(zoom * 100)}%</span>
      <button type="button" className="button button--secondary" onClick={() => setZoom((current) => Math.min(2, Number((current + .1).toFixed(1))))} disabled={zoom >= 2}>Zoom in</button>
      <label className="pdf-review-panel__width">Panel width
        <input aria-label="PDF panel width" type="range" min="32" max="68" value={panelWidth} onChange={(event) => setPanelWidth(Number(event.target.value))} />
      </label>
    </div>
    {loadError ? <div className="pdf-review-panel__error" role="alert"><h3>PDF could not load</h3><p>The source document is currently unavailable. Try opening it in a new tab or return to the queue.</p></div> :
      <div className="pdf-review-panel__document" aria-busy={pageCount === null}>
        <Document file={documentUrl} options={{ cMapUrl: `${localPdfAssets}cmaps/`, cMapPacked: true, standardFontDataUrl: `${localPdfAssets}standard_fonts/` }} loading={<p>Loading source document…</p>} onLoadSuccess={({ numPages }) => { setPageCount(numPages); setPage((current) => clampPage(current, numPages)); }} onLoadError={() => setLoadError(true)}>
          <Page pageNumber={page} scale={zoom} devicePixelRatio={pixelDensity} renderTextLayer={false} renderAnnotationLayer={false} />
          {bufferedPage ? <div className="pdf-review-panel__buffer" aria-hidden="true"><Page pageNumber={bufferedPage} scale={zoom} devicePixelRatio={pixelDensity} renderTextLayer={false} renderAnnotationLayer={false} /></div> : null}
        </Document>
      </div>}
  </section>;
}
