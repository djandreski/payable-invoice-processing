import { Component, type ErrorInfo, type ReactNode } from 'react';
import { Button } from './Button';

type AppErrorBoundaryProps = {
  children: ReactNode;
};

type AppErrorBoundaryState = {
  hasError: boolean;
};

export class AppErrorBoundary extends Component<AppErrorBoundaryProps, AppErrorBoundaryState> {
  public state: AppErrorBoundaryState = { hasError: false };

  public static getDerivedStateFromError(): AppErrorBoundaryState {
    return { hasError: true };
  }

  public componentDidCatch(_error: Error, _errorInfo: ErrorInfo): void {
    // Error reporting is intentionally connected by the host, never with invoice data here.
  }

  private readonly recover = (): void => {
    this.setState({ hasError: false });
  };

  public render(): ReactNode {
    if (this.state.hasError) {
      return (
        <main className="app-recovery" aria-labelledby="application-error-title">
          <section className="app-recovery__card" role="alert">
            <p className="eyebrow">Application issue</p>
            <h1 id="application-error-title">We couldn’t display this page.</h1>
            <p>Try loading the workspace again. Your saved invoice records are unaffected.</p>
            <Button onClick={this.recover}>Try again</Button>
          </section>
        </main>
      );
    }

    return this.props.children;
  }
}
