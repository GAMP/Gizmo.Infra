// Userinfo-bearing PackageBaseAddress candidates are built in memory here so the
// credential-shaped URL never reaches a process command line; the C# test selects a
// named case by writing it to fd 0. The runner prints "rejected"/"accepted:<href>"
// and reserves a non-zero exit for a broken fixture, so a load failure cannot
// masquerade as a rejection.

import { readFileSync } from "node:fs";
import { validatePackageBaseAddress } from "../../../.github/actions/package-private-publish/scripts/validate-package-base-address.mjs";

const cases = new Map([
  ["user-and-password", { username: "user", password: "pass" }],
  ["username-only", { username: "user", password: "" }],
  ["password-only", { username: "", password: "pass" }],
]);

const caseId = readFileSync(0, "utf8");
const selected = cases.get(caseId);
if (selected === undefined) {
  process.stderr.write(`unknown credential case '${caseId}'\n`);
  process.exit(2);
}

const url = new URL("https://nuget.pkg.github.com/owner/download");
url.username = selected.username;
url.password = selected.password;

const href = validatePackageBaseAddress(url.href);
process.stdout.write(href === null ? "rejected\n" : `accepted:${href}\n`);
