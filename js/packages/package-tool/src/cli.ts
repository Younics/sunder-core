#!/usr/bin/env node
import { resolve } from "node:path";
import { aggregateAndPack, buildDevPackage, buildWebTarget } from "./package";
import { buildSeaTarget, smokeSeaTarget } from "./sea";
import { SUPPORTED_RIDS, type SunderRid } from "./model";

async function main(): Promise<void> {
  const [command, ...argumentsList] = process.argv.slice(2);
  if (command === undefined || command === "--help" || command === "-h") {
    help();
    return;
  }
  const options = parse(argumentsList);
  const projectPath = resolve(options.project ?? process.cwd());
  switch (command) {
    case "dev":
      await buildDevPackage({ projectPath, watch: options.watch });
      break;
    case "build":
      await buildSeaTarget({
        projectPath,
        ...(options.rid === undefined ? {} : { rid: options.rid }),
        allowUnsignedWindows: options.allowUnsignedWindows,
      });
      await buildWebTarget(projectPath, options.rid);
      break;
    case "smoke":
      await smokeSeaTarget(projectPath, options.rid);
      break;
    case "package":
      await aggregateAndPack(projectPath, options.leaves);
      break;
    default:
      throw new Error(`Unknown command '${command}'. Run sunder-package --help.`);
  }
}

function parse(argumentsList: readonly string[]): CliOptions {
  let project: string | undefined;
  let rid: SunderRid | undefined;
  let watch = false;
  let allowUnsignedWindows = false;
  const leaves: string[] = [];
  for (let index = 0; index < argumentsList.length; index++) {
    const argument = argumentsList[index];
    switch (argument) {
      case "--project":
        project = requiredValue(argumentsList, ++index, argument);
        break;
      case "--rid": {
        const value = requiredValue(argumentsList, ++index, argument);
        if (!SUPPORTED_RIDS.includes(value as SunderRid)) throw new Error(`Unsupported exact RID '${value}'.`);
        rid = value as SunderRid;
        break;
      }
      case "--leaf":
        leaves.push(resolve(requiredValue(argumentsList, ++index, argument)));
        break;
      case "--watch":
        watch = true;
        break;
      case "--allow-unsigned-windows":
        allowUnsignedWindows = true;
        break;
      default:
        throw new Error(`Unknown option '${argument}'.`);
    }
  }
  return { project, rid, watch, allowUnsignedWindows, leaves };
}

function requiredValue(argumentsList: readonly string[], index: number, option: string): string {
  const value = argumentsList[index];
  if (value === undefined || value.startsWith("--")) throw new Error(`${option} requires a value.`);
  return value;
}

function help(): void {
  process.stdout.write(`Sunder TypeScript package tooling\n\nCommands:\n  dev      Build canonical current-RID process and optional Vite App targets (--watch optional)\n  build    Build pinned Node 24.18.1 SEA and optional Vite App exact-RID target leaves\n  smoke    Run a native sunder.worker.v1 handshake against the built SEA\n  package  Aggregate target leaves and write a deterministic .sunderpkg\n\nOptions:\n  --project <path>\n  --rid <exact-rid>\n  --leaf <canonical-leaf> (repeatable for package)\n  --watch\n  --allow-unsigned-windows\n`);
}

interface CliOptions {
  readonly project?: string;
  readonly rid?: SunderRid;
  readonly watch: boolean;
  readonly allowUnsignedWindows: boolean;
  readonly leaves: readonly string[];
}

void main().catch((error: unknown) => {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
});
