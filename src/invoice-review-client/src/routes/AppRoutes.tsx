import { Route, Routes } from 'react-router-dom';
import { InvoiceRoute } from '../features/invoice-review/InvoiceRoute';
import { QueueRoute } from '../features/invoice-queue/QueueRoute';

export function AppRoutes() {
  return (
    <Routes>
      <Route path="/" element={<QueueRoute />} />
      <Route path="/invoices/:invoiceId" element={<InvoiceRoute />} />
      <Route path="*" element={<QueueRoute />} />
    </Routes>
  );
}
