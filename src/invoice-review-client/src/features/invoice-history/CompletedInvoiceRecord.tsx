import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import {
  AuditEventType,
  DocumentIntegrityStatus,
  InvoiceStatus,
  type AuditEventDto,
  type DateFieldDto,
  type FieldCorrectionDto,
  type InvoiceDetailDto,
  type MoneyFieldDto,
  type TextFieldDto,
} from '../../api/generated/client';
import { downloadInvoiceExport, invoiceApi, invoiceQueryKeys, isInvoiceApiError } from '../../api/invoiceApi';
import { Button } from '../../shared/components/Button';
import { PageLoading } from '../../shared/components/PageLoading';
import './CompletedInvoiceRecord.css';

type DisplayField = TextFieldDto | DateFieldDto | MoneyFieldDto;

export function isTerminalInvoice(status: InvoiceStatus): boolean {
  return status === InvoiceStatus.Approved || status === InvoiceStatus.Rejected;
}

function present(value: string | null): string { return value ?? 'Not provided'; }
function presentDate(value: string | null): string {
  if (!value) return 'Not provided';
  const [year, month, day] = value.split('-');
  return year && month && day ? `${day}.${month}.${year}` : value;
}
function presentTimestamp(value: string): string {
  const timestamp = new Date(value);
  return Number.isNaN(timestamp.valueOf()) ? value : timestamp.toLocaleString(undefined, { timeZoneName: 'short' });
}
function sourceLabel(source: DisplayField['currentSource']): string {
  return source === 'aiInference' ? 'AI inference' : source === 'nativeText' ? 'Native text' : source === 'ocr' ? 'OCR' : 'Reviewer';
}

function ReadOnlyField({ label, field, kind = 'text' }: { label: string; field: DisplayField; kind?: 'text' | 'date' | 'money' }) {
  const value = kind === 'date' ? presentDate(field.value) : present(field.value);
  const original = kind === 'date' ? presentDate(field.originalValue) : present(field.originalValue);
  return <div className="completed-record__field">
    <dt>{label}</dt><dd>{value}</dd>
    <dd className="field-metadata">Source: {sourceLabel(field.currentSource)} · {field.confidenceBand} confidence{field.confidence === null ? '' : ` (${Math.round(field.confidence * 100)}%)`}</dd>
    {field.differsFromOriginal ? <dd className="correction-disclosure">Original extracted value: {original}</dd> : null}
  </div>;
}

function FinalValues({ invoice }: { invoice: InvoiceDetailDto }) {
  if (!invoice.fields) return <section aria-labelledby="completed-values"><h2 id="completed-values">Final values</h2><p>Final fields are unavailable for this record.</p></section>;
  const { fields } = invoice;
  return <section aria-labelledby="completed-values"><h2 id="completed-values">Final values</h2>
    <dl className="completed-record__fields">
      <ReadOnlyField label="Supplier name" field={fields.supplier.name} /><ReadOnlyField label="Supplier registration ID" field={fields.supplier.registrationId} />
      <ReadOnlyField label="Invoice number" field={fields.reference.invoiceNumber} /><ReadOnlyField label="Purchase order number" field={fields.reference.purchaseOrderNumber} />
      <ReadOnlyField label="Invoice date" field={fields.datesAndTerms.invoiceDate} kind="date" /><ReadOnlyField label="Due date" field={fields.datesAndTerms.dueDate} kind="date" />
      <ReadOnlyField label="Payment terms" field={fields.datesAndTerms.paymentTerms} /><ReadOnlyField label="Currency" field={fields.amounts.currency} />
      <ReadOnlyField label="Subtotal" field={fields.amounts.subtotal} kind="money" /><ReadOnlyField label="Tax amount" field={fields.amounts.taxAmount} kind="money" /><ReadOnlyField label="Total" field={fields.amounts.total} kind="money" />
    </dl>
    <p><strong>Review notes:</strong> {present(invoice.reviewNotes)}</p>
  </section>;
}

function Corrections({ corrections }: { corrections: FieldCorrectionDto[] }) {
  return <section aria-labelledby="completed-corrections"><h2 id="completed-corrections">Manual corrections</h2>
    {corrections.length === 0 ? <p>No manual corrections were recorded.</p> : <ol>{corrections.map((correction) => <li key={correction.id}><strong>{correction.field}</strong>: {present(correction.previousValue)} → {present(correction.newValue)} (draft {correction.draftVersion}, {presentTimestamp(correction.occurredAt)})</li>)}</ol>}
  </section>;
}

function eventDescription(event: AuditEventDto): string {
  switch (event.type) {
    case AuditEventType.InvoiceUploaded: return `Document uploaded: ${event.details.document.originalFilename}.`;
    case AuditEventType.ExtractionCompleted: return `Extraction completed from ${event.details.documentTextSource}; ${event.details.extractedFieldCount} fields extracted.`;
    case AuditEventType.ExtractionFailed: return `Extraction failed: ${event.details.failure.message}`;
    case AuditEventType.DraftSaved: return event.details.isNoOp ? 'Draft save recorded with no field changes.' : `${event.details.changes.length} field correction(s) saved.`;
    case AuditEventType.ValidationCompleted: return `Validation (${event.details.trigger}): ${event.details.warningCount} warning(s), ${event.details.errorCount} error(s); status ${event.details.resultingStatus}.`;
    case AuditEventType.InvoiceApproved: return `Approved at ${presentTimestamp(event.details.decidedAt)}.`;
    case AuditEventType.InvoiceRejected: return `Rejected at ${presentTimestamp(event.details.decidedAt)}. Reason: ${event.details.rejectionReason}`;
    case AuditEventType.DocumentIntegrityChanged: return `Document integrity changed from ${event.details.previousStatus} to ${event.details.currentStatus}.`;
  }
}

function AuditHistory({ invoiceId }: { invoiceId: string }) {
  const [page, setPage] = useState(1);
  const query = useQuery({ queryKey: invoiceQueryKeys.history(invoiceId, { page }), queryFn: () => invoiceApi.getHistory(invoiceId, { page }) });
  return <section aria-labelledby="completed-history"><h2 id="completed-history">Audit history</h2>
    {query.isPending ? <PageLoading label="Loading audit history" /> : null}
    {query.isError ? <p role="alert">Audit history could not load. Please try again.</p> : null}
    {query.data ? <><ol aria-label="Audit events">{query.data.items.map((event) => <li key={event.id}><strong>{event.type}</strong> · {presentTimestamp(event.occurredAt)} · draft {event.draftVersion}<br />{eventDescription(event)}</li>)}</ol>
      <p>Page {query.data.page} of {query.data.totalPages} ({query.data.totalItems} events)</p>
      <div><Button tone="secondary" disabled={!query.data.hasPreviousPage} onClick={() => setPage((current) => current - 1)}>Previous history page</Button>{' '}<Button tone="secondary" disabled={!query.data.hasNextPage} onClick={() => setPage((current) => current + 1)}>Next history page</Button></div>
    </> : null}
  </section>;
}

function RecordActions({ invoice }: { invoice: InvoiceDetailDto }) {
  const [exportError, setExportError] = useState<string | null>(null);
  const [isExporting, setIsExporting] = useState(false);
  const documentAvailable = invoice.document.integrityStatus === DocumentIntegrityStatus.Available;
  async function exportRecord() {
    setExportError(null); setIsExporting(true);
    try {
      const attachment = await downloadInvoiceExport(invoice.id);
      const objectUrl = URL.createObjectURL(attachment.data);
      const link = document.createElement('a');
      link.href = objectUrl; link.download = attachment.fileName; link.click();
      URL.revokeObjectURL(objectUrl);
    } catch (error) {
      setExportError(isInvoiceApiError(error) ? error.detail : 'The JSON export could not be downloaded. Please try again.');
    } finally { setIsExporting(false); }
  }
  return <section aria-labelledby="completed-artifacts"><h2 id="completed-artifacts">Record artifacts</h2>
    <p>Document: {documentAvailable ? <a href={invoiceApi.documentUrl(invoice.id)} target="_blank" rel="noreferrer">Open source PDF: {invoice.document.originalFilename}</a> : <span role="status">Source PDF is unavailable ({invoice.document.integrityStatus}).</span>}</p>
    <Button onClick={exportRecord} disabled={isExporting}>{isExporting ? 'Preparing export…' : 'Download JSON export'}</Button>
    {exportError ? <p role="alert">{exportError}</p> : null}
  </section>;
}

export function CompletedInvoiceRecord({ invoice }: { invoice: InvoiceDetailDto }) {
  if (!isTerminalInvoice(invoice.status)) return null;
  const decision = invoice.decision;
  return <article className="completed-record" aria-label="Completed invoice record">
    <header><h1>{invoice.status === InvoiceStatus.Approved ? 'Approved invoice' : 'Rejected invoice'}</h1><p>Status: <strong>{invoice.status}</strong> · Draft version: {invoice.draftVersion}</p></header>
    <section aria-labelledby="completed-decision"><h2 id="completed-decision">Decision</h2>{decision ? <><p>{decision.kind} at {presentTimestamp(decision.decidedAt)}</p>{decision.rejectionReason ? <p><strong>Reason:</strong> {decision.rejectionReason}</p> : null}</> : <p>Decision details are unavailable.</p>}</section>
    <section aria-labelledby="completed-validation"><h2 id="completed-validation">Latest validation</h2>{invoice.currentValidation ? <><p>Validated at {presentTimestamp(invoice.currentValidation.validatedAt)} for draft {invoice.currentValidation.draftVersion}: {invoice.currentValidation.warningCount} warning(s), {invoice.currentValidation.errorCount} blocking error(s).</p><ul>{invoice.currentValidation.results.map((result) => <li key={`${result.code}-${result.fields.join('-')}`}><strong>{result.severity} — {result.code}</strong>: {result.message}</li>)}</ul></> : <p>No validation run is available.</p>}</section>
    <FinalValues invoice={invoice} /><Corrections corrections={invoice.corrections} /><RecordActions invoice={invoice} /><AuditHistory invoiceId={invoice.id} />
  </article>;
}
