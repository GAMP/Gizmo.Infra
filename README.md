# Gizmo.Infra

Gizmo.Infra provides centrally maintained GitHub Actions reusable workflows
for NuGet validation and publish preparation. Callers own the package identity
and set only a canonical `<major>.<minor>` compatibility line in their project
`<Version>`;
workflows calculate package patches from caller-repository tags, and callers
route publication from the preparation outputs.

## GitHub NuGet workflows

See [docs/GITHUB_NUGET_PROVIDER.md](docs/GITHUB_NUGET_PROVIDER.md) for the
validation and canonical publish workflow contracts, automatic
GitHub-calculated versioning, caller branch-role and visibility routing,
immutable full-SHA invocation, collision and release recovery rules, and
one-time existing-package bootstrap and adoption. See
[docs/CALLER_OWNED_NUGET_PUBLISHING.md](docs/CALLER_OWNED_NUGET_PUBLISHING.md)
for the canonical caller shape, the OIDC and `GITHUB_TOKEN` authentication
split, the internal-visibility fail-closed rule, and the deliberate one-time
existing-package adoption procedure. The documentation
describes external configuration only; this repository does not perform
publication, tagging, or any remote configuration.
