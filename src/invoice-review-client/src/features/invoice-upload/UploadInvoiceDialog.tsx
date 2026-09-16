import * as Dialog from '@radix-ui/react-dialog';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useEffect, useId, useRef, useState, type ChangeEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  applyUploadMutation,
  invoiceApi,
  isInvoiceApiError,
  type InvoiceApi,
} from '../../api/invoiceApi';
import type { InvoiceDetailDto } from '../../api/generated/client';
import { Button } from '../../shared/components/Button';

type UploadInvoiceDialogProps = {
  /** Allows the queue feature and tests to supply the stable feature adapter. */
  api?: Pick<InvoiceApi, 'upload'>;
};

function UploadProblem({ error }: { error: unknown }) {
  if (!isInvoiceApiError(error)) {
    return <p className="upload-problem" role="alert">The invoice could not be uploaded. Please try again.</p>;
  }

  return (
    <section className="upload-problem" role="alert" aria-labelledby="upload-error-title">
      <h3 id="upload-error-title">{error.title}</h3>
      <p>{error.detail}</p>
      {error.code ? <p className="upload-problem__code">Reference: {error.code}</p> : null}
      {error.correlationId ? <p className="upload-problem__code">Support ID: {error.correlationId}</p> : null}
    </section>
  );
}

function ProcessingFailure({ invoice }: { invoice: InvoiceDetailDto }) {
  const failure = invoice.processingFailure;
  if (!failure) return null;

  return (
    <section className="upload-processing-failure" role="status" aria-labelledby="upload-processing-title">
      <h3 id="upload-processing-title">Invoice created, but processing needs attention</h3>
      <p>{failure.message}</p>
      <p className="upload-problem__code">{failure.code} during {failure.stage}</p>
      <p>The invoice record is available for review.</p>
    </section>
  );
}

export function UploadInvoiceDialog({ api = invoiceApi }: UploadInvoiceDialogProps) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const inputId = useId();
  const [open, setOpen] = useState(false);
  const [file, setFile] = useState<File | null>(null);
  const [selectionError, setSelectionError] = useState<string | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  const upload = useMutation({
    mutationFn: (selectedFile: File) => api.upload(selectedFile),
    onSuccess: async (invoice) => {
      await applyUploadMutation(queryClient, invoice);
      setOpen(false);
      setFile(null);
      setSelectionError(null);
      navigate(`/invoices/${encodeURIComponent(invoice.id)}`);
    },
  });

  useEffect(() => {
    if (upload.isError) inputRef.current?.focus();
  }, [upload.isError]);

  function chooseFile(event: ChangeEvent<HTMLInputElement>) {
    const files = event.target.files;
    upload.reset();
    if (!files?.length) {
      setFile(null);
      setSelectionError('Choose one PDF to upload.');
      return;
    }
    if (files.length !== 1) {
      setFile(null);
      setSelectionError('Choose only one PDF at a time.');
      event.target.value = '';
      return;
    }

    setFile(files[0]);
    setSelectionError(null);
  }

  function submit() {
    if (!file) {
      setSelectionError('Choose one PDF to upload.');
      inputRef.current?.focus();
      return;
    }
    upload.mutate(file);
  }

  function close(nextOpen: boolean) {
    if (upload.isPending) return;
    setOpen(nextOpen);
    if (!nextOpen) {
      setFile(null);
      setSelectionError(null);
      upload.reset();
    }
  }

  return (
    <Dialog.Root open={open} onOpenChange={close}>
      <Dialog.Trigger asChild>
        <Button>Upload invoice</Button>
      </Dialog.Trigger>
      <Dialog.Portal>
        <Dialog.Overlay className="dialog-overlay" />
        <Dialog.Content className="dialog-content upload-dialog" aria-describedby={`${inputId}-guidance`}>
          <Dialog.Title>Upload invoice PDF</Dialog.Title>
          <Dialog.Description id={`${inputId}-guidance`}>
            Select one PDF. The service checks the configured size and page limits and validates the document before accepting it.
          </Dialog.Description>
          <div className="upload-dialog__field">
            <label htmlFor={inputId}>Invoice PDF</label>
            <input
              ref={inputRef}
              id={inputId}
              name="file"
              type="file"
              accept="application/pdf,.pdf"
              onChange={chooseFile}
              disabled={upload.isPending}
              aria-describedby={`${inputId}-guidance ${selectionError ? `${inputId}-error` : ''}`}
            />
            {file ? <p className="upload-dialog__selected">Selected: {file.name}</p> : null}
            {selectionError ? <p id={`${inputId}-error`} className="upload-problem" role="alert">{selectionError}</p> : null}
          </div>
          {upload.isPending ? <p className="page-loading" role="status"><span className="loading-indicator" aria-hidden="true" />Uploading and processing invoice…</p> : null}
          {upload.isError ? <UploadProblem error={upload.error} /> : null}
          {upload.data?.processingFailure ? <ProcessingFailure invoice={upload.data} /> : null}
          <div className="dialog-actions">
            <Dialog.Close asChild><Button tone="secondary" disabled={upload.isPending}>Cancel</Button></Dialog.Close>
            <Button onClick={submit} disabled={upload.isPending} aria-busy={upload.isPending}>
              {upload.isPending ? 'Uploading…' : 'Upload invoice'}
            </Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
