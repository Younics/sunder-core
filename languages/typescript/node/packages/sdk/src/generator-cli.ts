#!/usr/bin/env node
import { readFile, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { parseRpcContractDescriptor } from "./descriptor";
import { generateTypeScriptBindings } from "./generator";

async function main(): Promise<void> {
  const [input, output] = process.argv.slice(2);
  if (input === undefined || output === undefined || process.argv.length !== 4) {
    throw new Error("Usage: sunder-rpc-gen <descriptor.json> <bindings.ts>");
  }
  const descriptor = parseRpcContractDescriptor(await readFile(resolve(input)));
  await writeFile(resolve(output), generateTypeScriptBindings(descriptor), "utf8");
}

void main().catch((error: unknown) => {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
});
