// The internal publisher keeps this validator in a named file instead of an inline
// interpreter argument: a credential-shaped URL on an interpreter command line is
// the process pattern endpoint security flags, and parsing still has to fail closed.
// The untrusted candidate arrives on stdin, so it never appears in argv at all.

import { readFileSync, realpathSync } from "node:fs";
import { fileURLToPath } from "node:url";

const TRUSTED_HOSTNAME = "nuget.pkg.github.com";

/**
 * Returns the canonical href for a trusted PackageBaseAddress @id, or null when the
 * candidate is not exactly the HTTPS trusted origin with no port, credentials,
 * query, fragment, or WHATWG rewriting.
 */
export function validatePackageBaseAddress(candidate) {
  if (typeof candidate !== "string" || candidate.includes("?") || candidate.includes("#")) {
    return null;
  }

  let parsed;
  try {
    parsed = new URL(candidate);
  } catch {
    return null;
  }

  if (parsed.protocol !== "https:") return null;
  if (parsed.hostname !== TRUSTED_HOSTNAME) return null;
  if (parsed.port !== "") return null;
  if (parsed.username !== "" || parsed.password !== "") return null;
  if (parsed.search !== "" || parsed.hash !== "") return null;
  if (parsed.href !== candidate) return null;
  return parsed.href;
}

function main() {
  // Read the exact candidate bytes from fd 0. The caller sends them with no trailing
  // newline; any trailing byte would break the parsed.href === candidate equality.
  const candidate = readFileSync(0, "utf8");
  const href = validatePackageBaseAddress(candidate);
  if (href === null) {
    process.exitCode = 1;
    return;
  }
  process.stdout.write(href);
}

// realpath on both sides so a Windows path invoked with different case still matches.
function isDirectInvocation() {
  if (process.argv[1] === undefined) {
    return false;
  }
  try {
    return realpathSync(process.argv[1]) === realpathSync(fileURLToPath(import.meta.url));
  } catch {
    return false;
  }
}

if (isDirectInvocation()) {
  main();
}
