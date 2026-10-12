// @ts-check
import { useEffect, useState } from "react";
import { useApi } from "@sunder/sdk/react";
import { Self } from "#sunder/api";
const packageName = "__SUNDER_PACKAGE_NAME_JSON__";

export function App() {
  const api = useApi(Self);
  const [message, setMessage] = useState("Loading the greeting...");
  useEffect(() => {
    const cancellation = new AbortController();
    void api.messages.send("Sunder App", { signal: cancellation.signal }).then(setMessage, error => {
      if (!cancellation.signal.aborted) setMessage(error instanceof Error ? error.message : "The package service is unavailable.");
    });
    return () => cancellation.abort();
  }, [api]);
  return <main className="shell"><section className="hero">
    <p className="eyebrow">__SUNDER_PACKAGE_ID_JSON__</p>
    <h1>{packageName}</h1>
    <p className="lede" aria-live="polite">{message}</p>
  </section></main>;
}
