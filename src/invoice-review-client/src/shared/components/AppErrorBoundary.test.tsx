import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { AppErrorBoundary } from './AppErrorBoundary';

function BrokenView(): never {
  throw new Error('test error');
}

describe('AppErrorBoundary', () => {
  it('provides an accessible recovery action after an unexpected rendering failure', async () => {
    const error = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const user = userEvent.setup();

    render(<AppErrorBoundary><BrokenView /></AppErrorBoundary>);

    expect(screen.getByRole('heading', { name: 'We couldn’t display this page.' })).toBeInTheDocument();
    await user.tab();
    expect(screen.getByRole('button', { name: 'Try again' })).toHaveFocus();
    error.mockRestore();
  });
});
