import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { useFormContext } from 'react-hook-form';
import { InvoiceStatus, ValidationSeverity, type InvoiceDetailDto, type InvoiceDraftInputDto, type InvoiceFieldKey, type ValidationResultDto } from '../../api/generated/client';
import { applyInvoiceMutation, invoiceApi, invoiceQueryKeys, isInvoiceApiError } from '../../api/invoiceApi';
import { Button } from '../../shared/components/Button';

export const validationFieldPaths: Record<InvoiceFieldKey, string> = {
  supplierName: 'supplier.name', supplierRegistrationId: 'supplier.registrationId', invoiceNumber: 'reference.invoiceNumber',
  purchaseOrderNumber: 'reference.purchaseOrderNumber', invoiceDate: 'datesAndTerms.invoiceDate', dueDate: 'datesAndTerms.dueDate',
  paymentTerms: 'datesAndTerms.paymentTerms', currency: 'amounts.currency', subtotal: 'amounts.subtotal', taxAmount: 'amounts.taxAmount',
  total: 'amounts.total', reviewNotes: 'reviewNotes',
};

export function canRevalidateInvoice(status: InvoiceStatus): boolean {
  return status === InvoiceStatus.ReviewRequired || status === InvoiceStatus.ReadyForApproval;
}

export function firstBlockingField(results: ValidationResultDto[]): string | null {
  const first = results.find((result) => result.severity === ValidationSeverity.Error && result.fields.length > 0);
  return first ? validationFieldPaths[first.fields[0]] : null;
}

function focusValidationField(path: string | null) {
  if (!path) return;
  window.setTimeout(() => document.getElementById(path)?.focus(), 0);
}

export function RevalidationWorkflow({ invoice }: { invoice: InvoiceDetailDto }) {
  const queryClient = useQueryClient();
  const form = useFormContext<InvoiceDraftInputDto>();
  const [error, setError] = useState<string | null>(null);
  const [conflictVersion, setConflictVersion] = useState<number | null>(null);
  const errorRef = useRef<HTMLDivElement>(null);
  const validate = useMutation({
    mutationFn: () => invoiceApi.validate(invoice.id, { expectedVersion: invoice.draftVersion }),
    onMutate: () => { setError(null); setConflictVersion(null); },
    onSuccess: async (validatedInvoice) => {
      await applyInvoiceMutation(queryClient, validatedInvoice);
      focusValidationField(firstBlockingField(validatedInvoice.currentValidation?.results ?? []));
    },
    onError: (mutationError) => {
      if (isInvoiceApiError(mutationError) && mutationError.status === 409) {
        setConflictVersion(mutationError.currentVersion);
        setError('Validation could not be completed because this invoice changed. Your unsaved edits have been kept; refetch before reconciling.');
        return;
      }
      setError(isInvoiceApiError(mutationError) ? mutationError.detail : 'Validation could not be completed. Please try again.');
    },
  });
  const refetchCurrent = async () => {
    await queryClient.fetchQuery({ queryKey: invoiceQueryKeys.detail(invoice.id), queryFn: () => invoiceApi.getInvoice(invoice.id) });
  };
  useEffect(() => { if (error) errorRef.current?.focus(); }, [error]);
  const disabled = validate.isPending || form.formState.isDirty || !canRevalidateInvoice(invoice.status);
  const disabledMessage = form.formState.isDirty ? 'Save your draft before revalidating.' : !canRevalidateInvoice(invoice.status) ? 'This invoice cannot be revalidated in its current status.' : null;

  return <section className="revalidation-workflow" aria-label="Revalidation controls">
    <Button type="button" disabled={disabled} onClick={() => validate.mutate()}>{validate.isPending ? 'Revalidating…' : 'Revalidate'}</Button>
    {disabledMessage ? <p role="status">{disabledMessage}</p> : null}
    {error ? <div ref={errorRef} role="alert" tabIndex={-1}><p>{error}</p>{conflictVersion !== null ? <><p>Current server version: {conflictVersion}</p><Button type="button" tone="secondary" onClick={() => void refetchCurrent()}>Refetch current record</Button></> : null}</div> : null}
  </section>;
}
