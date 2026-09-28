# Caller-owned NuGet publishing

This is the canonical caller contract for publishing a NuGet package through
Gizmo.Infra. The deterministic discovery, versioning, artifact, collision, and
tag logic stays in `Gizmo.Infra`, while the GitHub job identity that owns the
publication credential is a normal job in the caller repository.

## Why the publish job is caller-owned

A reusable `workflow_call` job runs with the called workflow's identity, so it
cannot present caller-owned OIDC claims to NuGet.org. The reusable
`.github/workflows/package-publish.yml` is therefore a non-mutating preparation
authority: it discovers the package, resolves the branch role and visibility,
calculates the version and tag state, builds and packs the exact artifact, and
exports routing outputs. It never requests OIDC, contacts a package feed,
publishes, or creates a tag.

The caller then routes to exactly one publisher using the preparation outputs
and runs the publisher as a caller-owned normal job with the credential that
publisher needs.

## Canonical caller shape

The versioned, tested caller workflow is
[`.github/templates/package-publish.yml`](../.github/templates/package-publish.yml).
Copy it to the caller repository as `.github/workflows/package-publish.yml` and
replace every `<40-character-infra-commit-sha>` placeholder with the same
immutable, complete 40-character Gizmo.Infra commit SHA. Do not rename the file:
the caller workflow name is also the NuGet.org Trusted Publishing binding.

Pinning every Gizmo.Infra workflow and composite-action reference to one
immutable 40-character commit SHA is a GitHub supply-chain invariant. It is
enforced by the caller template and is separate from the NuGet.org policy
described below.

Keep the caller workflow on a protected branch and trigger it only from `push`
or `workflow_dispatch`; every publisher and the tag action repeats the
protected-branch and event check and fails closed. Do not name the development or
release branches in the trigger: the caller's `.github/package.yml` configuration
and the preflight `branch-role` output are the only branch-role authority, and
any other ref resolves to `none` and publishes nothing.

The caller template owns the single non-cancelling caller-repository concurrency
group `nuget-${{ github.repository }}` around preparation, publication, and
tagging. The reusable preparation workflow declares no concurrency of its own, so
one run is never evaluated against the same lock twice.

## Routing and authentication

The caller must condition the public and private jobs mutually exclusively from
the preparation output and branch role:

- `branch-role == none` runs no publication and no tag work. Everything else on
  a non-publishing branch is skipped by the preparation workflow itself.
- `repository-visibility == public` runs only the public job: `contents: read`
  plus `id-token: write`, and it must never be granted `packages: write`.
- `repository-visibility == private` runs only the private job: `contents: read`
  plus `packages: write`, and it must never be granted `id-token`.
- Any other visibility (including `internal`) runs only
  `reject-unsupported-visibility`, which fails closed, and runs no publisher and
  no tag.

The tag job runs only when the preparation resolved `release` and the selected
publisher succeeded, so a development run never tags. It also passes the
prepared `branch-role` to `package-release-tag`, whose own validation fails
closed unless that value is exactly `release`; a caller wiring mistake cannot
produce a tag from a development or unresolved run.

The visibility used for routing is only
`needs.prepare.outputs.repository-visibility`, the authenticated value from the
preflight. Do not use `github.event.repository.visibility`, a repository
variable, a workflow input, or any independent visibility query. The publisher
composites also assert their expected visibility, so a caller wiring mistake
fails closed instead of silently publishing to the wrong feed.

## Collision, provenance, and same-SHA recovery

The publisher composites download the exact prepared artifact, refetch the
complete tag state under the package prefix, compare the calculated-state and
fingerprint, and then query their feed. A calculated version that already
exists is inspected: if the published package embeds the caller commit as
`RepositoryCommit`, the publisher treats it as already published, skips the
push, and succeeds so the tag job can reconcile the immutable release tag. A
version that exists without matching provenance fails closed. This preserves
same-SHA rerun recovery without a permanent key and without moving a tag.

## NuGet.org trusted publishing

Public publishing uses NuGet.org Trusted Publishing instead of a stored API key.
The OIDC-requesting job is the caller-owned normal job, so the NuGet.org policy
binds the caller identity and the caller workflow file, never Gizmo.Infra:

- **Repository owner and repository** — the exact GitHub owner and repository
  that publishes, without wildcards, for example `GAMP` and `Gizmo.Widget`.
- **Workflow file** — exactly the caller `package-publish.yml`. That caller
  workflow defines the OIDC-requesting job, so it is the trusted identity; the
  same policy covers both development and stable publication.
- **Environment and scopes** — set only when the caller deliberately deploys the
  job through a GitHub environment or narrows package scopes; otherwise leave
  them unset.

Do not bind the Gizmo.Infra SHA in the NuGet.org policy. The immutable
Gizmo.Infra commit SHA pinned on every Infra workflow and action reference is a
separate GitHub supply-chain invariant and must not appear in the Trusted
Publishing binding.

The repository or organization variable `NUGET_USER` remains a NuGet.org profile
identifier, not a secret or API key.

Do not use `secrets: inherit`, pass a permanent API key, or create a
`NUGET_API_KEY` secret. The public path obtains a short-lived API key through
OIDC and masks it before use.
