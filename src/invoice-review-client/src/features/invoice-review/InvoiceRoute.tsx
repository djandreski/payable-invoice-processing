import { Link, useParams } from 'react-router-dom';
import { AppShell } from '../../shared/layout/AppShell';

export function InvoiceRoute() {
  const { invoiceId } = useParams();

  return (
    <AppShell
      title="Invoice review"
      description="Compare the source document with the extracted record before making a decision."
    >
      <section className="route-card" aria-labelledby="review-foundation-title">
        <p className="eyebrow">Invoice workspace</p>
        <h2 id="review-foundation-title">Review workspace is being prepared.</h2>
        <p>
          Invoice reference: <span className="numeric-value">{invoiceId ?? 'Not available'}</span>
        </p>
        <Link className="text-link" to="/">Return to invoice queue</Link>
      </section>
    </AppShell>
  );
}
