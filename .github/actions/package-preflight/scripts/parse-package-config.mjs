// The preflight branch-configuration parser is a checked-in named module rather than
// an inline interpreter program: inline programs are opaque to review and to static
// scanning, and the constrained YAML subset still has to fail closed on any surprise.
// The config path is the first module argument, mirroring the previous inline call.

import { readFileSync } from "node:fs";

const configPath = process.argv[2];
const lines = readFileSync(configPath, "utf8").split(/\r?\n/);
const branches = new Map();
let foundRoot = false;
for (const line of lines) {
  if (line === "" || /^\s*#/.test(line)) continue;
  if (line === "branches:") {
    if (foundRoot) process.exit(1);
    foundRoot = true;
    continue;
  }
  const match = /^  (development|release): ([^\s].*)$/.exec(line);
  if (!foundRoot || !match || /\s$/.test(match[2]) || branches.has(match[1])) process.exit(1);
  let value = match[2];
  if (value.startsWith("'")) {
    if (!value.endsWith("'") || value.length < 2) process.exit(1);
    value = value.slice(1, -1).replace(/''/g, "'");
  } else if (value.startsWith("\"")) {
    if (!value.endsWith("\"") || value.length < 2) process.exit(1);
    try { value = JSON.parse(value); } catch { process.exit(1); }
    if (typeof value !== "string") process.exit(1);
  } else if (/^(?:~|null|true|false|[-+]?[0-9][0-9_]*(?:\.[0-9_]*)?(?:e[-+]?[0-9]+)?)$/i.test(value) || /^[!&*|>@[{]/.test(value)) {
    process.exit(1);
  }
  if (value === "") process.exit(1);
  branches.set(match[1], value);
}
if (!foundRoot || branches.size !== 2 || !branches.has("development") || !branches.has("release")) process.exit(1);
process.stdout.write(JSON.stringify(Object.fromEntries(branches)));
