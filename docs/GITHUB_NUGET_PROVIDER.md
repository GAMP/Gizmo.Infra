# GitHub NuGet provider

This is the callable-workflow contract for GitHub Actions NuGet pipelines in
Gizmo.Infra. It documents workflows only; it does not configure GitHub, NuGet,
or a caller repository, and it does not perform a publication or tag write.

## Callable workflows

Gizmo.Infra exposes three distinct direct `workflow_call` workflows. There is
no mode, descriptor version, workflow version, or equivalent version input.

| Workflow | Purpose | Caller permissions |
| --- | --- | --- |
| `.github/workflows/package-validation.yml` | Calculate, restore, audit, build, pack, and retain a nonpublished validation package. It cannot publish or tag. | `contents: read` |
| `.github/workflows/package-development.yml` | Calculate and publish a development package after fail-closed state and collision checks. | public: `contents: read`, `id-token: write`; private: `contents: read`, `packages: write` |
| `.github/workflows/package-release.yml` | Calculate and publish a stable package, then create or reconcile its immutable package-qualified tag. | public: `contents: write`, `id-token: write`; private: `contents: write`, `packages: write` |

Every caller must use an immutable, complete 40-character Gizmo.Infra commit
SHA. A branch, tag, abbreviated SHA, or expression is not an acceptable
workflow reference. For example:

```yaml
permissions:
  contents: read

jobs:
  validate-package:
    uses: gizmo/Gizmo.Infra/.github/workflows/package-validation.yml@<40-character-infra-commit-sha>
    with:
      project-path: src/Gizmo.Widget/Gizmo.Widget.csproj
      package-id: Gizmo.Widget
      package-visibility: public
```

All workflows require `project-path`, `package-id`, and `package-visibility`.
Publishing workflows additionally accept `nuget-user` only for a public
package. `project-path` is a caller-repository-relative `.csproj` path without
traversal; `package-id` must match evaluated project `PackageId`; and visibility
is exactly `public` or `private`. Public `nuget-user` is a NuGet.org profile
identifier, not an email address or secret. Inputs, paths, metadata, visibility,
and public user identifiers fail closed before restore, pack, or publication.

Callers grant permissions on the calling job; a called workflow cannot elevate
them. Do not use `secrets: inherit`, pass an API key, or create a
`NUGET_API_KEY` secret. Public publishing uses caller-bound GitHub OIDC and
exchanges it for a short-lived NuGet credential only in memory. Private
publishing uses only the calling job's `GITHUB_TOKEN` for its GitHub Packages
feed.

## Publisher invocation trust boundary

The development and release publisher jobs (`publish-public`, `publish-private`,
and the release `tag` job) run only for a trusted caller invocation. Each
publisher job requires the caller event to be `push` or `workflow_dispatch`, the
`github.ref` to be a branch ref (`refs/heads/`), and GitHub to report
`github.ref_protected` as true for that ref. Callers must configure branch
protection or a ruleset on every branch allowed to publish. `pull_request`,
`pull_request_target`, `workflow_run`, tag refs, and unprotected branches are
therefore denied: the publisher jobs are skipped before they can download the
package artifact or obtain OIDC or package credentials. The check is enforced
with supported `github` contexts in the publisher job `if` conditions; it is
not prose-only. Validation does not publish and applies no such boundary.

## Automatic versioning

The evaluated project `<Version>` is a compatibility-line input and must be
exactly `3.X`: generation is fixed at `3`, and `X` is a non-negative decimal
integer with no leading zero except `0`. Callers supply no patch or prerelease
version. Project descriptor values and workflow version inputs are not
authoritative.

For each package independently, the workflow uses the caller job's
token-supported GitHub Git-refs API to paginate the complete caller-repository
tag set under the exact prefix `<package-id>/`. Each tag there must be exactly
`<package-id>/v3.X.Y`; malformed prefix tags fail closed. The matching line
selects `Y=0` when it has no tags, otherwise numeric `max(Y)+1`. The GitHub run
number supplies `N`:

| Operation | Calculated package version |
| --- | --- |
| Validation | `3.X.Y-pr.${{ github.run_number }}` (packed only) |
| Development | `3.X.Y-dev.${{ github.run_number }}` |
| Release | `3.X.Y` and `<package-id>/v3.X.Y` |

Validation and development always calculate from the complete tag state.
Release first resolves every matching line tag to its commit. A rerun reuses a
base only if exactly one package/line tag resolves to the caller SHA. No
current-SHA tag calculates the next base. Multiple current-SHA tags are
ambiguous and fail closed. A claimed calculated tag on another commit, a
malformed tag response, or a changed tag state is a failure; the workflow never
moves or overwrites a tag.

The build job emits package version, complete calculated state, and a
tag-state fingerprint. Immediately before every publication—and before release
tagging—the workflow refetches, validates, and fingerprints the caller tag
state and rechecks the target feed version. The publication and tagging rechecks
paginate the same complete tag prefix and resolve annotated tags to their
commit, matching the build job's tag-state logic rather than a partial page. A
fingerprint/state drift or a package/tag collision fails closed. This is both an
external-race check and the release rerun safety boundary.

Development and release (and validation for the same package) use native
per-package concurrency `nuget-${{ github.repository }}-${{ inputs.package-id }}`
with `cancel-in-progress: false`. It never cancels running work. GitHub does
not guarantee FIFO: the latest pending run may replace an earlier pending run,
so this is not a durable queue.

## Artifacts, collision checks, and release recovery

The build job packs the calculated version with the caller commit as repository
metadata and uploads only its exact `.nupkg` path. Artifact names include both
`github.run_id` and `github.run_attempt`; publishers download that exact name,
never a wildcard, and never rebuild from source.

Public packages check NuGet's flat-container version index. Private packages
check the caller-owner GitHub Packages NuGet feed at
`GITHUB_REPOSITORY_OWNER` using the caller job token; they do not use GitHub's
package-management REST endpoints. For release, an existing stable version is
recoverable only after reading the published package and confirming that its
embedded repository commit equals the caller SHA; an unreadable package, a
missing commit, or a different commit fails closed instead of creating a
recovery tag. Existing versions, transport errors, unexpected statuses,
malformed or empty successful responses, and a collision detected during the
final recheck fail closed.

Release creates the lightweight immutable `<package-id>/v3.X.Y` ref only after
the stable package is known published. A previously published stable version is
recoverable only when the published package's embedded repository commit durably
matches the exact caller SHA; an existing version with no same-SHA provenance is
a collision and fails closed. When a publish succeeded but tag creation failed,
a rerun with no current-SHA tag verifies that same-SHA provenance and may create
only the missing tag. If the tag is already at the caller SHA, it is an
idempotent result. The workflow never force-updates, deletes, or moves a tag.

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

Before a public publisher is enabled, configure NuGet.org Trusted Publishing
for the exact caller repository and each applicable Gizmo.Infra reusable
workflow identity (`package-development.yml` and/or `package-release.yml`) at
the approved immutable Infra revision. The trust binding must identify both the
caller repository and reusable workflow; do not use wildcard repository or
workflow rules. The publish job requests `id-token: write` only for the public
publisher, obtains a NuGet.org-audience OIDC token, and exchanges it at the
documented NuGet endpoint.

This is a confirmation-gated operator action. Verify NuGet.org UI and OIDC
claim support before enabling a real publisher. This repository neither
performs nor implies that configuration.
