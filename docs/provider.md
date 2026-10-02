# GitHub NuGet provider

This is the callable-workflow contract for GitHub Actions NuGet pipelines in
Gizmo.Infra. It documents workflows and composite actions only; it does not
configure GitHub, NuGet, or a caller repository, and the reusable publish
workflow does not perform a publication or tag write. The caller-owned routing
and authentication shape is in
[caller.md](caller.md).

## Callable workflows

Gizmo.Infra exposes two direct `workflow_call` workflows. There is no mode,
descriptor version, workflow version, or equivalent version input. Both require
the caller-supplied `development-branch` and `production-branch` string inputs:
normal version-controlled branch policy, never secrets, and never a project,
package, visibility, patch, version, or registry input.

| Workflow | Purpose | Caller permissions |
| --- | --- | --- |
| `.github/workflows/package-validation.yml` | Calculate, restore, audit, build, pack, and retain a nonpublished validation package. It cannot publish or tag. | `contents: read` |
| `.github/workflows/package-publish.yml` | Resolve the caller branch role, calculate the development or stable version and tag state, build, pack, and upload the exact package artifact, then export caller-routing outputs. It never requests OIDC, never contacts a package feed, never publishes, and never creates a tag. | `contents: read` |

Every caller must use an immutable, complete 40-character Gizmo.Infra commit
SHA. A branch, tag, abbreviated SHA, or expression is not an acceptable
workflow reference.

A package repository needs exactly one package automation file,
`.github/workflows/package.yml`. It triggers on `pull_request` and `push`,
declares the two physical branch names exactly once as YAML anchors, and passes
them to both reusable workflows as the required `development-branch` and
`production-branch` inputs:

```yaml
env:
  DEVELOPMENT_BRANCH: &development_branch version-3
  PRODUCTION_BRANCH: &production_branch release

jobs:
  validate:
    permissions:
      contents: read
    uses: GAMP/Gizmo.Infra/.github/workflows/package-validation.yml@<40-character-infra-commit-sha>
    with:
      development-branch: *development_branch
      production-branch: *production_branch
```

There is no separate `.github/package.yml` descriptor and no separate caller
`package-validation.yml` or `package-publish.yml`; changing a package branch
requires editing only the single caller workflow. The branch names must be
distinct valid Git branch names. Gizmo.Infra resolves the logical role
`development`, `production`, or `none` from the event and these inputs and never
reads a caller configuration file. A pull request resolves the role from its base
branch, a push resolves the role from the effective branch, and any other ref
resolves `none`. The caller workflow has no `workflow_dispatch` trigger, so a
same-SHA recovery re-runs an existing production `push` workflow run instead of
starting a new manually dispatched run.

When the role is `development` or `production`, the workflows deterministically
discover exactly one SDK-style packable `.csproj` from the caller workspace and
read `PackageId`, `Version`, and `IsPackable` through MSBuild. Zero or multiple
candidates, non-packable or non-SDK-style projects, invalid project metadata, or
invalid branch inputs fail closed. When the role is `none`, discovery is skipped
entirely. The evaluated project `Version` remains a canonical `<major>.<minor>`
compatibility line; callers do not supply a project path, package ID, version, or
package visibility.

The preflight uses the caller `GITHUB_TOKEN` and `github.repository` to read
authenticated repository metadata with bounded connect and total request
timeouts, then emits the `repository-visibility` output. Only `public`,
`private`, and `internal` visibility values are accepted; transport failures,
timeouts, non-success responses, malformed metadata, and unknown values fail
closed. It never reads `github.event.repository.visibility`.

The reusable workflow validates its own repository and file path through the
caller-independent `job.workflow_*` contexts. It validates `job.workflow_ref` as
the expected workflow identity ending in the same complete 40-character commit
SHA reported by `job.workflow_sha`, then checks out that exact commit into
`.gizmo-infra` and runs the bundled preflight action there; it never assumes a
caller-local `./.github/actions` path belongs to Gizmo.Infra. `job.workflow_sha`
alone is a resolved commit and cannot establish that a caller used a full SHA;
the original `job.workflow_ref` check enforces that invariant.

## Preparation outputs for caller-owned routing

The publish workflow is a preparation authority. It exports `package-artifact`,
`package-version`, `release-tag`, `release-tag-state`, `calculated-state`,
`tag-state-fingerprint`, `package-id`, `branch-role`, and
`repository-visibility` from its `build` job. The caller-owned jobs consume
those outputs; the reusable workflow itself does not read its own
`repository-visibility` output to select a registry, gate a job, or branch a
step.

Discovered visibility, selected registry, and authentication are three separate
concerns:

| Discovered visibility | Selected registry | Authentication |
| --- | --- | --- |
| `public` | NuGet.org | Caller-owned OIDC (`id-token: write`), exchanged for a short-lived NuGet.org API key |
| `private` | GitHub Packages | Caller `GITHUB_TOKEN` with `packages: write` |
| `internal` | none | None; the caller must fail closed with no publication or tag work |

The reusable publish workflow only discovers and exports visibility. The caller
owns the registry decision and every credential. Internal visibility is
explicitly fail-closed: a caller must not route it to either registry.

Callers grant permissions on the calling job; a called workflow cannot elevate
them. Do not use `secrets: inherit`, pass an API key, or create a
`NUGET_API_KEY` secret. The public publisher requests only `contents: read` and
`id-token: write`; the private publisher requests only `contents: read` and
`packages: write`; the tag job requests only `contents: write`. The reusable
preparation workflow requests only `contents: read` and never requests OIDC.

## Composite actions

The reusable preparation workflow runs the bundled `preflight`
composite action for discovery. The caller-owned jobs then compose these pinned
Gizmo.Infra composite actions:

| Action | Registry | Credentials | Modes |
| --- | --- | --- | --- |
| `public` | NuGet.org | OIDC (`id-token: write`) | development and production |
| `private` | GitHub Packages | Caller `GITHUB_TOKEN` (`packages: write`) | development and production |
| `tag` | None | Caller `GITHUB_TOKEN` (`contents: write`) | production only; requires a `production` preparation `branch-role` and fails closed otherwise |

Each publisher re-downloads the exact prepared artifact, refetches the complete
package tag state under the exact `<package-id>/` prefix, rechecks the
calculated-state and tag-state fingerprint, and only then contacts its feed. A
version that already exists with the caller SHA embedded as provenance is
treated as an already-published success so the immutable release tag can be
reconciled; a version that exists without matching provenance fails closed. The
public publisher rejects any visibility other than `public`, and the private
publisher rejects any visibility other than `private`, so internal visibility
cannot be silently routed by a caller mistake.

## Automatic versioning

The evaluated project `<Version>` is a compatibility-line input and must be
exactly `<major>.<minor>` with `major >= 1` and `minor >= 0`. Each component is
a canonical decimal: `0` or a non-zero digit followed by digits, so leading
zeros are rejected. So `1.0`, `1.1`, `3.0`, and `4.12` are valid, while `0.0`,
`0.7`, `01.0`, `1.01`, and `1.0.14` are rejected. The declared major and minor
select the active compatibility line; neither is fixed. Callers supply no patch
or prerelease version: the patch and any `-dev.N`/`-pr.N` suffix are
infrastructure-owned. Project descriptor values and workflow version inputs are
not authoritative.

For each package independently, the workflow uses the caller job's
token-supported GitHub Git-refs API to paginate the complete caller-repository
tag set under the exact prefix `<package-id>/`. Each tag there must be exactly
`<package-id>/v<major>.<minor>.<patch>` with canonical numeric components;
malformed prefix tags fail closed. Validation, development, and production select
`patch=0` when the matching line has no tags, otherwise `max(patch)+1`. The
GitHub run number supplies `N`:

| Operation | Calculated package version |
| --- | --- |
| Validation | `<major>.<minor>.<patch>-pr.${{ github.run_number }}` (packed only) |
| Development | `<major>.<minor>.<patch>-dev.${{ github.run_number }}` |
| Production | `<major>.<minor>.<patch>` and `<package-id>/v<major>.<minor>.<patch>` |

Validation and development calculate the next patch from the complete stable
tag state: `0` when the matching compatibility line has no stable tag, otherwise
numeric `max(patch)+1`. Only tags whose `<major>.<minor>` equals the evaluated
compatibility line contribute to the patch calculation. Development does not
create a stable tag, so repeated development runs reuse that same next-production
base until a production run creates its stable tag. A production run first
resolves every matching line tag to its commit. A rerun reuses a base only if
exactly one package/line tag resolves to the caller SHA. No current-SHA tag
calculates the next base. Multiple current-SHA tags are ambiguous and fail
closed. A claimed calculated tag on another commit, a malformed tag response, or
a changed tag state is a failure; the workflow never moves or overwrites a tag.

### Governed compatibility-line transitions

Once package-qualified stable tags establish a governed compatibility line, the
project may declare only a controlled transition from the numerically highest
governed line `X.Y` represented by valid package-qualified stable tags for the
package:

- the same line `X.Y` — normal patch continuation, where the patch stays
  `max(patch)+1`;
- the next minor line `X.(Y+1)` — the first patch on the new line starts at `0`;
- the next major line `(X+1).0` — the major advances by one, the minor resets to
  `0`, and the first patch starts at `0`.

Every other transition fails closed, including skipping a line, moving backward,
or advancing a major by more than one. For example, from governed line `1.0`,
`1.0`, `1.1`, and `2.0` are accepted, while `1.2`, `2.1`, and `3.0` fail closed;
from `1.7`, `1.8` and `2.0` are accepted, while `1.9` fails closed. The
comparison authority is the package-qualified stable tag history for the package,
not a previous project-file value in Git history. When no governed tag history
exists yet, the declared canonical line may bootstrap, subject to the existing
registry-inspection and adoption prerequisite; runtime tag logic never pretends
that an unadopted registry history is safe.

The publish workflow uses the preflight action as the only authority for branch
role and never parses a caller configuration file. Its build job emits package
version, complete calculated state, and a tag-state fingerprint only for
`development` or `production`, plus the caller-routing outputs. When the preflight
resolves `none`, the version calculation, restore, build, pack, and upload are
skipped and the caller performs no publication or tag work.

The canonical single caller workflow owns the only non-cancelling
caller-repository concurrency group `nuget-${{ github.repository }}` around
validation, preparation, publication, and tagging, so development and production
do not overlap unbounded. Both reusable workflows declare no concurrency of their
own: GitHub evaluates a called workflow against the caller's lock, and a nested
declaration of the same group can deadlock the run against itself. GitHub does
not guarantee FIFO: the latest pending run may replace an earlier pending run,
so this is not a durable queue.

## One-time existing-package bootstrap and adoption

The workflow derives every version from tags. For each package it considers only
the complete tag set under the exact `<package-id>/` prefix and only the matching
compatibility line; tags for another package or another compatibility line never
advance the candidate. It never queries a registry, and the publisher rechecks
only the calculated package version. The runtime can therefore refuse an existing
package at that exact calculated version, but it cannot discover any other
published version, including a higher stable one.

Enabling steady state for an active compatibility line is gated on a migration
operator inspecting all stable registry versions first, before steady state is
enabled:

- **Natural bootstrap** — the active compatibility line has no stable package in
  the selected registry *and* no matching stable tag. Validation and development
  advertise the first `<major>.<minor>.0`, and a production run calculates
  `<major>.<minor>.0` with `release-tag-state=missing`. The preparation workflow
  never creates a synthetic tag; the immutable release tag is created only by the
  caller-owned `tag` action, in a resolved `production` run, after
  the selected publisher succeeded, and only for the exact calculated release tag.
  This is the only case where steady state is safe without a migration step.
- **Migration required** — the active compatibility line already has any stable
  package in the selected registry. Do not enable or run steady state until the
  migration procedure in
  [caller.md](caller.md) completes.
  With no line tags the tag-derived calculation is `<major>.<minor>.0`, and the
  publisher sees no package at `<major>.<minor>.0`, so a steady-state run would
  publish a new lower `<major>.<minor>.0` rather than detect the higher packages.
  Migration adopts the highest stable `<major>.<minor>.<patch>` published for the
  line, proves its `RepositoryCommit` is a real caller commit, deliberately
  creates `<package-id>/v<major>.<minor>.<patch>`, and lets the next production
  run claim `patch+1`. Adoption never selects the first `<major>.<minor>.0` when
  higher stable line versions already exist.
- **Migration stays disabled** — when the highest stable line version cannot be
  provenance-proven, do not enable steady state, do not publish, and do not
  create a tag. The runtime cannot detect the unadopted higher version.

A stable package and its `<package-id>/v<major>.<minor>.<patch>` tag must stay
consistent. When the matching stable tag already exists, the next production run
claims `patch+1`, and a same-commit rerun reuses the exact tagged version. A tag
is never moved or overwritten, and a claimed tag that resolves to a different
commit is a failure.

The steady-state workflow does not adopt an existing package. The publisher's
provenance recheck accepts an already-published *calculated* version only when
the package carries the exact caller commit in its `RepositoryCommit` metadata;
same-SHA recovery is the only automatic success. A stable package at the
calculated version with absent, malformed, or different provenance fails closed,
and the workflow neither publishes nor creates a recovery tag. Adopting a stable
package whose matching tag is missing into the compatibility line requires
conclusive `RepositoryCommit` provenance tied to a real caller commit in the
caller repository before the tag is created.

Runtime and migration fail closed separately. The runtime aborts on malformed,
conflicting, or ambiguous tag state, on calculated-state or tag-state drift, and
on an existing calculated version without matching provenance; that protects the
calculated version and the governed tags only. The operator must keep steady
state disabled whenever the registry shows a stable line package that cannot be
adopted, because the runtime never observes those higher versions. The workflow
carries no legacy bootstrap logic: the only bootstrap behavior is the generic
next-patch derivation from the complete line tag state, and no workflow or action
carries registry migration or adoption code.

### Gizmo.Shared 1.0 migration and adoption

`Gizmo.Shared` is on compatibility line 1.0, and the stable `Gizmo.Shared 1.0.13`
package already exists in the selected registry, so it is not a natural bootstrap
and needs migration: keep steady state disabled until the existing package is
adopted. Prove that `Gizmo.Shared 1.0.13` embeds a real caller commit as its
`RepositoryCommit` and deliberately create the immutable `Gizmo.Shared/v1.0.13`
tag. Steady state then derives the next patch: the next development build is
`1.0.14-dev.N`, the next stable release calculates `1.0.14`, and the caller-owned
tag action creates the immutable `Gizmo.Shared/v1.0.14` tag.

The evaluated project `<Version>` remains the compatibility line only and the
pilot must ultimately use `<Version>1.0</Version>`; the `1.0.14` patch is
infrastructure-owned and is never encoded in the project `Version`.

## Artifacts, collision checks, and release recovery

The build job packs the calculated version with the caller commit as repository
metadata and uploads only its exact `.nupkg` path. Artifact names include both
`github.run_id` and `github.run_attempt`, so a rerun can download the exact
artifact that its preparation produced. The caller-owned publisher actions
download that artifact by name and never rebuild it; they recheck collision and
provenance immediately before publishing. Same-SHA recovery re-runs an existing
production `push` workflow run rather than a new manually dispatched run: the
re-run keeps the pushed caller commit and immutable tag state, so the publisher
treats the matching package as already published and the tag job reconciles the
release tag.

All action references are pinned to full commit SHAs, checkout credentials are
disabled, and caller-supplied strings enter shell commands only through quoted
environment variables.

## Consumer development ranges

Consumer Central Package Management may explicitly opt into the floating
development range `<major>.<minor>.*-dev.*` when it intentionally tracks the latest
development build for one compatibility line. Exact development versions remain
the safer default. This is consumer documentation only: Gizmo.Infra does not
migrate consumers or enable CPM floating-version behavior.

## Public NuGet trusted publishing

Public publication requires the caller to configure NuGet.org Trusted
Publishing to trust the caller repository's own single publishing workflow file,
because the OIDC-requesting job is a caller-owned normal job. Bind the exact
caller owner and repository and the caller `package.yml` workflow file, without
wildcards, plus an optional GitHub environment or package scopes if the caller
uses them. The same caller policy covers both development and production
publication.

Do not bind the Gizmo.Infra commit SHA in the NuGet.org policy. Pinning every
Gizmo.Infra workflow and action reference to one immutable 40-character commit
SHA is a separate GitHub supply-chain invariant. The repository or organization
variable `NUGET_USER` is a NuGet.org profile identifier, not a secret or API key.
