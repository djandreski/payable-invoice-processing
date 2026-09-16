import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import type { FieldPath, UseFormReturn } from 'react-hook-form';
import { useBlocker } from 'react-router-dom';
import * as Dialog from '@radix-ui/react-dialog';
import type { InvoiceDetailDto, InvoiceDraftInputDto } from '../../api/generated/client';
import { applyInvoiceMutation, invoiceApi, invoiceDetailToDraft, isInvoiceApiError } from '../../api/invoiceApi';
import { Button } from '../../shared/components/Button';

export type DraftSaveWorkflowProps = {
  invoice: InvoiceDetailDto;
  form: UseFormReturn<InvoiceDraftInputDto>;
};

/** The route owner can pass this predicate to its router's navigation blocker. */
export function shouldBlockDraftNavigation(isDirty: boolean, isSaving = false): boolean {
  return isDirty && !isSaving;
}

/**
 * Registers the browser-level unsaved-work warning. Route-transition blocking is
 * deliberately mounted by the route owner, because this application currently
 * uses BrowserRouter rather than a data router with a transition blocker.
 */
export function useDraftUnloadGuard(isDirty: boolean): void {
  useEffect(() => {
    if (!isDirty) return;
    const onBeforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', onBeforeUnload);
    return () => window.removeEventListener('beforeunload', onBeforeUnload);
  }, [isDirty]);
}

export function DraftNavigationGuard({ isDirty, isSaving }: { isDirty: boolean; isSaving: boolean }) {
  const blocker = useBlocker(shouldBlockDraftNavigation(isDirty, isSaving));
  const open = blocker.state === 'blocked';

  return <Dialog.Root open={open} onOpenChange={(nextOpen) => { if (!nextOpen && blocker.state === 'blocked') blocker.reset(); }}>
    <Dialog.Portal>
      <Dialog.Overlay className="dialog-overlay" />
      <Dialog.Content className="dialog-content" aria-describedby="dirty-navigation-description">
        <Dialog.Title>Discard unsaved changes?</Dialog.Title>
        <Dialog.Description id="dirty-navigation-description">Your draft edits have not been saved. Stay here to keep editing, or discard them and continue.</Dialog.Description>
        <div className="dialog-actions">
          <Button tone="secondary" onClick={() => blocker.state === 'blocked' && blocker.reset()}>Keep editing</Button>
          <Button tone="danger" onClick={() => blocker.state === 'blocked' && blocker.proceed()}>Discard and continue</Button>
        </div>
      </Dialog.Content>
    </Dialog.Portal>
  </Dialog.Root>;
}

function applyServerFieldErrors(form: UseFormReturn<InvoiceDraftInputDto>, error: unknown): void {
  if (!isInvoiceApiError(error) || !error.fields) return;
  for (const [path, messages] of Object.entries(error.fields)) {
    if (!messages.length) continue;
    const formPath = path.startsWith('draft.') ? path.slice('draft.'.length) : path;
    form.setError(formPath as FieldPath<InvoiceDraftInputDto>, { type: 'server', message: messages.join(' ') });
  }
}

function collectErrorMessages(value: unknown, seen = new Set<object>()): string[] {
  if (!value || typeof value !== 'object' || value instanceof HTMLElement || seen.has(value)) return [];
  seen.add(value);
  const candidate = value as { message?: unknown };
  const own = typeof candidate.message === 'string' ? [candidate.message] : [];
  return [...own, ...Object.entries(value).flatMap(([key, child]) => key === 'message' || key === 'ref' ? [] : collectErrorMessages(child, seen))];
}

export function DraftSaveWorkflow({ invoice, form }: DraftSaveWorkflowProps) {
  const queryClient = useQueryClient();
  const [conflictVersion, setConflictVersion] = useState<number | null>(null);
  const [saveError, setSaveError] = useState<string | null>(null);
  const errorRef = useRef<HTMLDivElement>(null);
  const fieldErrors = collectErrorMessages(form.formState.errors);
  useDraftUnloadGuard(form.formState.isDirty);

  const save = useMutation({
    mutationFn: (draft: InvoiceDraftInputDto) => invoiceApi.saveDraft(invoice.id, { expectedVersion: invoice.draftVersion, draft }),
    onMutate: () => {
      setConflictVersion(null);
      setSaveError(null);
      form.clearErrors();
    },
    onSuccess: async (savedInvoice) => {
      await applyInvoiceMutation(queryClient, savedInvoice);
      const savedDraft = invoiceDetailToDraft(savedInvoice);
      if (savedDraft) form.reset(savedDraft);
    },
    onError: (error) => {
      applyServerFieldErrors(form, error);
      if (isInvoiceApiError(error) && error.status === 409) {
        setConflictVersion(error.currentVersion);
        setSaveError('This invoice changed elsewhere. Your edits have been kept. Refetch the current record before reconciling them manually.');
        return;
      }
      setSaveError(isInvoiceApiError(error) ? error.detail : 'The draft could not be saved. Please try again.');
    },
  });

  const refetchCurrent = async () => {
    await queryClient.fetchQuery({ queryKey: ['invoices', 'detail', invoice.id], queryFn: () => invoiceApi.getInvoice(invoice.id) });
  };

  useEffect(() => { if (saveError) errorRef.current?.focus(); }, [saveError]);

  return <section className="draft-save-workflow" aria-label="Draft save controls">
    <DraftNavigationGuard isDirty={form.formState.isDirty} isSaving={save.isPending} />
    <Button type="submit" disabled={save.isPending} onClick={form.handleSubmit((draft) => save.mutate(draft))}>
      {save.isPending ? 'Saving draft…' : 'Save draft'}
    </Button>
    {form.formState.isDirty ? <p role="status">You have unsaved changes.</p> : <p role="status">All changes are saved.</p>}
    {saveError ? <div ref={errorRef} role="alert" tabIndex={-1}><p>{saveError}</p>
      {conflictVersion !== null ? <><p>Current server version: {conflictVersion}</p><Button type="button" tone="secondary" onClick={() => void refetchCurrent()}>Refetch current record</Button></> : null}
    </div> : null}
    {fieldErrors.length ? <ul aria-label="Draft field errors">{fieldErrors.map((message, index) => <li key={`${message}-${index}`}>{message}</li>)}</ul> : null}
  </section>;
}
