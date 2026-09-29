// Userinfo-bearing PackageBaseAddress candidates are built in memory here so the
// credential-shaped URL never reaches a process command line; the C# test selects a
// named case instead. The runner prints "rejected"/"accepted:<href>" and reserves a
// non-zero exit for a broken fixture, so a load failure cannot masquerade as a rejection.

import { validatePackageBaseAddress } from "../../../.github/actions/package-private-publish/scripts/validate-package-base-address.mjs";

const cases = new Map([
  ["user-and-password", { username: "user", password: "pass" }],
  ["username-only", { username: "user", password: "" }],
  ["password-only", { username: "", password: "pass" }],
]);

const selected = cases.get(process.argv[2]);
if (selected === undefined) {
  process.stderr.write(`unknown credential case '${process.argv[2]}'\n`);
  process.exit(2);
}

const url = new URL("https://nuget.pkg.github.com/owner/download");
url.username = selected.username;
url.password = selected.password;

const href = validatePackageBaseAddress(url.href);
process.stdout.write(href === null ? "rejected\n" : `accepted:${href}\n`);
