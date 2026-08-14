import { createHash } from "node:crypto";

const STORAGE_KEY = /^[A-Za-z0-9._-]{1,256}$/u;
const MAXIMUM_VALUE_BYTES = 1024 * 1024;
const MAXIMUM_INT32 = 0x7fffffff;

export type PackageStorageKeyMigrationAction =
  | Readonly<{ kind: "no-match" }>
  | Readonly<{ kind: "delete" }>
  | Readonly<{ kind: "rewrite"; destinationKey: string; precedence: number }>;

export const PackageStorageKeyMigrationAction = Object.freeze({
  noMatch: Object.freeze({ kind: "no-match" } as const),
  delete: Object.freeze({ kind: "delete" } as const),
  rewrite(destinationKey: string, precedence = 0): PackageStorageKeyMigrationAction {
    validateStorageKey(destinationKey, "Migration destination key");
    if (!Number.isSafeInteger(precedence) || precedence < 0 || precedence > MAXIMUM_INT32) {
      throw new RangeError("Migration precedence must be a non-negative signed 32-bit integer.");
    }
    return Object.freeze({ kind: "rewrite", destinationKey, precedence });
  },
});

export type PackageStorageKeyMigration =
  | Readonly<{
    kind: "exact";
    legacyKey: string;
    destinationKey: string;
  }>
  | Readonly<{
    kind: "opaque-id";
    legacyPrefix: string;
    legacySuffix: string;
    destinationPrefix: string;
    destinationVersion: number;
  }>
  | Readonly<{
    kind: "opaque-id-json" | "opaque-id-json-case-insensitive";
    legacyPrefix: string;
    legacySuffix: string;
    destinationPrefix: string;
    destinationVersion: number;
    identityProperty: string;
  }>
  | Readonly<{
    kind: "dynamic-cleanup";
    resolve(legacyKey: string): PackageStorageKeyMigrationAction;
  }>;

export const PackageStorageKeyMigration = Object.freeze({
  exact(legacyKey: string, destinationKey: string): PackageStorageKeyMigration {
    validateLegacyPart(legacyKey, "Migration legacy key", false);
    validateStorageKey(destinationKey, "Migration destination key");
    if (legacyKey === destinationKey) throw new TypeError("A package storage key migration must change the physical key.");
    return Object.freeze({ kind: "exact", legacyKey, destinationKey });
  },

  opaqueId(
    legacyPrefix: string,
    legacySuffix: string,
    destinationPrefix: string,
    destinationVersion: number,
  ): PackageStorageKeyMigration {
    return opaqueIdMigration(
      "opaque-id",
      legacyPrefix,
      legacySuffix,
      destinationPrefix,
      destinationVersion,
    );
  },

  opaqueIdWithJsonIdentity(
    legacyPrefix: string,
    legacySuffix: string,
    destinationPrefix: string,
    destinationVersion: number,
    identityProperty: string,
  ): PackageStorageKeyMigration {
    return opaqueIdMigration(
      "opaque-id-json",
      legacyPrefix,
      legacySuffix,
      destinationPrefix,
      destinationVersion,
      identityProperty,
    );
  },

  opaqueIdWithCaseInsensitiveJsonIdentity(
    legacyPrefix: string,
    legacySuffix: string,
    destinationPrefix: string,
    destinationVersion: number,
    identityProperty: string,
  ): PackageStorageKeyMigration {
    return opaqueIdMigration(
      "opaque-id-json-case-insensitive",
      legacyPrefix,
      legacySuffix,
      destinationPrefix,
      destinationVersion,
      identityProperty,
    );
  },

  dynamicCleanup(
    resolve: (legacyKey: string) => PackageStorageKeyMigrationAction,
  ): PackageStorageKeyMigration {
    if (typeof resolve !== "function") throw new TypeError("A dynamic storage cleanup resolver must be a function.");
    return Object.freeze({ kind: "dynamic-cleanup", resolve });
  },
});

export function createPackageStorageKey(prefix: string, version: number, opaqueId: string): string {
  validateStorageKey(prefix, "Package storage key prefix");
  if (!Number.isSafeInteger(version) || version < 1 || version > MAXIMUM_INT32) {
    throw new RangeError("Package storage key versions must be positive signed 32-bit integers.");
  }
  validateLegacyPart(opaqueId, "Opaque package storage identifier", false);
  const versionedPrefix = `${prefix}.v${version}.`;
  if (versionedPrefix.length + 64 > 256) {
    throw new TypeError("The versioned package storage key prefix leaves insufficient room within the 256-character key limit.");
  }
  return `${versionedPrefix}${createHash("sha256").update(opaqueId, "utf8").digest("hex")}`;
}

function opaqueIdMigration(
  kind: "opaque-id" | "opaque-id-json" | "opaque-id-json-case-insensitive",
  legacyPrefix: string,
  legacySuffix: string,
  destinationPrefix: string,
  destinationVersion: number,
  identityProperty?: string,
): PackageStorageKeyMigration {
  validateLegacyPart(legacyPrefix, "Migration legacy prefix", false);
  validateLegacyPart(legacySuffix, "Migration legacy suffix", true);
  createPackageStorageKey(destinationPrefix, destinationVersion, "validation");
  if (kind === "opaque-id") {
    return Object.freeze({ kind, legacyPrefix, legacySuffix, destinationPrefix, destinationVersion });
  }
  validateLegacyPart(identityProperty, "Migration JSON identity property", false);
  return Object.freeze({
    kind,
    legacyPrefix,
    legacySuffix,
    destinationPrefix,
    destinationVersion,
    identityProperty,
  });
}

function validateStorageKey(value: unknown, label: string): asserts value is string {
  if (typeof value !== "string" || !STORAGE_KEY.test(value)) {
    throw new TypeError(`${label} must be a portable ASCII token of at most 256 characters.`);
  }
}

function validateLegacyPart(value: unknown, label: string, allowEmpty: boolean): asserts value is string {
  if (typeof value !== "string"
    || !isWellFormed(value)
    || !allowEmpty && value.length === 0
    || Buffer.byteLength(value, "utf8") > MAXIMUM_VALUE_BYTES) {
    throw new TypeError(`${label} must contain bounded valid Unicode.`);
  }
}

function isWellFormed(value: string): boolean {
  for (let index = 0; index < value.length; index++) {
    const code = value.charCodeAt(index);
    if (code >= 0xd800 && code <= 0xdbff) {
      const next = value.charCodeAt(++index);
      if (next < 0xdc00 || next > 0xdfff) return false;
    } else if (code >= 0xdc00 && code <= 0xdfff) {
      return false;
    }
  }
  return true;
}
