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

## One-time existing-package bootstrap and adoption

Steady-state publishing is tag-derived. The reusable workflow reads only the
complete tag set under `<package-id>/`; it never queries a registry, so it cannot
tell whether a matching stable package already exists. Natural bootstrap and
legacy adoption are therefore different situations and must not be conflated:

- **Natural bootstrap** — the active compatibility line has no stable package in
  the selected registry *and* no tag under `<package-id>/` for that line. This is
  the first-ever release of the line: the workflow derives the first `3.X.0`, the
  selected publisher publishes it, and the caller-owned `package-release-tag`
  action creates `<package-id>/v3.X.0`. No manual step is required.
- **Legacy adoption** — the active compatibility line already has one or more
  stable package versions in the selected registry, but the tag history under
  `<package-id>/` is missing or incomplete for those versions. The workflow still
  calculates a patch from the incomplete tag set and then fails closed on the
  existing-package collision, because the existing package does not carry the
  caller commit as `RepositoryCommit`. This case requires the deliberate
  one-time operator procedure below, and the adopted version is the highest
  stable `3.X.Y` already published for the line, never the first `3.X.0`.

An existing package is never adopted as a side effect of a run. Adoption is a
separate, deliberate, one-time caller action, outside the workflow; it is not part
of the reusable workflow, the caller template, or either publisher action.

1. **Select the candidate.** List every stable package version for the exact
   package ID in the target registry, then keep only versions on the active
   compatibility line. Versions for another compatibility line are excluded and
   never raise or lower the candidate. The candidate is the highest stable
   `3.X.Y` on the line: registry versions `3.X.2`, `3.X.4`, and `3.X.5` for the
   line yield candidate `3.X.5`. Never select an unpublished, prerelease, or
   out-of-line version.
2. **Prove the candidate's provenance.** Download that exact published package
   and read the `RepositoryCommit` value from its `.nuspec` metadata. The value
   must be a well-formed 40-character commit SHA.
3. **Prove the commit is real caller provenance.** Show that commit is a real
   commit in the caller repository, for example with the Git-refs or commits API
   or `git cat-file -e <sha>^{commit}`, and that it is the commit that produced the
   published package. Missing, malformed, or foreign provenance fails closed: do
   not adopt a package whose provenance is absent, malformed, or not a caller
   commit.
4. **Refuse conflicting or partially proven state.** A tag/package conflict, or a
   valid lower candidate that sits below an unproven higher line version, fails
   closed and adopts nothing: never tag a valid lower version while a higher line
   version exists whose provenance is missing, malformed, or foreign, and never
   reconcile a tag and a package that disagree about the same version.
5. **Create the tag deliberately.** Create the immutable package-qualified tag
   `<package-id>/v3.X.Y` for the selected candidate, pointing at that exact
   commit, outside the workflow. This is the one-time adoption. Never move or
   overwrite an existing tag.
6. **Resume steady state.** Re-run the normal caller workflow. The line now has a
   matching stable tag, so the next release claims `Y+1`; a candidate of `3.X.5`
   resumes at the next stable `3.X.6`, and steady-state publishing and tagging
   resume.

Any ambiguous, conflicting, or unproven state fails closed: do not guess a tag
target, do not create a synthetic tag, and do not route the package through a
publisher to force adoption. The runtime workflow and actions carry no registry
migration or adoption code: this remains an operator contract.

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
