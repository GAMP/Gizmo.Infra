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

A package repository needs exactly one package automation file:

```text
.github/
  workflows/
    package.yml
```

The versioned, tested template is
[`.github/templates/package.yml`](../.github/templates/package.yml). Copy it to
the caller repository as `.github/workflows/package.yml` and replace every
`<40-character-infra-commit-sha>` placeholder with the same immutable, complete
40-character Gizmo.Infra commit SHA. Do not rename the file: the caller workflow
file is part of the NuGet.org Trusted Publishing binding.

The single workflow triggers on `pull_request`, `push`, and `workflow_dispatch`.
It declares the two physical branch names exactly once as YAML anchors and passes
both to Gizmo.Infra as the required reusable `development-branch` and
`production-branch` inputs:

```yaml
env:
  DEVELOPMENT_BRANCH: &development_branch pre-release
  PRODUCTION_BRANCH: &production_branch release

jobs:
  validate:
    uses: GAMP/Gizmo.Infra/.github/workflows/package-validation.yml@<40-character-infra-commit-sha>
    with:
      development-branch: *development_branch
      production-branch: *production_branch
```

There is no separate `.github/package.yml` descriptor and no separate caller
`package-validation.yml` or `package-publish.yml`. The branch names live only in
this one file, so changing a package branch requires editing only the single
caller workflow, and the caller never duplicates those names in trigger filters.

Pinning every Gizmo.Infra workflow and composite-action reference to one
immutable 40-character commit SHA is a GitHub supply-chain invariant. It is
enforced by the caller template and is separate from the NuGet.org policy
described below.

The caller is the only owner of the non-cancelling caller-repository concurrency
group `nuget-${{ github.repository }}` around validation, preparation,
publication, and tagging. The reusable workflows declare no concurrency of their
own, so one run is never evaluated against the same lock twice.

### Event and role contract

Gizmo.Infra resolves the logical role from the event plus the two declared branch
inputs:

- `pull_request` — compare the pull-request base branch. Base equal to the
  development branch resolves `development`, base equal to the production branch
  resolves `production`, and any other base resolves `none`. A pull request only
  validates; it never publishes and never tags.
- `push` or `workflow_dispatch` — compare the effective branch. Development
  resolves `development`, production resolves `production`, and any other branch
  resolves `none`.

The logical roles are exactly `development`, `production`, and `none`. No
logical `release` role remains. `none` is a cheap successful no-op: validation
and preparation resolve the role and stop, so an unrelated branch performs no
project discovery, tag lookup, build, pack, publish, or tag work.

### One-file jobs

The canonical template contains:

- `validate` — pull requests only. It calls the reusable validation workflow with
  `contents: read`, packs the calculated `-pr.N` validation version, and can never
  publish or tag.
- `prepare` — push and dispatch only. It calls the reusable publish preparation
  workflow with `contents: read` and exports the routing outputs.
- `publish-public` — caller-owned, `contents: read` plus `id-token: write`, and
  only for `repository-visibility == public`.
- `publish-private` — caller-owned, `contents: read` plus `packages: write`, and
  only for `repository-visibility == private`.
- `reject-unsupported-visibility` — fails closed for any other visibility
  (including `internal`) with no publisher and no tag.
- `tag` — caller-owned, `contents: write`, and only for a `production` run whose
  selected publisher succeeded. It reconciles the immutable package-qualified tag
  through `package-release-tag`, which fails closed unless the role is exactly
  `production`.

## Routing and authentication

The caller must condition the public and private jobs mutually exclusively from
the preparation output and branch role:

- `branch-role == none` runs no publication and no tag work, and a pull request
  never publishes or tags regardless of role. Everything else on a
  non-publishing branch is skipped by the preparation workflow itself.
- `repository-visibility == public` runs only the public job: `contents: read`
  plus `id-token: write`, and it must never be granted `packages: write`.
- `repository-visibility == private` runs only the private job: `contents: read`
  plus `packages: write`, and it must never be granted `id-token`.
- Any other visibility (including `internal`) runs only
  `reject-unsupported-visibility`, which fails closed, and runs no publisher and
  no tag.

The tag job runs only when the preparation resolved `production` and the
selected publisher succeeded, so a development run never tags. It also passes
the prepared `branch-role` to `package-release-tag`, whose own validation fails
closed unless that value is exactly `production`; a caller wiring mistake cannot
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
complete tag set under `<package-id>/`; it never queries a registry, and the
publisher rechecks only the calculated package version. Runtime fail-closed
therefore covers exactly the calculated version and the governed tags: it can
refuse an existing package at that calculated version without matching caller
provenance, and it aborts on malformed, foreign, ambiguous, or drifted state, but
it cannot discover any other published version, including a higher stable one.

Enabling steady state for an active compatibility line is therefore gated on a
migration operator inspecting every stable registry version for the exact package
ID before steady state is enabled. The runtime never performs that inspection, so
the operator must, and must do it first.

### Enable steady state only after registry inspection

1. **Inspect the registry before enabling steady state.** List every stable
   package version for the exact package ID in the selected registry, then keep
   only versions on the active compatibility line. Versions for another
   compatibility line are excluded and never raise or lower the conclusion.
2. **Natural bootstrap — no stable line package and no matching line tag.** Only
   when the active compatibility line has no stable package in the selected
   registry *and* no tag under `<package-id>/` for that line is steady state safe
   from the start: the workflow derives the first `<major>.<minor>.0`, the
   selected publisher publishes it, and the caller-owned `package-release-tag`
   action creates `<package-id>/v<major>.<minor>.0`. No manual step is required.
3. **Any stable line package — keep steady state disabled until migration.** If
   the active line has one or more stable packages in the selected registry, do
   not enable or run steady state yet. A tag-derived calculation can sit below
   the published packages and the runtime will not catch it: for example, with
   registry versions `<major>.<minor>.4` and `<major>.<minor>.5` and no line tags,
   the workflow calculates `<major>.<minor>.0`, and the publisher sees no package
   at `<major>.<minor>.0` and would publish a new lower `<major>.<minor>.0`
   instead of failing closed. Complete the migration procedure below before
   enabling steady state.
4. **Adopt the highest proven version, then advance one patch.** The migration
   candidate is the highest stable `<major>.<minor>.<patch>` already published for
   the line: for example, registry versions `<major>.<minor>.4` and
   `<major>.<minor>.5` yield candidate `<major>.<minor>.5`, never the first
   `<major>.<minor>.0`. Prove the candidate's provenance, create the immutable
   `<package-id>/v<major>.<minor>.<patch>` tag deliberately, and only then enable
   steady state: the next release claims `patch+1`, so a candidate of
   `<major>.<minor>.5` resumes at `<major>.<minor>.6`.
5. **Unprovable highest — migration stays disabled.** When the highest stable
   `<major>.<minor>.<patch>` on the line has missing, malformed, or foreign
   provenance, migration remains disabled: do not enable or run steady state, do
   not create a tag, and do not publish. The runtime provides no safety net for
   the unadopted higher version.

### Prove the migration candidate's provenance

Download that exact published candidate package and read the `RepositoryCommit`
value from its `.nuspec` metadata. The value must be a well-formed 40-character
commit SHA. Show that commit is a real commit in the caller repository, for
example with the Git-refs or commits API or `git cat-file -e <sha>^{commit}`, and
that it is the commit that produced the published package. Missing, malformed, or
foreign provenance fails migration.

A tag/package version conflict, or a valid lower candidate that sits below an
unproven higher line version, fails migration and adopts nothing: never tag a
valid lower version while a higher line version exists whose provenance is
missing, malformed, or foreign, and never reconcile a tag and a package that
disagree about the same version.

### Runtime fail-closed versus migration fail-closed

- **Runtime fail-closed** — the workflow and publishers abort on malformed,
  foreign, or ambiguous tag state, on calculated-state or tag-state drift, and on
  an existing package at the calculated version whose provenance is not the
  caller commit. This guards the calculated version and the governed tags only.
- **Migration fail-closed** — the operator keeps steady state disabled whenever
  the registry shows a stable line package that cannot be adopted. The runtime
  never sees those higher versions, so this is an operator obligation and not a
  workflow guarantee.

### Gizmo.Shared 1.0 migration and adoption

`Gizmo.Shared` is on compatibility line 1.0, and the stable `Gizmo.Shared 1.0.13`
package already exists in the selected registry, so the line is not a natural
bootstrap: keep steady state disabled until the migration procedure above adopts
that package. Prove the published `1.0.13` embeds a valid 40-character
`RepositoryCommit` that is a real commit in the caller repository and produced
that package, then deliberately create the immutable `Gizmo.Shared/v1.0.13` tag.
Steady state then resumes at the next patch: the next development build is
`1.0.14-dev.N`, the next stable release publishes `1.0.14`, and the caller-owned
`package-release-tag` action creates the immutable `Gizmo.Shared/v1.0.14` tag.

The evaluated project `<Version>` stays the compatibility line only: the pilot
must ultimately use `<Version>1.0</Version>`. The `1.0.14` patch remains
infrastructure-owned and is never encoded in the project `Version`.

An existing package is never adopted as a side effect of a run. Adoption is a
separate, deliberate, one-time caller action, outside the workflow; it is not
part of the reusable workflow, the caller template, or either publisher action.
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
- **Workflow file** — exactly the caller `package.yml`. That single caller
  workflow defines the OIDC-requesting job, so it is the trusted identity; the
  same policy covers both development and production publication.
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
