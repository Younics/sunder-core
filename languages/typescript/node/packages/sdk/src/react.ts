import { createContext, createElement, useContext, useEffect, useMemo, useRef, type ReactNode } from "react";
import { createDeferredApi, type ApiContext, type ApiToken } from "./api";

const Owner = createContext<ApiContext | null>(null);
/** The application owns and retires the root; each hook owns only its component calls. */
export function ApiProvider(props: { readonly context: ApiContext; readonly children?: ReactNode }): ReactNode {
  return createElement(Owner.Provider, { value: props.context }, props.children);
}
export function useApi<T>(token: ApiToken<T>): T {
  const owner = useContext(Owner);
  if (!owner) throw new Error("useApi requires an ApiProvider for this application root.");
  const current = useRef<ApiContext | undefined>(undefined);
  useEffect(() => {
    const context = owner.fork(); current.current = context;
    return () => { context.dispose(); if (current.current === context) current.current = undefined; };
  }, [owner]);
  return useMemo(() => createDeferredApi(token, () => {
    if (!current.current) throw new Error("Service calls require a mounted component; do not invoke services during render.");
    return current.current;
  }), [owner, token]);
}
