import { useEffect, useState } from "react";
import { getSunder, isSunderRpcFailure } from "@sunder/sdk/browser";

type Status =
  | { readonly kind: "loading" }
  | { readonly kind: "ready"; readonly message: string }
  | { readonly kind: "error"; readonly message: string };

export function App() {
  const [status, setStatus] = useState<Status>({ kind: "loading" });

  useEffect(() => {
    const cancellation = new AbortController();
    void greet(cancellation.signal).then(
      (message) => setStatus({ kind: "ready", message }),
      (error: unknown) => {
        if (cancellation.signal.aborted) return;
        const message = isSunderRpcFailure(error)
          ? `${error.error.code}: ${error.error.message}`
          : error instanceof Error ? error.message : "The package Runtime is unavailable.";
        setStatus({ kind: "error", message });
      },
    );
    return () => cancellation.abort();
  }, []);

  return (
    <main className="shell">
      <section className="hero">
        <p className="eyebrow">SUNDER_PACKAGE_ID</p>
        <h1>SUNDER_PACKAGE_NAME</h1>
        <p className="lede">A sandboxed React view backed by an exact Node Runtime activation.</p>
      </section>
      <section className={`status status--${status.kind}`} aria-live="polite">
        <span className="status__signal" />
        <div>
          <p className="status__label">Runtime bridge</p>
          <p className="status__message">
            {status.kind === "loading" ? "Discovering the package provider..." : status.message}
          </p>
        </div>
      </section>
      {status.kind === "error" && (
        <p className="hint">Grant this package the requested RPC permissions in Sunder Settings, then reload the view.</p>
      )}
    </main>
  );
}

async function greet(signal: AbortSignal): Promise<string> {
  const rpc = getSunder().rpc;
  const catalog = await rpc.discover("example.messages", { signal });
  const provider = catalog.providers.find((candidate) => candidate.providerId === "SUNDER_PACKAGE_ID.provider");
  if (provider === undefined) throw new Error("The exact package provider is not active.");
  const response = await rpc.invoke<{ readonly message: string }, { readonly message: string }>(
    provider.providerHandle,
    "messages",
    "send",
    { message: "Sunder App bridge" },
    { signal },
  );
  return response.message;
}
