import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import type { JsonValue } from "../src/types";

export interface WorkerV3ConformanceCorpus {
  readonly formatVersion: number;
  readonly protocol: {
    readonly identity: string;
    readonly version: number;
  };
  readonly envelopePropertySets: Readonly<Record<string, readonly string[]>>;
  readonly categories: Readonly<Record<string, {
    readonly requirements: readonly string[];
  }>>;
  readonly lifecycle: {
    readonly challenge: string;
    readonly sessionGeneration: number;
    readonly shutdownId: string;
    readonly shutdownReason: string;
  };
  readonly rpc: {
    readonly serviceId: string;
    readonly declaredErrors: Readonly<Record<string, readonly string[]>>;
    readonly unary: RpcCase & { readonly result: JsonValue };
    readonly stream: RpcCase & { readonly events: readonly JsonValue[] };
    readonly domainFault: RpcCase & {
      readonly error: {
        readonly kind: string;
        readonly code: string;
        readonly message: string;
        readonly details: JsonValue;
      };
    };
    readonly undeclaredDomainFault: RpcCase & {
      readonly thrownError: { readonly code: string; readonly message: string };
      readonly diagnosticRequired: boolean;
      readonly error: { readonly kind: string; readonly code: string; readonly message: string };
    };
  };
  readonly storageMigrations: {
    readonly capabilities: readonly string[];
    readonly plans: readonly StorageMigrationPlan[];
    readonly dynamicKeys: readonly string[];
    readonly dynamicActions: readonly DynamicMigrationAction[];
  };
  readonly packageData: {
    readonly key: string;
    readonly value: string;
  };
}

export interface StorageMigrationPlan {
  readonly store: "state" | "secrets";
  readonly rules: readonly StorageMigrationRule[];
}

export type StorageMigrationRule =
  | Readonly<{ kind: "exact"; legacyKey: string; destinationKey: string }>
  | Readonly<{ kind: "opaque-id"; legacyPrefix: string; legacySuffix: string; destinationPrefix: string; destinationVersion: number }>
  | Readonly<{ kind: "opaque-id-json" | "opaque-id-json-case-insensitive"; legacyPrefix: string; legacySuffix: string; destinationPrefix: string; destinationVersion: number; identityProperty: string }>
  | Readonly<{ kind: "dynamic-cleanup" }>;

export interface DynamicMigrationAction {
  readonly key: string;
  readonly kind: "no-match" | "delete" | "rewrite";
  readonly destinationKey: string | null;
  readonly precedence: number;
}

interface RpcCase {
  readonly id: string;
  readonly methodId: string;
  readonly request: JsonValue;
}

export const workerV3ConformanceCorpus = JSON.parse(readFileSync(resolve(
  __dirname,
  "../../../../../../../specs/worker/v3/conformance/corpus.json",
), "utf8")) as WorkerV3ConformanceCorpus;
