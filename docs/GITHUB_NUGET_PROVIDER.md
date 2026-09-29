# GitHub NuGet provider

This is the callable-workflow contract for GitHub Actions NuGet pipelines in
Gizmo.Infra. It documents workflows and composite actions only; it does not
configure GitHub, NuGet, or a caller repository, and the reusable publish
workflow does not perform a publication or tag write. The caller-owned routing
and authentication shape is in
[CALLER_OWNED_NUGET_PUBLISHING.md](CALLER_OWNED_NUGET_PUBLISHING.md).

## Callable workflows

Gizmo.Infra exposes two direct `workflow_call` workflows. There is no mode,
descriptor version, workflow version, or equivalent version input.

| Workflow | Purpose | Caller permissions |
| --- | --- | --- |
| `.github/workflows/package-validation.yml` | Calculate, restore, audit, build, pack, and retain a nonpublished validation package. It cannot publish or tag. | `contents: read` |
| `.github/workflows/package-publish.yml` | Resolve the caller branch role, calculate the development or stable version and tag state, build, pack, and upload the exact package artifact, then export caller-routing outputs. It never requests OIDC, never contacts a package feed, never publishes, and never creates a tag. | `contents: read` |

Every caller must use an immutable, complete 40-character Gizmo.Infra commit
SHA. A branch, tag, abbreviated SHA, or expression is not an acceptable
workflow reference. For example:

```yaml
permissions:
  contents: read

jobs:
  validate-package:
    uses: GAMP/Gizmo.Infra/.github/workflows/package-validation.yml@<40-character-infra-commit-sha>
```

Every caller contains `.github/package.yml` with exactly its branch deployment
configuration:

```yaml
branches:
  development: version-3
  release: release
```

The branch names must be distinct valid Git branch names. The preflight resolves
the caller ref to `development`, `release`, or `none`; non-branch refs and
branches not named by this file resolve to `none`.

The workflows deterministically discover exactly one SDK-style packable
`.csproj` from the caller workspace and read `PackageId`, `Version`, and
`IsPackable` through MSBuild. Zero or multiple candidates, non-packable or
non-SDK-style projects, invalid project metadata, or invalid package
configuration fail closed. The evaluated project `Version` remains the `3.X`
compatibility line; callers do not supply a project path, package ID, version,
or package visibility.

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

The reusable preparation workflow runs the bundled `package-preflight`
composite action for discovery. The caller-owned jobs then compose these pinned
Gizmo.Infra composite actions:

| Action | Registry | Credentials | Modes |
| --- | --- | --- | --- |
| `package-public-publish` | NuGet.org | OIDC (`id-token: write`) | development and release |
| `package-private-publish` | GitHub Packages | Caller `GITHUB_TOKEN` (`packages: write`) | development and release |
| `package-release-tag` | None | Caller `GITHUB_TOKEN` (`contents: write`) | release only; requires a `release` preparation `branch-role` and fails closed otherwise |

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
exactly `3.X`: generation is fixed at `3`, and `X` is a non-negative decimal
integer with no leading zero except `0`. Callers supply no patch or prerelease
version. Project descriptor values and workflow version inputs are not
authoritative.

For each package independently, the workflow uses the caller job's
token-supported GitHub Git-refs API to paginate the complete caller-repository
tag set under the exact prefix `<package-id>/`. Each tag there must be exactly
`<package-id>/v3.X.Y`; malformed prefix tags fail closed. Validation, development,
and a new release select `Y=0` when the matching line has no tags, otherwise
`max(Y)+1`. The GitHub run number supplies `N`:

| Operation | Calculated package version |
| --- | --- |
| Validation | `3.X.Y-pr.${{ github.run_number }}` (packed only) |
| Development | `3.X.Y-dev.${{ github.run_number }}` |
| Release | `3.X.Y` and `<package-id>/v3.X.Y` |

Validation and development calculate the next patch from the complete stable
tag state: `0` when the matching compatibility line has no stable tag, otherwise
numeric `max(Y)+1`. Development does not create a stable tag, so repeated
development runs use that same next-release base until a release creates its
stable tag. Release first resolves every matching line tag to its commit. A
rerun reuses a base only if exactly one package/line tag resolves to the caller
SHA. No current-SHA tag calculates the next base. Multiple current-SHA tags are
ambiguous and fail closed. A claimed calculated tag on another commit, a
malformed tag response, or a changed tag state is a failure; the workflow never
moves or overwrites a tag.

The publish workflow uses the preflight action as the only authority for branch
role and never parses `.github/package.yml` itself. Its build job emits package
version, complete calculated state, and a tag-state fingerprint only for
`development` or `release`, plus the caller-routing outputs. When the preflight
resolves `none`, the version calculation, restore, build, pack, and upload are
skipped and the caller performs no publication or tag work.

The canonical caller publish template owns the single non-cancelling
caller-repository concurrency group `nuget-${{ github.repository }}` around
preparation, publication, and tagging, so development and release do not overlap
unbounded. The reusable publish preparation workflow declares no concurrency of
its own: GitHub evaluates a called workflow against the caller's lock, and a
nested declaration of the same group can deadlock the run against itself. The
direct validation workflow declares the same non-cancelling group, and it never
cancels running work. GitHub does not guarantee FIFO: the latest pending run may
replace an earlier pending run, so this is not a durable queue.

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
  advertise the first `3.X.0`, and a new release calculates `3.X.0` with
  `release-tag-state=missing`. The preparation workflow never creates a synthetic
  tag; the immutable release tag is created only by the caller-owned
  `package-release-tag` action, in a resolved `release` run, after the selected
  publisher succeeded, and only for the exact calculated release tag. This is the
  only case where steady state is safe without a migration step.
- **Migration required** — the active compatibility line already has any stable
  package in the selected registry. Do not enable or run steady state until the
  migration procedure in
  [CALLER_OWNED_NUGET_PUBLISHING.md](CALLER_OWNED_NUGET_PUBLISHING.md) completes.
  With no line tags the tag-derived calculation is `3.X.0`, and the publisher
  sees no package at `3.X.0`, so a steady-state run would publish a new lower
  `3.X.0` rather than detect the higher packages. Migration adopts the highest
  stable `3.X.Y` published for the line, proves its `RepositoryCommit` is a real
  caller commit, deliberately creates `<package-id>/v3.X.Y`, and lets the next
  release claim `Y+1`. Adoption never selects the first `3.X.0` when higher
  stable line versions already exist.
- **Migration stays disabled** — when the highest stable line version cannot be
  provenance-proven, do not enable steady state, do not publish, and do not
  create a tag. The runtime cannot detect the unadopted higher version.

A stable package and its `<package-id>/v3.X.Y` tag must stay consistent. When the
matching stable tag already exists, the next release claims `Y+1`, and a
same-commit rerun reuses the exact tagged version. A tag is never moved or
overwritten, and a claimed tag that resolves to a different commit is a failure.

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

### Gizmo.Shared 3.0 conclusion

`Gizmo.Shared` is on compatibility line 3.0 with no stable `3.0.Y` NuGet package
in the selected registry and no `Gizmo.Shared/v3.0.Y` tag, so it needs no
migration: natural bootstrap derives the first `3.0.0-dev.N` development build
and the first stable release calculates `3.0.0`, whose immutable
`Gizmo.Shared/v3.0.0` tag the caller-owned tag action creates. Legacy `1.0.x`
packages are another compatibility line and never advance the 3.0 candidate.

## Artifacts, collision checks, and release recovery

The build job packs the calculated version with the caller commit as repository
metadata and uploads only its exact `.nupkg` path. Artifact names include both
`github.run_id` and `github.run_attempt`, so a rerun can download the exact
artifact that its preparation produced. The caller-owned publisher actions
download that artifact by name and never rebuild it; they recheck collision and
provenance immediately before publishing.

All action references are pinned to full commit SHAs, checkout credentials are
disabled, and caller-supplied strings enter shell commands only through quoted
environment variables.

## Consumer development ranges

Consumer Central Package Management may explicitly opt into the floating
development range `3.X.*-dev.*` when it intentionally tracks the latest
development build for one compatibility line. Exact development versions remain
the safer default. This is consumer documentation only: Gizmo.Infra does not
migrate consumers or enable CPM floating-version behavior.

## Public NuGet trusted publishing

Public publication requires the caller to configure NuGet.org Trusted
Publishing to trust the caller repository's own publishing workflow file,
because the OIDC-requesting job is a caller-owned normal job. Bind the exact
caller owner and repository and the caller `package-publish.yml` workflow file,
without wildcards, plus an optional GitHub environment or package scopes if the
caller uses them. The same caller policy covers both development and stable
publication.

Do not bind the Gizmo.Infra commit SHA in the NuGet.org policy. Pinning every
Gizmo.Infra workflow and action reference to one immutable 40-character commit
SHA is a separate GitHub supply-chain invariant. The repository or organization
variable `NUGET_USER` is a NuGet.org profile identifier, not a secret or API key.
