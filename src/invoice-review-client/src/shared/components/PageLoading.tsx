type PageLoadingProps = {
  label?: string;
};

export function PageLoading({ label = 'Loading workspace' }: PageLoadingProps) {
  return (
    <div className="page-loading" role="status" aria-live="polite">
      <span className="loading-indicator" aria-hidden="true" />
      {label}
    </div>
  );
}
