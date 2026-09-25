# GitHub NuGet provider

This is the callable-workflow contract for GitHub Actions NuGet pipelines in
Gizmo.Infra. It documents workflows only; it does not configure GitHub, NuGet,
or a caller repository, and it does not perform a publication or tag write.

## Callable workflows

Gizmo.Infra exposes two direct `workflow_call` workflows. There is no mode,
descriptor version, workflow version, or equivalent version input.

| Workflow | Purpose | Caller permissions |
| --- | --- | --- |
| `.github/workflows/package-validation.yml` | Calculate, restore, audit, build, pack, and retain a nonpublished validation package. It cannot publish or tag. | `contents: read` |
| `.github/workflows/package-publish.yml` | Resolves the caller branch role, then calculates, builds, packs, and retains a nonpublished development or stable package. A `none` role stops before tag lookup, restore, build, pack, publication, or tagging work. | `contents: read` |

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
closed. It never reads
`github.event.repository.visibility`. This release only discovers and validates
visibility; it does not introduce registry-routing behavior. Consequently, the
public/private collision, publication, and release-tag jobs are disabled until a
separate routing contract is authorized.

The reusable workflow validates its own repository and file path through the
caller-independent `job.workflow_*` contexts. It validates `job.workflow_ref` as
the expected workflow identity ending in the same complete 40-character commit
SHA reported by `job.workflow_sha`, then checks out that exact commit into
`.gizmo-infra` and runs the bundled preflight action there; it never assumes a
caller-local `./.github/actions` path belongs to Gizmo.Infra. `job.workflow_sha`
alone is a resolved commit and cannot establish that a caller used a full SHA;
the original `job.workflow_ref` check enforces that invariant. Repository
visibility discovery is diagnostic and fail-closed only; it does not select a
collision check or publishing registry.

Callers grant permissions on the calling job; a called workflow cannot elevate
them. Do not use `secrets: inherit`, pass an API key, or create a
`NUGET_API_KEY` secret. The active workflows do not request publication
credentials or publish to either registry.

## Publisher invocation trust boundary

The public/private collision, publisher, and release-tag jobs remain disabled.
They do not obtain OIDC or package credentials, download artifacts, contact a
package feed, or create tags. A separate routing contract must restore an
operation-specific publisher path and its protected-branch trust boundary.

## Automatic versioning

The evaluated project `<Version>` is a compatibility-line input and must be
exactly `3.X`: generation is fixed at `3`, and `X` is a non-negative decimal
integer with no leading zero except `0`. Callers supply no patch or prerelease
version. Project descriptor values and workflow version inputs are not
authoritative.

For each package independently, the workflow uses the caller job's
token-supported GitHub Git-refs API to paginate the complete caller-repository
tag set under the exact prefix `<package-id>/`. Each tag there must be exactly
`<package-id>/v3.X.Y`; malformed prefix tags fail closed. Validation and a new
release select `Y=0` when the matching line has no tags, otherwise numeric
`max(Y)+1`. The GitHub run number supplies `N`:

| Operation | Calculated package version |
| --- | --- |
| Validation | `3.X.Y-pr.${{ github.run_number }}` (packed only) |
| Development | `3.X.Y-dev.${{ github.run_number }}` |
| Release | `3.X.Y` and `<package-id>/v3.X.Y` |

Validation calculates the next patch from the complete tag state. Development
uses the current highest stable patch on its compatibility line (or `0` when no
stable tag exists), so a development package never advances the stable patch.
Release first resolves every matching line tag to its commit. A rerun reuses a
base only if exactly one package/line tag resolves to the caller SHA. No
current-SHA tag calculates the next base. Multiple current-SHA tags are
ambiguous and fail closed. A claimed calculated tag on another commit, a
malformed tag response, or a changed tag state is a failure; the workflow never
moves or overwrites a tag.

The publish workflow uses the preflight action as the only authority for branch
role and never parses `.github/package.yml` itself. Its build job emits package
version, complete calculated state, and a tag-state fingerprint only for
`development` or `release`. The inactive publisher and tag jobs retain their
fail-closed rechecks but do not run until a routing contract authorizes them.

Development, release, and validation use a shared caller-repository concurrency
group `nuget-${{ github.repository }}` with `cancel-in-progress: false`. It
never cancels running work. GitHub does not guarantee FIFO: the latest pending
run may replace an earlier pending run, so this is not a durable queue.

## Artifacts, collision checks, and release recovery

The build job packs the calculated version with the caller commit as repository
metadata and uploads only its exact `.nupkg` path. Artifact names include both
`github.run_id` and `github.run_attempt`. No active job downloads the artifact,
queries a package feed, publishes, or creates a release tag.

All action references are pinned to full commit SHAs, checkout credentials are
disabled, and caller-supplied strings enter shell commands only through quoted
environment variables. No third-party GitHub Action is used.

## Consumer development ranges

Consumer Central Package Management may explicitly opt into the floating
development range `3.X.*-dev.*` when it intentionally tracks the latest
development build for one compatibility line. Exact development versions remain
the safer default. This is consumer documentation only: Gizmo.Infra does not
migrate consumers or enable CPM floating-version behavior.

## Public NuGet trusted publishing

Do not configure NuGet.org Trusted Publishing for these inactive publishers.
Any future publisher is a confirmation-gated operator action and must bind the
exact caller repository and approved immutable Gizmo.Infra revision without
wildcards.
