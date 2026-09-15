import type { QueryClient } from '@tanstack/react-query';
import {
  Client,
  type ApproveInvoiceRequest,
  type AuditHistoryPageDto,
  type FileParameter,
  type FileResponse,
  type InvoiceDetailDto,
  type InvoiceDraftInputDto,
  type InvoiceExportV1Dto,
  type InvoiceProblemDetails,
  type InvoiceQueuePageDto,
  type InvoiceSort,
  type InvoiceStatus,
  type RejectInvoiceRequest,
  type SaveInvoiceDraftRequest,
  type ValidateInvoiceRequest,
} from './generated/client';

const correlationHeader = 'X-Correlation-ID';

export type InvoiceQueueFilters = {
  search?: string;
  statuses?: InvoiceStatus[];
  page?: number;
  pageSize?: number;
  sort?: InvoiceSort;
};

export type InvoiceHistoryPage = { page?: number; pageSize?: number };

export type InvoiceFeatureError = {
  status: number;
  code: string | null;
  title: string;
  detail: string;
  fields: Record<string, string[]> | null;
  currentVersion: number | null;
  correlationId: string | null;
};

export type InvoiceExportAttachment = {
  data: Blob;
  fileName: string;
};

export class InvoiceApiError extends Error implements InvoiceFeatureError {
  readonly status: number;
  readonly code: string | null;
  readonly title: string;
  readonly detail: string;
  readonly fields: Record<string, string[]> | null;
  readonly currentVersion: number | null;
  readonly correlationId: string | null;

  constructor(problem: InvoiceFeatureError) {
    super(problem.title);
    this.name = 'InvoiceApiError';
    this.title = problem.title;
    this.status = problem.status;
    this.code = problem.code;
    this.detail = problem.detail;
    this.fields = problem.fields;
    this.currentVersion = problem.currentVersion;
    this.correlationId = problem.correlationId;
  }
}

export function isInvoiceApiError(error: unknown): error is InvoiceApiError {
  return error instanceof InvoiceApiError;
}

function isProblemDetails(error: unknown): error is InvoiceProblemDetails {
  return typeof error === 'object' && error !== null &&
    typeof (error as Partial<InvoiceProblemDetails>).status === 'number' &&
    typeof (error as Partial<InvoiceProblemDetails>).title === 'string' &&
    typeof (error as Partial<InvoiceProblemDetails>).detail === 'string';
}

function newCorrelationId(): string {
  return globalThis.crypto?.randomUUID?.() ?? `client-${Date.now()}-${Math.random().toString(36).slice(2)}`;
}

export function createCorrelationFetch(fetchImplementation: typeof fetch = fetch): { fetch: typeof fetch } {
  return {
    async fetch(input, init) {
      const headers = new Headers(init?.headers);
      if (!headers.has(correlationHeader)) {
        headers.set(correlationHeader, newCorrelationId());
      }
      return fetchImplementation(input, { ...init, headers });
    },
  };
}

function apiBaseUrl(): string {
  const configured = import.meta.env.VITE_API_BASE_URL?.trim();
  return configured ? configured.replace(/\/$/, '') : '';
}

export function createInvoiceClient(baseUrl = apiBaseUrl(), fetchImplementation?: typeof fetch): Client {
  return new Client(baseUrl, createCorrelationFetch(fetchImplementation));
}

function translateError(error: unknown): never {
  if (isProblemDetails(error)) {
    throw new InvoiceApiError({
      status: error.status,
      code: error.code ?? null,
      title: error.title,
      detail: error.detail,
      fields: error.fields ?? null,
      currentVersion: error.currentVersion ?? null,
      correlationId: error.correlationId ?? null,
    });
  }

  throw error;
}

async function call<T>(operation: () => Promise<T>): Promise<T> {
  try {
    return await operation();
  } catch (error) {
    return translateError(error);
  }
}

export const invoiceQueryKeys = {
  all: ['invoices'] as const,
  queues: () => [...invoiceQueryKeys.all, 'queue'] as const,
  queue: (filters: InvoiceQueueFilters) => [...invoiceQueryKeys.queues(), filters] as const,
  details: () => [...invoiceQueryKeys.all, 'detail'] as const,
  detail: (invoiceId: string) => [...invoiceQueryKeys.details(), invoiceId] as const,
  histories: () => [...invoiceQueryKeys.all, 'history'] as const,
  history: (invoiceId: string, page: InvoiceHistoryPage = {}) => [...invoiceQueryKeys.histories(), invoiceId, page] as const,
  exports: () => [...invoiceQueryKeys.all, 'export'] as const,
  export: (invoiceId: string) => [...invoiceQueryKeys.exports(), invoiceId] as const,
};

export function invoiceDetailToDraft(invoice: InvoiceDetailDto): InvoiceDraftInputDto | null {
  const fields = invoice.fields;
  if (!fields) return null;

  return {
    supplier: { name: fields.supplier.name.value, registrationId: fields.supplier.registrationId.value },
    reference: { invoiceNumber: fields.reference.invoiceNumber.value, purchaseOrderNumber: fields.reference.purchaseOrderNumber.value },
    datesAndTerms: {
      invoiceDate: fields.datesAndTerms.invoiceDate.value,
      dueDate: fields.datesAndTerms.dueDate.value,
      paymentTerms: fields.datesAndTerms.paymentTerms.value,
    },
    amounts: {
      currency: fields.amounts.currency.value,
      subtotal: fields.amounts.subtotal.value,
      taxAmount: fields.amounts.taxAmount.value,
      total: fields.amounts.total.value,
    },
    reviewNotes: invoice.reviewNotes,
  };
}

export class InvoiceApi {
  constructor(private readonly client = createInvoiceClient()) {}

  listInvoices(filters: InvoiceQueueFilters = {}): Promise<InvoiceQueuePageDto> {
    return call(() => this.client.listInvoices(filters.search, filters.statuses, filters.page, filters.pageSize, filters.sort));
  }

  getInvoice(invoiceId: string): Promise<InvoiceDetailDto> {
    return call(() => this.client.getInvoice(invoiceId));
  }

  getDocument(invoiceId: string): Promise<FileResponse> {
    return call(() => this.client.getInvoiceDocument(invoiceId));
  }

  documentUrl(invoiceId: string): string {
    return `${apiBaseUrl()}/api/invoices/${encodeURIComponent(invoiceId)}/document`;
  }

  upload(file: File): Promise<InvoiceDetailDto> {
    const parameter: FileParameter = { data: file, fileName: file.name };
    return call(() => this.client.uploadInvoice(parameter));
  }

  saveDraft(invoiceId: string, request: SaveInvoiceDraftRequest): Promise<InvoiceDetailDto> {
    return call(() => this.client.saveInvoiceDraft(invoiceId, request));
  }

  validate(invoiceId: string, request: ValidateInvoiceRequest): Promise<InvoiceDetailDto> {
    return call(() => this.client.validateInvoice(invoiceId, request));
  }

  approve(invoiceId: string, request: ApproveInvoiceRequest): Promise<InvoiceDetailDto> {
    return call(() => this.client.approveInvoice(invoiceId, request));
  }

  reject(invoiceId: string, request: RejectInvoiceRequest): Promise<InvoiceDetailDto> {
    return call(() => this.client.rejectInvoice(invoiceId, request));
  }

  getHistory(invoiceId: string, page: InvoiceHistoryPage = {}): Promise<AuditHistoryPageDto> {
    return call(() => this.client.getInvoiceHistory(invoiceId, page.page, page.pageSize));
  }

  export(invoiceId: string): Promise<InvoiceExportV1Dto> {
    return call(() => this.client.exportInvoice(invoiceId));
  }
}

export const invoiceApi = new InvoiceApi();

function attachmentFileName(contentDisposition: string | null, fallback: string): string {
  const encodedName = contentDisposition?.match(/filename\*=UTF-8''([^;]+)/i)?.[1];
  if (encodedName) return decodeURIComponent(encodedName);
  return contentDisposition?.match(/filename="?([^";]+)"?/i)?.[1] ?? fallback;
}

/**
 * The generated endpoint exposes the export JSON body but not its attachment metadata.
 * Keep that browser concern at the adapter boundary, alongside the generated transport.
 */
export async function downloadInvoiceExport(
  invoiceId: string,
  fetchImplementation: typeof fetch = fetch,
  baseUrl = apiBaseUrl(),
): Promise<InvoiceExportAttachment> {
  const response = await createCorrelationFetch(fetchImplementation).fetch(
    `${baseUrl}/api/invoices/${encodeURIComponent(invoiceId)}/export`,
    { method: 'GET', headers: { Accept: 'application/json' } },
  );

  if (response.ok) {
    return {
      data: await response.blob(),
      fileName: attachmentFileName(response.headers.get('content-disposition'), `invoice-${invoiceId}.json`),
    };
  }

  let problem: unknown = null;
  try { problem = await response.json(); } catch { /* Non-JSON failures have no feature fields. */ }
  if (isProblemDetails(problem)) translateError(problem);
  throw new InvoiceApiError({
    status: response.status,
    code: null,
    title: response.statusText || 'Export failed',
    detail: 'The invoice export could not be retrieved.',
    fields: null,
    currentVersion: null,
    correlationId: response.headers.get(correlationHeader),
  });
}

export async function applyInvoiceMutation(queryClient: QueryClient, invoice: InvoiceDetailDto): Promise<void> {
  queryClient.setQueryData(invoiceQueryKeys.detail(invoice.id), invoice);
  await Promise.all([
    queryClient.invalidateQueries({ queryKey: invoiceQueryKeys.queues() }),
    queryClient.invalidateQueries({ queryKey: invoiceQueryKeys.histories() }),
    queryClient.invalidateQueries({ queryKey: invoiceQueryKeys.exports() }),
  ]);
}

export async function applyUploadMutation(queryClient: QueryClient, invoice: InvoiceDetailDto): Promise<void> {
  queryClient.setQueryData(invoiceQueryKeys.detail(invoice.id), invoice);
  await queryClient.invalidateQueries({ queryKey: invoiceQueryKeys.queues() });
}
