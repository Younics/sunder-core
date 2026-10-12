import { useEffect, useState } from "react";
import { useApi } from "@sunder/sdk/react";
import { Self } from "#sunder/api";

type Status =
  | { readonly kind: "loading" }
  | { readonly kind: "ready"; readonly message: string }
  | { readonly kind: "error"; readonly message: string };

const packageId = "__SUNDER_PACKAGE_ID_JSON__";
const packageName = "__SUNDER_PACKAGE_NAME_JSON__";

export function App() {
  const api = useApi(Self);
  const [status, setStatus] = useState<Status>({ kind: "loading" });

  useEffect(() => {
    const cancellation = new AbortController();
    void api.messages.send("Sunder App", { signal: cancellation.signal }).then(
      (message) => setStatus({ kind: "ready", message }),
      (error: unknown) => {
        if (cancellation.signal.aborted) return;
        const message = error instanceof Error ? error.message : "The package service is unavailable.";
        setStatus({ kind: "error", message });
      },
    );
    return () => cancellation.abort();
  }, [api]);

  return (
    <main className="shell">
      <section className="hero">
        <p className="eyebrow">{packageId}</p>
        <h1>{packageName}</h1>
        <p className="lede">A sandboxed React view backed by an exact Node Runtime activation.</p>
      </section>
      <section className={`status status--${status.kind}`} aria-live="polite">
        <span className="status__signal" />
        <div>
          <p className="status__label">Runtime bridge</p>
          <p className="status__message">
            {status.kind === "loading" ? "Loading the greeting..." : status.message}
          </p>
        </div>
      </section>
      {status.kind === "error" && (
        <p className="hint">Check this package's permissions in Sunder Settings, then reload the view.</p>
      )}
    </main>
  );
}
