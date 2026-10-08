# Gizmo.Infra

Gizmo.Infra provides a centrally maintained GitHub Actions reusable workflow
for NuGet validation and publish preparation. Each consumer repository declares
its package branch policy once in a single `.github/workflows/package.yml`, sets
only a canonical `<major>.<minor>` compatibility line in its project `<Version>`,
and owns publication; the workflow calculates package patches and prerelease
suffixes from caller-repository tags and routes a resolved destination
capability `nuget`, `internal`, or `none`.

## GitHub NuGet workflow

See [docs/provider.md](docs/provider.md) for the plan workflow contract,
automatic GitHub-calculated versioning and governed compatibility-line
transitions, event/role routing, immutable full-SHA invocation, collision and
release recovery rules, and one-time existing-package bootstrap and adoption.
See [docs/caller.md](docs/caller.md) for the single canonical caller workflow,
the OIDC and `GITHUB_TOKEN` authentication split, the unsupported-visibility
fail-closed rule, and the deliberate one-time existing-package adoption
procedure. The documentation describes external configuration only; this
repository does not perform publication, tagging, or any remote configuration.
