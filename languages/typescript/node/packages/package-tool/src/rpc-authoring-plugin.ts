import type { Plugin } from "esbuild";
import type { LoadedProject } from "./model";

export function ownedRpcPlugins(project: LoadedProject): Plugin[] {
  const analysis = project.ownedRpc;
  if (!analysis) return [];
  return [analysis.runtime.plugin(analysis.revalidate)];
}

export async function revalidateOwnedRpc(project: LoadedProject): Promise<void> {
  await project.ownedRpc?.revalidate();
}
