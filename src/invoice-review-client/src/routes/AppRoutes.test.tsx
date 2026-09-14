import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { AppRoutes } from './AppRoutes';

describe('application route shells', () => {
  it('renders the accessible invoice queue shell', () => {
    render(<MemoryRouter initialEntries={['/']}><AppRoutes /></MemoryRouter>);

    expect(screen.getByRole('heading', { name: 'Invoice queue' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Upload invoice' })).toBeDisabled();
    expect(screen.getByRole('navigation', { name: 'Primary navigation' })).toBeInTheDocument();
  });

  it('renders an invoice workspace shell and supports keyboard return to the queue', async () => {
    const user = userEvent.setup();
    render(<MemoryRouter initialEntries={['/invoices/inv-042']}><AppRoutes /></MemoryRouter>);

    expect(screen.getByRole('heading', { name: 'Invoice review' })).toBeInTheDocument();
    expect(screen.getByText('inv-042')).toHaveClass('numeric-value');

    await user.tab();
    expect(screen.getByRole('link', { name: 'Invoice Review Assistant home' })).toHaveFocus();
    await user.keyboard('{Tab}{Tab}{Enter}');
    expect(screen.getByRole('heading', { name: 'Invoice queue' })).toBeInTheDocument();
  });
});
