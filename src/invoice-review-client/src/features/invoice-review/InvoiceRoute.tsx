import { useQuery } from '@tanstack/react-query';
import { useEffect, useMemo, type ReactNode } from 'react';
import { FormProvider, useForm, useFormContext, type FieldPath } from 'react-hook-form';
import { Link, useParams } from 'react-router-dom';
import { ValidationSeverity, type DateFieldDto, type InvoiceDetailDto, type InvoiceDraftInputDto, type MoneyFieldDto, type TextFieldDto, type ValidationResultDto } from '../../api/generated/client';
import { invoiceApi, invoiceDetailToDraft, invoiceQueryKeys } from '../../api/invoiceApi';
import { PageLoading } from '../../shared/components/PageLoading';
import { AppShell } from '../../shared/layout/AppShell';
import { CompletedInvoiceRecord, isTerminalInvoice } from '../invoice-history/CompletedInvoiceRecord';
import { DraftSaveWorkflow } from './DraftSaveWorkflow';
import { PdfReviewPanel } from './PdfReviewPanel';
import { RevalidationWorkflow, validationFieldPaths } from './RevalidationWorkflow';
import { DecisionWorkflow } from './DecisionWorkflow';

type ExtractedField = TextFieldDto | DateFieldDto | MoneyFieldDto;
type FieldDefinition = { label: string; name: FieldPath<InvoiceDraftInputDto>; field: ExtractedField; type?: 'date' | 'text' };

export function formatMoney(value: string | null): string { return value ?? 'Not provided'; }
export function formatDate(value: string | null): string {
  if (!value) return 'Not provided';
  const [year, month, day] = value.split('-');
  return year && month && day ? `${day}.${month}.${year}` : value;
}
export function confidenceLabel(field: ExtractedField): string {
  return `${field.confidenceBand} confidence (${field.confidence === null ? 'no score' : `${Math.round(field.confidence * 100)}%`})`;
}
function sourceLabel(source: ExtractedField['currentSource']): string {
  return source === 'aiInference' ? 'AI inference' : source === 'nativeText' ? 'Native text' : source === 'ocr' ? 'OCR' : 'Reviewer';
}
function displayOriginal(field: ExtractedField, isMoney: boolean, isDate: boolean): string {
  return isMoney ? formatMoney(field.originalValue) : isDate ? formatDate(field.originalValue) : field.originalValue ?? 'Not provided';
}

function FieldMetadata({ field, isDate = false, isMoney = false }: { field: ExtractedField; isDate?: boolean; isMoney?: boolean }) {
  return <div className="field-metadata">
    <span className={`confidence-badge confidence-badge--${field.confidenceBand}`}>{confidenceLabel(field)}</span>
    <span>Source: {sourceLabel(field.currentSource)}</span>
    {field.differsFromOriginal ? <span className="correction-disclosure">Corrected from: {displayOriginal(field, isMoney, isDate)}</span> : null}
  </div>;
}

function ReviewField({ definition, validationResults }: { definition: FieldDefinition; validationResults: ValidationResultDto[] }) {
  const { register } = useFormContext<InvoiceDraftInputDto>();
  const isDate = definition.type === 'date';
  const isMoney = definition.name.startsWith('amounts.') && definition.name !== 'amounts.currency';
  const results = validationResults.filter((result) => result.fields.some((field) => validationFieldPaths[field] === definition.name));
  const resultId = `${definition.name}-validation`;
  return <div className={`review-field${results.some((result) => result.severity === ValidationSeverity.Error) ? ' review-field--invalid' : ''}`}>
    <label htmlFor={definition.name}>{definition.label}</label>
    <input id={definition.name} type={definition.type ?? 'text'} className={isMoney ? 'numeric-value' : undefined} aria-invalid={results.some((result) => result.severity === ValidationSeverity.Error) || undefined} aria-describedby={results.length ? resultId : undefined} {...register(definition.name)} />
    <FieldMetadata field={definition.field} isDate={isDate} isMoney={isMoney} />
  </div>;
}
function FieldValidationMessages({ definition, validationResults }: { definition: FieldDefinition; validationResults: ValidationResultDto[] }) {
  const results = validationResults.filter((result) => result.fields.some((field) => validationFieldPaths[field] === definition.name));
  if (!results.length) return null;
  return <ul id={`${definition.name}-validation`} className="field-validation-results" aria-label={`${definition.label} validation`}>
    {results.map((result, index) => <li key={`${result.code}-${index}`} className={`validation-result validation-result--${result.severity}`}><strong>{result.severity === ValidationSeverity.Error ? 'Blocking error' : 'Warning'} ({result.code}):</strong> {result.message}</li>)}
  </ul>;
}
function ReviewFields({ definitions, validationResults }: { definitions: FieldDefinition[]; validationResults: ValidationResultDto[] }) {
  return <>{definitions.map((definition) => <ReviewField key={definition.name} definition={definition} validationResults={validationResults} />)}{definitions.map((definition) => <FieldValidationMessages key={`${definition.name}-validation`} definition={definition} validationResults={validationResults} />)}</>;
}
function ReviewGroup({ children, title }: { children: ReactNode; title: string }) {
  return <fieldset className="review-group"><legend>{title}</legend>{children}</fieldset>;
}

export function ReviewForm({ invoice }: { invoice: InvoiceDetailDto }) {
  const draft = useMemo(() => invoiceDetailToDraft(invoice), [invoice]);
  const form = useForm<InvoiceDraftInputDto>({ defaultValues: draft ?? undefined });
  useEffect(() => { if (draft && !form.formState.isDirty) form.reset(draft); }, [draft, form, form.formState.isDirty]);
  if (!invoice.fields || !draft) return null;
  const { fields } = invoice;
  const validationResults = invoice.currentValidation?.results ?? [];
  const supplier: FieldDefinition[] = [{ label: 'Supplier name', name: 'supplier.name', field: fields.supplier.name }, { label: 'Supplier registration ID', name: 'supplier.registrationId', field: fields.supplier.registrationId }];
  const reference: FieldDefinition[] = [{ label: 'Invoice number', name: 'reference.invoiceNumber', field: fields.reference.invoiceNumber }, { label: 'Purchase order number', name: 'reference.purchaseOrderNumber', field: fields.reference.purchaseOrderNumber }];
  const dates: FieldDefinition[] = [{ label: 'Invoice date', name: 'datesAndTerms.invoiceDate', field: fields.datesAndTerms.invoiceDate, type: 'date' }, { label: 'Due date', name: 'datesAndTerms.dueDate', field: fields.datesAndTerms.dueDate, type: 'date' }, { label: 'Payment terms', name: 'datesAndTerms.paymentTerms', field: fields.datesAndTerms.paymentTerms }];
  const amounts: FieldDefinition[] = [{ label: 'Currency', name: 'amounts.currency', field: fields.amounts.currency }, { label: 'Subtotal', name: 'amounts.subtotal', field: fields.amounts.subtotal }, { label: 'Tax amount', name: 'amounts.taxAmount', field: fields.amounts.taxAmount }, { label: 'Total', name: 'amounts.total', field: fields.amounts.total }];
  return <FormProvider {...form}><form className="review-form" aria-label="Invoice draft" onSubmit={(event) => event.preventDefault()}>
    <ReviewGroup title="Supplier"><ReviewFields definitions={supplier} validationResults={validationResults} /></ReviewGroup>
    <ReviewGroup title="Reference"><ReviewFields definitions={reference} validationResults={validationResults} /></ReviewGroup>
    <ReviewGroup title="Dates and terms"><ReviewFields definitions={dates} validationResults={validationResults} /><p className="system-metadata">Normalized payment-term days: {fields.datesAndTerms.normalizedPaymentTermsDays ?? 'Not available'} (derived by the system)</p></ReviewGroup>
    <ReviewGroup title="Amounts"><ReviewFields definitions={amounts} validationResults={validationResults} /></ReviewGroup>
    <ReviewGroup title="Review information"><div className="review-field"><label htmlFor="reviewNotes">Review notes</label><textarea id="reviewNotes" {...form.register('reviewNotes')} /></div></ReviewGroup>
    <DraftSaveWorkflow invoice={invoice} form={form} />
    <RevalidationWorkflow invoice={invoice} />
    <DecisionWorkflow invoice={invoice} />
  </form></FormProvider>;
}

function structuredData(data: unknown): string {
  if (data === null || data === undefined) return 'Not provided';
  if (typeof data !== 'object') return String(data);
  return JSON.stringify(data);
}

function ReviewSummary({ invoice }: { invoice: InvoiceDetailDto }) {
  return <aside className="review-summary" aria-label="Review summary"><h2>Review summary</h2><dl>
    <div><dt>Extracted fields</dt><dd>{invoice.summary.extractedFieldCount}</dd></div><div><dt>Warnings</dt><dd>{invoice.summary.warningCount}</dd></div><div><dt>Blocking errors</dt><dd>{invoice.summary.errorCount}</dd></div><div><dt>Manual corrections</dt><dd>{invoice.summary.manualCorrectionCount}</dd></div>
  </dl>{invoice.currentValidation?.results.map((result, index) => <div key={`${result.code}-${result.fields.join('-')}-${index}`} className={`validation-result validation-result--${result.severity}`}><strong>{result.severity === 'error' ? 'Blocking error:' : 'Warning:'}</strong> <span className="validation-code">{result.code}</span> {result.message}
    {result.fields.map((field) => <button key={field} type="button" className="validation-field-link" onClick={() => document.getElementById(validationFieldPaths[field])?.focus()}>Go to {field}</button>)}
    <details><summary>Rule data</summary><code>{structuredData(result.data)}</code></details>
  </div>)}</aside>;
}
function InvoiceSystemMetadata({ invoice }: { invoice: InvoiceDetailDto }) {
  return <p className="system-metadata">Status: {invoice.status} · Draft version: {invoice.draftVersion} · Document: {invoice.document.originalFilename} · Pages: {invoice.document.pageCount}</p>;
}

export function InvoiceRoute() {
  const { invoiceId } = useParams();
  const query = useQuery({ queryKey: invoiceQueryKeys.detail(invoiceId ?? ''), queryFn: () => invoiceApi.getInvoice(invoiceId!), enabled: Boolean(invoiceId) });
  return <AppShell title="Invoice review" description="Compare the source document with the extracted record before making a decision.">
    {!invoiceId ? <section className="route-card"><h2>Invoice not available</h2><Link className="text-link" to="/">Return to invoice queue</Link></section> : null}
    {invoiceId && query.isPending ? <PageLoading label="Loading invoice review" /> : null}
    {query.isError ? <section className="route-card" role="alert"><h2>Invoice review could not load</h2><p>Please return to the queue and try again.</p><Link className="text-link" to="/">Return to invoice queue</Link></section> : null}
    {query.data && isTerminalInvoice(query.data.status) ? <CompletedInvoiceRecord invoice={query.data} /> : null}
    {query.data && !isTerminalInvoice(query.data.status) ? <section className="review-workspace" aria-label="Invoice review workspace"><InvoiceSystemMetadata invoice={query.data} />
      {query.data.processingFailure ? <section className="processing-failure" role="alert"><h2>Processing failed</h2><p>{query.data.processingFailure.message}</p><p>Stage: {query.data.processingFailure.stage} · Code: {query.data.processingFailure.code}</p></section> : null}
      <div className="review-split-layout">
        <PdfReviewPanel invoiceId={query.data.id} documentName={query.data.document.originalFilename} documentIntegrityStatus={query.data.document.integrityStatus} />
        <div className="review-details-panel"><ReviewSummary invoice={query.data} />
          {query.data.fields ? <ReviewForm invoice={query.data} /> : <section className="route-card"><h2>Fields are unavailable</h2><p>This invoice has no extracted fields to review.</p></section>}
        </div>
      </div>
    </section> : null}
  </AppShell>;
}
