import * as Dialog from '@radix-ui/react-dialog';
import type { ReactNode } from 'react';
import { Button } from './Button';

type ConfirmationDialogProps = {
  children: ReactNode;
  confirmLabel: string;
  description: string;
  onConfirm: () => void;
  title: string;
  tone?: 'primary' | 'danger';
  triggerLabel: string;
};

export function ConfirmationDialog({
  children,
  confirmLabel,
  description,
  onConfirm,
  title,
  tone = 'primary',
  triggerLabel,
}: ConfirmationDialogProps) {
  return (
    <Dialog.Root>
      <Dialog.Trigger asChild>
        <Button tone={tone}>{triggerLabel}</Button>
      </Dialog.Trigger>
      <Dialog.Portal>
        <Dialog.Overlay className="dialog-overlay" />
        <Dialog.Content className="dialog-content" aria-describedby="confirmation-description">
          <Dialog.Title>{title}</Dialog.Title>
          <Dialog.Description id="confirmation-description">{description}</Dialog.Description>
          {children}
          <div className="dialog-actions">
            <Dialog.Close asChild><Button tone="secondary">Cancel</Button></Dialog.Close>
            <Button tone={tone} onClick={onConfirm}>{confirmLabel}</Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
