import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { ConfirmationDialog } from './ConfirmationDialog';

describe('ConfirmationDialog', () => {
  it('opens from a keyboard-accessible trigger and restores focus when cancelled', async () => {
    const user = userEvent.setup();
    const onConfirm = vi.fn();
    render(
      <ConfirmationDialog
        title="Confirm action"
        description="This action needs a clear confirmation."
        triggerLabel="Open confirmation"
        confirmLabel="Confirm"
        onConfirm={onConfirm}
      >
        <p>Review the action before confirming.</p>
      </ConfirmationDialog>,
    );

    const trigger = screen.getByRole('button', { name: 'Open confirmation' });
    await user.tab();
    expect(trigger).toHaveFocus();
    await user.keyboard('{Enter}');
    expect(screen.getByRole('dialog')).toHaveAccessibleName('Confirm action');

    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(trigger).toHaveFocus();
    expect(onConfirm).not.toHaveBeenCalled();
  });
});
