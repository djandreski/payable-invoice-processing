import type { ReactNode } from 'react';
import { Link, NavLink } from 'react-router-dom';

type AppShellProps = {
  children: ReactNode;
  title: string;
  description: string;
  actions?: ReactNode;
};

export function AppShell({ actions, children, description, title }: AppShellProps) {
  return (
    <div className="app-frame">
      <header className="app-header">
        <div className="app-header__inner">
          <Link className="brand" to="/" aria-label="Invoice Review Assistant home">
            <span className="brand__mark" aria-hidden="true">IR</span>
            <span>Invoice Review Assistant</span>
          </Link>
          <nav aria-label="Primary navigation">
            <NavLink className={({ isActive }) => `nav-link${isActive ? ' nav-link--active' : ''}`} to="/" end>
              Invoice queue
            </NavLink>
          </nav>
        </div>
      </header>
      <main className="app-main">
        <section className="page-heading" aria-labelledby="page-title">
          <div>
            <p className="eyebrow">Accounts payable</p>
            <h1 id="page-title">{title}</h1>
            <p>{description}</p>
          </div>
          {actions ? <div className="page-heading__actions">{actions}</div> : null}
        </section>
        {children}
      </main>
    </div>
  );
}
