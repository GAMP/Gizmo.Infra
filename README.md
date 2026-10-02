# Gizmo.Infra

Gizmo.Infra provides centrally maintained GitHub Actions reusable workflows
for NuGet validation and publish preparation. Each consumer repository declares
its package branch policy once in a single `.github/workflows/package.yml`, sets
only a canonical `<major>.<minor>` compatibility line in its project `<Version>`,
and owns publication; workflows calculate package patches and prerelease
suffixes from caller-repository tags and route the resolved role `development`,
`production`, or `none`.

## GitHub NuGet workflows

See [docs/provider.md](docs/provider.md) for the
validation and publish preparation workflow contracts, automatic
GitHub-calculated versioning and governed compatibility-line transitions,
event/role routing, immutable full-SHA invocation, collision and release recovery
rules, and one-time existing-package bootstrap and adoption. See
[docs/caller.md](docs/caller.md)
for the single canonical caller workflow, the OIDC and `GITHUB_TOKEN`
authentication split, the internal-visibility fail-closed rule, and the
deliberate one-time existing-package adoption procedure. The documentation
describes external configuration only; this repository does not perform
publication, tagging, or any remote configuration.
