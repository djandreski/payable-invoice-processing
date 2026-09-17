import { useQuery } from '@tanstack/react-query';
import { useEffect, useId, useState, type FormEvent } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { invoiceApi, invoiceQueryKeys, type InvoiceQueueFilters } from '../../api/invoiceApi';
import { InvoiceSort, InvoiceStatus, type InvoiceQueueItemDto, type InvoiceQueueSummaryDto } from '../../api/generated/client';
import { UploadInvoiceDialog } from '../invoice-upload/UploadInvoiceDialog';
import { Button } from '../../shared/components/Button';
import { PageLoading } from '../../shared/components/PageLoading';
import { AppShell } from '../../shared/layout/AppShell';

const statuses = [
  [InvoiceStatus.Processing, 'Processing'], [InvoiceStatus.ReviewRequired, 'Review required'],
  [InvoiceStatus.ReadyForApproval, 'Ready for approval'], [InvoiceStatus.Approved, 'Approved'],
  [InvoiceStatus.Rejected, 'Rejected'], [InvoiceStatus.ProcessingFailed, 'Processing failed'],
] as const;
const statusLabels = new Map(statuses);

function filtersFrom(params: URLSearchParams): InvoiceQueueFilters {
  const statusesFromUrl = params.getAll('status').filter((status): status is InvoiceStatus => statuses.some(([value]) => value === status));
  const sort = Object.values(InvoiceSort).includes(params.get('sort') as InvoiceSort) ? params.get('sort') as InvoiceSort : InvoiceSort.UpdatedAtDesc;
  const pageValue = Number(params.get('page'));
  return { ...(params.get('search')?.trim() ? { search: params.get('search')!.trim() } : {}), ...(statusesFromUrl.length ? { statuses: statusesFromUrl } : {}), page: Number.isInteger(pageValue) && pageValue > 0 ? pageValue : 1, pageSize: 25, sort };
}

function formatDate(value: string | null): string {
  if (!value) return 'Not available';
  const date = new Date(`${value}T00:00:00Z`);
  return Number.isNaN(date.valueOf()) ? value : new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeZone: 'UTC' }).format(date);
}

function formatUpdated(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.valueOf()) ? 'Not available' : new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(date);
}

function formatMoney(total: string | null, currency: string | null): string {
  if (total === null) return 'Not available';
  const amount = Number(total);
  if (!Number.isFinite(amount)) return currency ? `${currency} ${total}` : total;
  try { return currency ? new Intl.NumberFormat(undefined, { style: 'currency', currency, minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(amount) : total; } catch { return `${currency ?? ''} ${total}`.trim(); }
}

function SummaryCards({ summary }: { summary: InvoiceQueueSummaryDto }) {
  const cards = [['Pending review', summary.pendingReviewCount], ['Warnings', summary.warningInvoiceCount], ['Errors', summary.errorInvoiceCount], ['Approved', summary.approvedCount]] as const;
  return <section aria-label="Queue summary" className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">{cards.map(([label, value]) => <article key={label} className="rounded-lg border border-[var(--slate-300)] bg-[var(--warm-white)] p-4 shadow-[var(--shadow-soft)]"><p className="m-0 text-sm font-semibold text-[var(--ink-700)]">{label}</p><p className="numeric-value my-1 text-3xl font-bold text-[var(--ink-950)]">{value}</p></article>)}</section>;
}

function QueueRow({ item }: { item: InvoiceQueueItemDto }) {
  const status = statusLabels.get(item.status) ?? item.status;
  return <tr className="border-t border-[var(--slate-300)] align-top"><td className="p-3"><Link className="text-link" to={`/invoices/${encodeURIComponent(item.id)}`}>{item.supplierName ?? 'Supplier unavailable'}</Link></td><td className="p-3">{item.invoiceNumber ?? 'Not available'}</td><td className="p-3 whitespace-nowrap">{formatDate(item.invoiceDate)}</td><td className="numeric-value p-3 whitespace-nowrap">{formatMoney(item.total, item.currency)}</td><td className="p-3"><span className="inline-flex rounded-full border border-[var(--slate-300)] px-2 py-1 text-sm font-semibold" aria-label={`Status: ${status}`}>{status}</span></td><td className="p-3"><span className="numeric-value">{item.exceptionCount} exception{item.exceptionCount === 1 ? '' : 's'}</span>{item.processingFailure ? <p className="mt-1 mb-0 text-sm font-semibold text-[var(--red-700)]">Processing failed: {item.processingFailure.code}</p> : null}</td><td className="p-3 whitespace-nowrap text-[var(--slate-600)]">{formatUpdated(item.updatedAt)}</td></tr>;
}

export function QueueRoute() {
  const [params, setParams] = useSearchParams();
  const filters = filtersFrom(params);
  const [search, setSearch] = useState(params.get('search') ?? '');
  const searchId = useId(); const statusesId = useId();
  useEffect(() => setSearch(params.get('search') ?? ''), [params]);
  const query = useQuery({ queryKey: invoiceQueryKeys.queue(filters), queryFn: () => invoiceApi.listInvoices(filters) });
  const update = (change: (next: URLSearchParams) => void) => { const next = new URLSearchParams(params); change(next); setParams(next); };
  const clear = () => { setSearch(''); setParams({}); };
  const submit = (event: FormEvent<HTMLFormElement>) => { event.preventDefault(); update((next) => { const trimmed = search.trim(); trimmed ? next.set('search', trimmed) : next.delete('search'); next.set('page', '1'); }); };
  const toggleStatus = (status: InvoiceStatus) => update((next) => {
    const selectedStatuses = next.getAll('status').filter((value): value is InvoiceStatus => statuses.some(([knownStatus]) => knownStatus === value));
    const nextStatuses = selectedStatuses.includes(status) ? selectedStatuses.filter((value) => value !== status) : [...selectedStatuses, status];
    next.delete('status');
    nextStatuses.forEach((value) => next.append('status', value));
    next.set('page', '1');
  });

  return <AppShell title="Invoice queue" description="Review incoming invoices, resolve exceptions, and keep decisions traceable." actions={<UploadInvoiceDialog />}>
    {query.isPending ? <PageLoading label="Loading invoice queue" /> : null}
    {query.isError ? <section className="route-card" role="alert" aria-labelledby="queue-error-title"><p className="eyebrow">Queue unavailable</p><h2 id="queue-error-title">We couldn’t load the invoice queue.</h2><p>Try again. Saved records have not been changed.</p><Button onClick={() => void query.refetch()}>Try again</Button></section> : null}
    {query.data && !query.isError ? <><SummaryCards summary={query.data.summary} />
      <section className="mt-6 rounded-lg border border-[var(--slate-300)] bg-[var(--warm-white)] p-4 shadow-[var(--shadow-soft)]" aria-label="Queue filters"><form className="flex flex-wrap items-start gap-4" onSubmit={submit}><div className="w-56 shrink-0"><label htmlFor={searchId} className="mb-1 block whitespace-nowrap text-sm font-semibold">Search supplier or invoice number</label><input id={searchId} value={search} onChange={(event) => setSearch(event.target.value)} className="min-h-11 w-full rounded border border-[var(--slate-300)] bg-white px-3" /></div><fieldset className="min-w-0 flex-1"><legend className="mb-2 text-sm font-semibold">Status filters</legend><div className="flex flex-wrap items-center gap-x-4 gap-y-2">{statuses.map(([value, label]) => <label key={value} className="inline-flex min-h-6 cursor-pointer items-center gap-2 text-sm font-semibold text-[var(--ink-900)]"><input id={`${statusesId}-${value}`} type="checkbox" value={value} checked={filters.statuses?.includes(value) ?? false} onChange={() => toggleStatus(value)} className="size-4 accent-[var(--blue-700)]" />{label}</label>)}</div></fieldset><div className="mt-6 flex w-25 shrink-0 gap-3"><Button type="submit" aria-label="Search" title="Search" className="grid size-11 place-items-center !p-0"><svg aria-hidden="true" viewBox="0 0 24 24" className="size-5 fill-none stroke-current stroke-2"><circle cx="11" cy="11" r="6" /><path d="m16 16 4 4" /></svg></Button><Button tone="secondary" onClick={clear} aria-label="Clear filters" title="Clear filters" className="grid size-11 place-items-center !p-0"><svg aria-hidden="true" viewBox="0 0 24 24" className="size-5 fill-none stroke-current stroke-2"><path d="M6 6l12 12M18 6 6 18" /></svg></Button></div></form></section>
      {query.data.totalItems === 0 ? <section className="route-card mt-6" aria-labelledby="queue-empty-title"><p className="eyebrow">{query.data.summary.totalInvoiceCount === 0 ? 'No invoices yet' : 'No matching invoices'}</p><h2 id="queue-empty-title">{query.data.summary.totalInvoiceCount === 0 ? 'Upload an invoice to begin review.' : 'Try changing or clearing the current filters.'}</h2>{query.data.summary.totalInvoiceCount > 0 ? <Button tone="secondary" className="mt-4" onClick={clear}>Clear filters</Button> : null}</section> : <><div className="mt-6 overflow-x-auto rounded-lg border border-[var(--slate-300)] bg-[var(--warm-white)] shadow-[var(--shadow-soft)]"><table className="w-full min-w-230 border-collapse text-left text-sm"><caption className="p-3 text-left text-[var(--slate-600)]">{query.data.totalItems} invoice{query.data.totalItems === 1 ? '' : 's'} matching the current queue filters.</caption><thead className="bg-[var(--slate-50)] text-[var(--ink-900)]"><tr><th scope="col" className="p-3">Supplier</th><th scope="col" className="p-3">Reference</th><th scope="col" className="p-3">Invoice date</th><th scope="col" className="p-3">Total</th><th scope="col" className="p-3">Status</th><th scope="col" className="p-3">Exceptions</th><th scope="col" className="p-3">Last updated</th></tr></thead><tbody>{query.data.items.map((item) => <QueueRow key={item.id} item={item} />)}</tbody></table></div><nav className="mt-4 flex items-center justify-between gap-3" aria-label="Queue pagination"><p className="m-0 text-sm text-[var(--slate-600)]">Page <span className="numeric-value">{query.data.page}</span> of <span className="numeric-value">{query.data.totalPages}</span></p><div className="flex gap-2"><Button tone="secondary" disabled={!query.data.hasPreviousPage} onClick={() => update((next) => next.set('page', String(query.data.page - 1)))}>Previous page</Button><Button tone="secondary" disabled={!query.data.hasNextPage} onClick={() => update((next) => next.set('page', String(query.data.page + 1)))}>Next page</Button></div></nav></>}
    </> : null}
  </AppShell>;
}
