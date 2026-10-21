// Conservative source/CLR-shape comparison of PublicApiFormatter v2 output.
// Hashes authenticate retained input bytes; they are not the compatibility test.
export const maximumSnapshotBytes = 8 * 1024 * 1024;

export function comparePublicApi(releasedText, currentText) {
  const released = parsePublicApi(releasedText);
  const current = parsePublicApi(currentText);
  const errors = [];
  if (released.assembly !== current.assembly) errors.push(`Assembly identity changed: ${released.assembly}`);
  for (const [id, oldType] of released.types) {
    const newType = current.types.get(id);
    if (!newType) {
      errors.push(`Released type removed: ${id}`);
      continue;
    }
    if (oldType.shape !== newType.shape) errors.push(`Released type shape changed: ${id}`);
    for (const member of oldType.members.keys()) {
      if (!newType.members.has(member)) errors.push(`Released member removed or changed: ${id}: ${member}`);
    }
    for (const [member, declaration] of newType.members) {
      if (!oldType.members.has(member) && requiresImplementation(declaration)) {
        errors.push(`New mandatory implementer/initializer obligation: ${id}: ${declaration}`);
      }
    }
  }
  return errors;
}

function requiresImplementation(declaration) {
  const modifiers = /^(?:method|field) ((?:(?:public|protected|internal|private|static|abstract|virtual|override|final|readonly|const|required) )*)/u.exec(declaration)?.[1] ?? "";
  return /\b(?:abstract|required)\b/u.test(modifiers)
    || declaration.startsWith("property required ")
    || /(?:\{ |; )(?:public |protected internal |protected |private protected |internal |private )(?:static )?abstract /u.test(declaration);
}

export function parsePublicApi(text) {
  if (typeof text !== "string" || Buffer.byteLength(text) > maximumSnapshotBytes) throw new Error("API snapshot exceeds size bound.");
  const lines = text.replaceAll("\r\n", "\n").split("\n");
  if (lines.shift() !== "# sunder-public-api-v2") throw new Error("Expected sunder-public-api-v2; legacy candidate snapshots are not release baselines.");
  const assembly = lines.shift();
  if (!/^assembly [A-Za-z0-9_.-]+, Version=\d+\.\d+\.\d+\.\d+, Culture=[A-Za-z0-9-]+, PublicKeyToken=(?:null|[a-f0-9]{16})$/u.test(assembly ?? "")) {
    throw new Error("API snapshot has no bounded assembly identity.");
  }
  if (lines.at(-1) === "") lines.pop();
  if (lines.length > 100_000) throw new Error("API snapshot exceeds line bound.");
  const types = new Map();
  let type;
  let attributes = [];
  let indent;
  for (const line of lines) {
    if (line.length > 32_768 || /[\r\x00-\x08\x0b-\x1f]/u.test(line)) throw new Error("Invalid API snapshot line.");
    if (/^(?:  )?attribute \S/u.test(line)) {
      const nextIndent = line.startsWith("  ") ? "  " : "";
      if (attributes.length && indent !== nextIndent) throw new Error("Mixed API attribute scope.");
      indent = nextIndent;
      attributes.push(line);
      continue;
    }
    if (attributes.length && indent !== (line.startsWith("  ") ? "  " : "")) throw new Error("Orphan API attribute.");
    const shape = [...attributes.sort(), line].join("\n");
    attributes = [];
    if (line.startsWith("type ")) {
      const id = typeIdentity(line);
      if (types.has(id)) throw new Error(`Duplicate API type: ${id}`);
      type = { shape, members: new Map() };
      types.set(id, type);
    } else if (/^  (?:method|field|property|event) \S/u.test(line) && type) {
      if (type.members.has(shape)) throw new Error(`Duplicate API member: ${line}`);
      type.members.set(shape, line.slice(2));
    } else {
      throw new Error(`Unrecognized API snapshot line: ${line}`);
    }
  }
  if (attributes.length) throw new Error("Orphan API attribute.");
  if (types.size === 0) throw new Error("API snapshot contains no types.");
  return { assembly, types };
}

function typeIdentity(line) {
  const rest = /^type (?:public|protected internal|protected) (?:(?:abstract|sealed|static) )?(?:class|struct|interface|enum) (.+)$/u.exec(line)?.[1];
  if (!rest) throw new Error(`Invalid API type: ${line}`);
  let depth = 0;
  let end = rest.length;
  for (let index = 0; index < rest.length; index++) {
    if (rest[index] === "<") depth++;
    else if (rest[index] === ">") depth--;
    else if (depth === 0 && rest[index] === " ") { end = index; break; }
    if (depth < 0 || depth > 64) throw new Error("Invalid generic API type nesting.");
  }
  if (depth !== 0) throw new Error("Unclosed generic API type.");
  return rest.slice(0, end).replaceAll(/\b(?:in|out) /gu, "");
}
