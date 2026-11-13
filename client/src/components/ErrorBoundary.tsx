import React, { Component, type ErrorInfo, type ReactNode } from "react";
import { AlertTriangle, Home, RotateCcw } from "lucide-react";
import { Button } from "@/components/ui/button";

interface Props {
  children: ReactNode;
  fallbackTitle?: string;
  fallbackMessage?: string;
}

interface State {
  hasError: boolean;
  error: Error | null;
}

export class ErrorBoundary extends Component<Props, State> {
  public state: State = {
    hasError: false,
    error: null,
  };

  public static getDerivedStateFromError(error: Error): State {
    return { hasError: true, error };
  }

  public componentDidCatch(error: Error, errorInfo: ErrorInfo) {
    console.error("ErrorBoundary caught an unhandled render error:", error, errorInfo);
  }

  private handleReset = () => {
    this.setState({ hasError: false, error: null });
  };

  public render() {
    if (this.state.hasError) {
      return (
        <div className="flex min-h-[50vh] flex-col items-center justify-center p-6 text-center">
          <div className="mx-auto max-w-md rounded-2xl border border-destructive/30 bg-destructive/5 p-6 shadow-sm">
            <div className="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-destructive/10 text-destructive">
              <AlertTriangle className="h-6 w-6" aria-hidden="true" />
            </div>

            <h2 className="font-display text-xl font-bold text-ink">
              {this.props.fallbackTitle || "Something went wrong"}
            </h2>

            <p className="mt-2 text-sm text-muted-foreground">
              {this.props.fallbackMessage ||
                "We encountered an unexpected error while loading this page. Please try refreshing or return to the home screen."}
            </p>

            {this.state.error?.message && (
              <p className="mt-3 rounded-lg bg-card/80 p-2 font-mono text-xs text-muted-foreground border">
                {this.state.error.message}
              </p>
            )}

            <div className="mt-5 flex flex-wrap items-center justify-center gap-2">
              <Button size="sm" onClick={this.handleReset}>
                <RotateCcw className="mr-1.5 h-3.5 w-3.5" /> Try again
              </Button>
              <Button size="sm" variant="outline" onClick={() => window.location.reload()}>
                Reload page
              </Button>
              <Button size="sm" variant="ghost" asChild>
                <a href="/">
                  <Home className="mr-1.5 h-3.5 w-3.5" /> Home
                </a>
              </Button>
            </div>
          </div>
        </div>
      );
    }

    return this.props.children;
  }
}
