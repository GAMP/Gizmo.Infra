# Gizmo.Infra

Gizmo.Infra provides centrally maintained GitHub Actions reusable workflows
for NuGet validation and publishing. Callers own the package identity and set
only the `3.X` compatibility line in their project `<Version>`; workflows
calculate package patches from caller-repository tags.

## GitHub NuGet workflows

See [docs/GITHUB_NUGET_PROVIDER.md](docs/GITHUB_NUGET_PROVIDER.md) for the
three callable workflow contracts, automatic GitHub-calculated versioning,
required inputs and permissions, immutable full-SHA invocation, OIDC
trusted-publishing requirements, and recovery rules. The documentation
describes external configuration only; this repository does not perform
publication, tagging, or any remote configuration.
