# Release Gates

`worker-v1-thresholds.json` contains reviewed upper bounds for Worker V1 release evidence. Native .NET release CI installs each exact-RID Worker archive through the real Runtime Host and emits one JSON artifact per RID. Node CI validates all six leaves in the deterministic universal runtime-backed package, installs it through the Ubuntu Runtime Host handoff, and emits `node-module.json`. Both lanes fail before upload when archive size, current-RID installed size, startup latency, process count, or resident memory exceeds these bounds.

The bounds intentionally allow normal hosted-runner variance while catching packaging duplication, accidental framework/runtime inclusion, process fan-out, startup regressions, and material RSS growth. Threshold changes require review with fresh evidence from the affected lane: six exact-RID metrics for .NET, or six-leaf validation plus the Ubuntu Runtime Host metric for Node.
