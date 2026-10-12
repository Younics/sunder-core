// This is a declaration inventory, not a C# compiler or an expanded xUnit case
// inventory. Keep build-conditional declarations; the adapter owns runtime cases.
export function csharpTestDeclarations(content) {
  const tokens = tokenize(content);
  const scopes = [];
  const output = [];
  let fileNamespace = "";
  for (let index = 0; index < tokens.length; index++) {
    const token = tokens[index];
    const parent = scopes.at(-1);
    if (token.text === "}") {
      scopes.pop();
    } else if (token.text === "{") {
      scopes.push({ kind: "block" });
    } else if (token.text === "namespace" && (parent === undefined || parent.kind === "namespace")) {
      let end = index + 1;
      while (tokens[end]?.identifier || tokens[end]?.text === ".") end++;
      const name = tokens.slice(index + 1, end).map(identifierText).join("");
      if (tokens[end]?.text === ";") fileNamespace = name;
      else if (tokens[end]?.text === "{") scopes.push({ kind: "namespace", name });
      else continue;
      index = end;
    } else if (["class", "struct", "interface", "record"].includes(token.text)
        && (parent === undefined || ["namespace", "type"].includes(parent.kind))) {
      let nameIndex = index + 1;
      if (token.text === "record" && ["class", "struct"].includes(tokens[nameIndex]?.text)) nameIndex++;
      if (!tokens[nameIndex]?.identifier) continue;
      let name = identifierText(tokens[nameIndex]);
      if (tokens[nameIndex + 1]?.text === "<") {
        const end = balancedEnd(tokens, nameIndex + 1, "<", ">");
        const arity = 1 + tokens.slice(nameIndex + 2, end - 1).filter((item) => item.text === ",").length;
        name += `\`${arity}`;
      }
      let end = nameIndex + 1;
      while (end < tokens.length && !["{", ";"].includes(tokens[end].text)) {
        if (tokens[end].text === "(") end = balancedEnd(tokens, end, "(", ")");
        else if (tokens[end].text === "[") end = balancedEnd(tokens, end, "[", "]");
        else end++;
      }
      if (tokens[end]?.text === "{") scopes.push({ kind: "type", name });
      index = end;
    } else if (token.text === "[" && parent?.kind === "type") {
      const { attributes, end } = readAttributes(tokens, index);
      index = end - 1;
      if (!attributes.some((attribute) => /(?:Fact|Theory)(?:Attribute)?$/u.test(attribute.name))) continue;
      let method = end;
      while (method < tokens.length && !["(", "{", ";", "=", "}"].includes(tokens[method].text)) method++;
      const header = tokens.slice(end, method);
      if (tokens[method]?.text !== "(" || !header.some((item) => item.text === "public")
          || header.some((item) => ["class", "struct", "interface", "record"].includes(item.text))
          || !header.at(-1)?.identifier) continue;
      const namespace = [fileNamespace, ...scopes.filter((scope) => scope.kind === "namespace").map((scope) => scope.name)]
        .filter(Boolean).join(".");
      const className = scopes.filter((scope) => scope.kind === "type").map((scope) => scope.name).join("+");
      // Data/Skip strings can contain arbitrary C# fixture text. Only actual
      // attribute names and Trait arguments may classify a declaration's lane.
      const policyAttributes = attributes.map((attribute) => attribute.name.replace(/Attribute$/u, "") === "Trait"
        ? attribute.text.replace(/TraitAttribute\(/u, "Trait(") : `[${attribute.name}]`).join("");
      output.push({
        id: [namespace, className, identifierText(header.at(-1))].filter(Boolean).join("."),
        name: identifierText(header.at(-1)),
        attributes: attributes.map((attribute) => attribute.text).join(""),
        policyAttributes,
        attributeNames: attributes.map((attribute) => attribute.name),
        externalAttribute: attributes.find((attribute) => /^ExternalArtifact(?:Fact|Theory)(?:Attribute)?$/u.test(attribute.name))?.text,
      });
    }
  }
  return output;
}

function readAttributes(tokens, start) {
  const attributes = [];
  let end = start;
  while (tokens[end]?.text === "[") {
    const close = balancedEnd(tokens, end, "[", "]") - 1;
    let start = ++end;
    while (end <= close) {
      if (tokens[end]?.text === "(") end = balancedEnd(tokens, end, "(", ")");
      else if (tokens[end]?.text === "[") end = balancedEnd(tokens, end, "[", "]");
      else if (end === close || tokens[end]?.text === ",") {
        const parts = tokens.slice(start, end);
        const argument = parts.findIndex((part) => part.text === "(");
        const name = (argument < 0 ? parts : parts.slice(0, argument)).filter((part) => part.identifier).at(-1)?.text.replace(/^@/u, "") ?? "";
        attributes.push({ name, text: `[${parts.map((part) => part.text).join("")}]` });
        start = ++end;
      } else end++;
    }
  }
  return { attributes, end };
}

function balancedEnd(tokens, start, open, close) {
  let depth = 0;
  for (let index = start; index < tokens.length; index++) {
    if (tokens[index].text === open) depth++;
    if (tokens[index].text === close && --depth === 0) return index + 1;
  }
  return tokens.length;
}

function identifierText(token) {
  return token.text.replace(/^@/u, "");
}

function tokenize(content) {
  const tokens = [];
  const identifier = /@?[\p{L}_][\p{L}\p{N}\p{Mn}\p{Mc}\p{Pc}\p{Cf}_]*/uy;
  for (let index = 0; index < content.length;) {
    if (/\s/u.test(content[index])) { index++; continue; }
    const comment = commentEnd(content, index);
    if (comment !== undefined) { index = comment; continue; }
    // Directives do not contain declaration tokens. Do not select a build/OS
    // branch here: conditional Facts and external release gates stay inventoried.
    if (content[index] === "#") {
      index = lineEnd(content, index);
      continue;
    }
    const literal = literalEnd(content, index);
    if (literal !== undefined) {
      tokens.push({ text: content.slice(index, literal) });
      index = literal;
      continue;
    }
    identifier.lastIndex = index;
    const match = identifier.exec(content);
    if (match !== null) {
      tokens.push({ text: match[0], identifier: true });
      index = identifier.lastIndex;
    } else tokens.push({ text: content[index++] });
  }
  return tokens;
}

function commentEnd(content, start) {
  if (content.startsWith("//", start)) return lineEnd(content, start);
  if (content.startsWith("/*", start)) {
    const end = content.indexOf("*/", start + 2);
    return end < 0 ? content.length : end + 2;
  }
}

function lineEnd(content, start) {
  const end = content.indexOf("\n", start);
  return end < 0 ? content.length : end;
}

function literalEnd(content, start) {
  let quote = start;
  while (content[quote] === "$" || content[quote] === "@") quote++;
  const prefix = content.slice(start, quote);
  if (content[quote] !== '"' && (prefix !== "" || content[quote] !== "'")) return undefined;
  const delimiter = content[quote];
  const verbatim = prefix.includes("@");
  const interpolated = prefix.includes("$");
  if (delimiter === '"' && !verbatim && content.startsWith('"""', quote)) {
    let width = 3;
    while (content[quote + width] === '"') width++;
    const dollars = prefix.length;
    const closingQuotes = '"'.repeat(width);
    const openingInterpolation = "{".repeat(dollars);
    for (let index = quote + width; index < content.length; index++) {
      if (content.startsWith(closingQuotes, index)) {
        while (content[index] === '"') index++;
        return index;
      }
      if (dollars > 0 && content.startsWith(openingInterpolation, index)) {
        while (content[index + 1] === "{") index++;
        index = interpolationEnd(content, index, dollars) - 1;
      }
    }
    return content.length;
  }
  for (let index = quote + 1; index < content.length; index++) {
    if (!verbatim && content[index] === "\\") { index++; continue; }
    if (content[index] === delimiter) {
      if (verbatim && content[index + 1] === '"') { index++; continue; }
      return index + 1;
    }
    if (interpolated && content[index] === "{") {
      if (content[index + 1] === "{") index++;
      else index = interpolationEnd(content, index) - 1;
    }
  }
  return content.length;
}

// Start at the last opening brace; a raw interpolation closes with as many
// braces as the literal's dollar prefix. Nested expression braces still pair.
function interpolationEnd(content, start, closingWidth = 1) {
  let depth = 1;
  for (let index = start + 1; index < content.length;) {
    const end = commentEnd(content, index) ?? literalEnd(content, index);
    if (end !== undefined) { index = end; continue; }
    if (content[index] === "{") depth++;
    if (content[index] === "}" && --depth === 0) return index + closingWidth;
    index++;
  }
  return content.length;
}
