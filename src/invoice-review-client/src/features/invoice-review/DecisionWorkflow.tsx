import { useMutation, useQueryClient } from '@tanstack/react-query';
import * as Dialog from '@radix-ui/react-dialog';
import { useEffect, useId, useRef, useState } from 'react';
import { useFormContext } from 'react-hook-form';
import { InvoiceStatus, type InvoiceDetailDto, type InvoiceDraftInputDto } from '../../api/generated/client';
import { applyInvoiceMutation, invoiceApi, invoiceQueryKeys, isInvoiceApiError } from '../../api/invoiceApi';
import { Button } from '../../shared/components/Button';

type DecisionAction = 'approve' | 'reject';

export function canApproveInvoice(status: InvoiceStatus): boolean {
  return status === InvoiceStatus.ReadyForApproval;
}

export function canRejectInvoice(status: InvoiceStatus): boolean {
  return status === InvoiceStatus.ReviewRequired || status === InvoiceStatus.ReadyForApproval;
}

function decisionErrorMessage(action: DecisionAction, error: unknown): string {
  if (!isInvoiceApiError(error)) return `The invoice could not be ${action === 'approve' ? 'approved' : 'rejected'}. Please try again.`;
  if (error.code === 'APPROVAL_BLOCKED') return 'Approval is blocked because the latest server validation found blocking errors. Refetch the record to review them.';
  if (error.status === 409) return 'This invoice changed or is no longer eligible for that decision. Your current review context has been kept; refetch before deciding again.';
  return error.detail;
}

function DecisionProblem({ action, error, onRefetch }: { action: DecisionAction; error: unknown; onRefetch: () => void }) {
  const alertRef = useRef<HTMLDivElement>(null);
  const invoiceError = isInvoiceApiError(error) ? error : null;
  useEffect(() => { alertRef.current?.focus(); }, [error]);
  return <div ref={alertRef} className="decision-problem" role="alert" tabIndex={-1}>
    <p>{decisionErrorMessage(action, error)}</p>
    {invoiceError?.currentVersion !== null && invoiceError?.currentVersion !== undefined ? <p>Current server version: {invoiceError.currentVersion}</p> : null}
    {invoiceError?.fields ? <ul aria-label="Decision field errors">{Object.entries(invoiceError.fields).flatMap(([field, messages]) => messages.map((message) => <li key={`${field}-${message}`}>{message}</li>))}</ul> : null}
    {invoiceError?.status === 409 ? <Button type="button" tone="secondary" onClick={onRefetch}>Refetch current record</Button> : null}
  </div>;
}

export function DecisionWorkflow({ invoice }: { invoice: InvoiceDetailDto }) {
  const queryClient = useQueryClient();
  const form = useFormContext<InvoiceDraftInputDto>();
  const reasonId = useId();
  const submittingRef = useRef(false);
  const [approveOpen, setApproveOpen] = useState(false);
  const [rejectOpen, setRejectOpen] = useState(false);
  const [reason, setReason] = useState('');
  const [reasonError, setReasonError] = useState<string | null>(null);
  const [decisionError, setDecisionError] = useState<unknown>(null);

  const onSuccess = async (decidedInvoice: InvoiceDetailDto) => {
    await applyInvoiceMutation(queryClient, decidedInvoice);
    setApproveOpen(false);
    setRejectOpen(false);
  };
  const approve = useMutation({
    mutationFn: () => invoiceApi.approve(invoice.id, { expectedVersion: invoice.draftVersion }),
    onMutate: () => setDecisionError(null),
    onSuccess,
    onError: setDecisionError,
    onSettled: () => { submittingRef.current = false; },
  });
  const reject = useMutation({
    mutationFn: (rejectionReason: string) => invoiceApi.reject(invoice.id, { expectedVersion: invoice.draftVersion, reason: rejectionReason }),
    onMutate: () => setDecisionError(null),
    onSuccess,
    onError: setDecisionError,
    onSettled: () => { submittingRef.current = false; },
  });
  const busy = approve.isPending || reject.isPending;
  const dirty = form.formState.isDirty;
  const refetchCurrent = async () => {
    await queryClient.fetchQuery({ queryKey: invoiceQueryKeys.detail(invoice.id), queryFn: () => invoiceApi.getInvoice(invoice.id) });
  };
  const submitRejection = () => {
    const trimmedReason = reason.trim();
    if (!trimmedReason) {
      setReasonError('Enter a reason for rejecting this invoice.');
      return;
    }
    setReasonError(null);
    if (submittingRef.current) return;
    submittingRef.current = true;
    reject.mutate(trimmedReason);
  };
  const submitApproval = () => {
    if (submittingRef.current) return;
    submittingRef.current = true;
    approve.mutate();
  };
  const decisionDisabled = busy || dirty;
  const dirtyMessage = dirty ? 'Save your draft before making a decision.' : null;

  return <section className="decision-workflow" aria-label="Approval and rejection controls">
    <div className="decision-workflow__actions">
      <Dialog.Root open={approveOpen} onOpenChange={setApproveOpen}>
        <Dialog.Trigger asChild>
          <Button disabled={decisionDisabled || !canApproveInvoice(invoice.status)}>Approve</Button>
        </Dialog.Trigger>
        <Dialog.Portal><Dialog.Overlay className="dialog-overlay" />
          <Dialog.Content className="dialog-content" aria-describedby="approve-description">
            <Dialog.Title>Approve invoice?</Dialog.Title>
            <Dialog.Description id="approve-description">This is a final decision. The server will validate the current saved draft again before approving it.</Dialog.Description>
            {decisionError ? <DecisionProblem action="approve" error={decisionError} onRefetch={() => void refetchCurrent()} /> : null}
            <div className="dialog-actions"><Dialog.Close asChild><Button tone="secondary" disabled={busy}>Cancel</Button></Dialog.Close><Button disabled={busy} aria-busy={approve.isPending} onClick={submitApproval}>{approve.isPending ? 'Approving…' : 'Confirm approval'}</Button></div>
          </Dialog.Content>
        </Dialog.Portal>
      </Dialog.Root>
      <Dialog.Root open={rejectOpen} onOpenChange={setRejectOpen}>
        <Dialog.Trigger asChild><Button tone="danger" disabled={decisionDisabled || !canRejectInvoice(invoice.status)}>Reject</Button></Dialog.Trigger>
        <Dialog.Portal><Dialog.Overlay className="dialog-overlay" />
          <Dialog.Content className="dialog-content" aria-describedby="reject-description">
            <Dialog.Title>Reject invoice?</Dialog.Title>
            <Dialog.Description id="reject-description">This is a final decision. Provide the reason that will be retained with the record.</Dialog.Description>
            <div className="decision-reason"><label htmlFor={reasonId}>Rejection reason</label><textarea id={reasonId} value={reason} onChange={(event) => { setReason(event.target.value); setReasonError(null); }} aria-describedby={reasonError ? `${reasonId}-error` : undefined} disabled={busy} />{reasonError ? <p id={`${reasonId}-error`} role="alert">{reasonError}</p> : null}</div>
            {decisionError ? <DecisionProblem action="reject" error={decisionError} onRefetch={() => void refetchCurrent()} /> : null}
            <div className="dialog-actions"><Dialog.Close asChild><Button tone="secondary" disabled={busy}>Cancel</Button></Dialog.Close><Button tone="danger" disabled={busy} aria-busy={reject.isPending} onClick={submitRejection}>{reject.isPending ? 'Rejecting…' : 'Confirm rejection'}</Button></div>
          </Dialog.Content>
        </Dialog.Portal>
      </Dialog.Root>
    </div>
    {dirtyMessage ? <p role="status">{dirtyMessage}</p> : null}
    {!dirty && !canApproveInvoice(invoice.status) ? <p role="status">Approval is available only when the server marks this invoice ready for approval.</p> : null}
    {!dirty && !canRejectInvoice(invoice.status) ? <p role="status">Rejection is unavailable in the current server status.</p> : null}
  </section>;
}
