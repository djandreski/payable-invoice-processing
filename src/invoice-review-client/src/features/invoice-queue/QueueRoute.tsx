import { Button } from '../../shared/components/Button';
import { AppShell } from '../../shared/layout/AppShell';

export function QueueRoute() {
  return (
    <AppShell
      title="Invoice queue"
      description="Review incoming invoices, resolve exceptions, and keep decisions traceable."
      actions={<Button disabled aria-describedby="upload-availability">Upload invoice</Button>}
    >
      <p id="upload-availability" className="visually-hidden">Upload becomes available when the invoice service is connected.</p>
      <section className="route-card" aria-labelledby="queue-foundation-title">
        <p className="eyebrow">Workspace ready</p>
        <h2 id="queue-foundation-title">Your invoice queue will appear here.</h2>
        <p>
          This foundation provides the accessible workspace shell. Queue data, search, filters, and upload are added as invoice features become available.
        </p>
      </section>
    </AppShell>
  );
}
