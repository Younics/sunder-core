// Partition declaration identities, never adapter-expanded theory rows. Assignment
// precedes OS filtering so the same method has the same owner on every native RID.
export function nativePartitionNames(model) {
  return ["main", ...rulesFor(model).flatMap(rule =>
    Array.from({ length: rule.shards }, (_, index) => `${rule.name}-${index + 1}`))];
}

export function selectNativePartition(model, name, operatingSystem) {
  if (!nativePartitionNames(model).includes(name)) throw new Error(`Unknown native partition '${name}' for scope '${model.scope}'.`);
  const tests = model.declarations.filter(test => test.framework === "dotnet" && test.primaryLane === "host-platform");
  const assignments = new Map(tests.map(test => [test.id, "main"]));
  for (const rule of rulesFor(model)) {
    const projectTests = tests.filter(test => test.project === rule.project).sort((left, right) =>
      left.id < right.id ? -1 : left.id > right.id ? 1 : 0);
    if (projectTests.length < rule.shards || projectTests.some(test => test.component !== rule.component)) {
      throw new Error(`Native partition '${rule.name}' has insufficient declarations or inconsistent component ownership.`);
    }
    for (const [index, test] of projectTests.entries()) assignments.set(test.id, `${rule.name}-${index % rule.shards + 1}`);
  }
  return tests.filter(test => assignments.get(test.id) === name
    && (operatingSystem === undefined || test.operatingSystems.includes(operatingSystem)));
}

function rulesFor(model) {
  const owners = model.policy.scopes?.[model.scope ?? "all"];
  return (model.policy.nativePartitions ?? []).filter(rule => owners === undefined || owners.includes(rule.component));
}
