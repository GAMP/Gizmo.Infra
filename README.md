# Gizmo.Infra

Gizmo.Infra provides a centrally maintained GitHub Actions reusable workflow
for NuGet validation and publish preparation. Each consumer repository declares
its package branch policy once in a single `.github/workflows/package.yml`, sets
only a canonical `<major>.<minor>` compatibility line in its project `<Version>`,
and owns publication; the workflow calculates package patches and prerelease
suffixes from caller-repository tags and routes a resolved destination
capability `nuget`, `internal`, or `none`. The `internal` capability is not
implemented: a private repository's canonical internal job fails closed,
publishes nothing, and cannot create a release tag.

Callers reference the reusable workflow through the single maintained
`GAMP/Gizmo.Infra/.github/workflows/package.yml@stable` entrypoint by default,
or opt out to one explicit 40-character Gizmo.Infra commit SHA. The plan job
guards its own repository, path, and resolved ref identity, exports the verified
resolved `infra-sha`, and the nuget publisher and tag job check out that exact
commit into `.gizmo-infra` and run their local composite actions from the same
checkout. Tag motion therefore cannot split one run across Infra versions.
`refs/tags/stable` is the only intentionally movable Gizmo.Infra reference: it
moves only through an explicitly approved promotion of an integrated, validated
commit, recording the old and new SHA, the actor, and a readback; package-qualified
release tags remain immutable.

Consumer `-dev.*` floating references follow the historical `-dev` package
family and do not advance to `-preview.*` builds; use `-preview.*`, an exact
version, or another explicitly managed policy instead.

## GitHub NuGet workflow

See [docs/provider.md](docs/provider.md) for the plan workflow contract,
automatic GitHub-calculated versioning and governed compatibility-line
transitions, event/role routing, the trusted stable-or-SHA entrypoint and
same-run `infra-sha` provenance, collision and release recovery rules, rerun and
partial-retry limits, audit reporting, and one-time existing-package bootstrap
and adoption. See [docs/caller.md](docs/caller.md) for the single canonical
caller workflow, the OIDC-only public path, the unsupported internal-capability
fail-closed rule, and the deliberate one-time existing-package adoption
procedure. The documentation describes external
configuration only; this repository does not perform publication, tagging, or any
remote configuration.

## Dependency baseline

The approved Version 3 customer baseline preserves the actual MessagePack and
SignalR package versions resolved at its authoritative product commit, not a
universal version and not an automatic upgrade. Known vulnerabilities in those
frozen packages remain known, visible, and unresolved, with package, version,
severity, and advisory URL; they are not remediated, and this repository makes no
clean-audit claim.

NuGet audit stays enabled at `low` over direct and transitive packages, and, by
temporary explicit user decision, every vulnerability finding (`NU1901`-`NU1904`,
low to critical) is a visible non-blocking warning that is not suppressed. See
[docs/provider.md](docs/provider.md#dependency-baseline-and-audit-reporting) for
the authoritative flag set, escalation codes, and their limits.
