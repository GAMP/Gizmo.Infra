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

Pin both the reusable workflow and every composite action to the same complete
40-character Gizmo.Infra commit SHA. Keep the caller workflow on a protected
branch and trigger it only from `push` or `workflow_dispatch`; every publisher
composite repeats the protected-branch and event check and fails closed. Do not
name the development or release branches in the trigger: the caller's
`.github/package.yml` configuration and the preflight `branch-role` output are
the only branch-role authority, and any other ref resolves to `none` and
publishes nothing.

```yaml
name: Publish package

on:
  push:
  workflow_dispatch:

permissions:
  contents: read

concurrency:
  group: nuget-${{ github.repository }}
  cancel-in-progress: false

jobs:
  prepare:
    uses: GAMP/Gizmo.Infra/.github/workflows/package-publish.yml@<40-character-infra-commit-sha>
    permissions:
      contents: read

  publish-public:
    needs: prepare
    if: ${{ needs.prepare.outputs.branch-role != 'none' && needs.prepare.outputs.repository-visibility == 'public' }}
    runs-on: ubuntu-latest
    permissions:
      contents: read
      id-token: write
    steps:
      - name: Publish public package
        uses: GAMP/Gizmo.Infra/.github/actions/package-public-publish@<40-character-infra-commit-sha>
        with:
          package-id: ${{ needs.prepare.outputs.package-id }}
          nuget-user: ${{ vars.NUGET_USER }}
          package-artifact: ${{ needs.prepare.outputs.package-artifact }}
          package-version: ${{ needs.prepare.outputs.package-version }}
          calculated-state: ${{ needs.prepare.outputs.calculated-state }}
          tag-state-fingerprint: ${{ needs.prepare.outputs.tag-state-fingerprint }}
          repository-visibility: ${{ needs.prepare.outputs.repository-visibility }}

  publish-private:
    needs: prepare
    if: ${{ needs.prepare.outputs.branch-role != 'none' && needs.prepare.outputs.repository-visibility == 'private' }}
    runs-on: ubuntu-latest
    permissions:
      contents: read
      packages: write
    steps:
      - name: Publish private package
        uses: GAMP/Gizmo.Infra/.github/actions/package-private-publish@<40-character-infra-commit-sha>
        with:
          package-id: ${{ needs.prepare.outputs.package-id }}
          package-artifact: ${{ needs.prepare.outputs.package-artifact }}
          package-version: ${{ needs.prepare.outputs.package-version }}
          calculated-state: ${{ needs.prepare.outputs.calculated-state }}
          tag-state-fingerprint: ${{ needs.prepare.outputs.tag-state-fingerprint }}
          repository-visibility: ${{ needs.prepare.outputs.repository-visibility }}

  reject-unsupported-visibility:
    needs: prepare
    if: ${{ needs.prepare.outputs.branch-role != 'none' && needs.prepare.outputs.repository-visibility != 'public' && needs.prepare.outputs.repository-visibility != 'private' }}
    runs-on: ubuntu-latest
    permissions: {}
    steps:
      - name: Fail closed for unsupported visibility
        shell: bash
        env:
          REPOSITORY_VISIBILITY: ${{ needs.prepare.outputs.repository-visibility }}
        run: |
          set -euo pipefail
          echo "Unsupported caller repository visibility '$REPOSITORY_VISIBILITY' for this branch role; refusing to publish or tag." >&2
          exit 1

  tag:
    needs:
      - prepare
      - publish-public
      - publish-private
    if: ${{ always() && needs.prepare.outputs.branch-role == 'release' && (needs.publish-public.result == 'success' || needs.publish-private.result == 'success') }}
    runs-on: ubuntu-latest
    permissions:
      contents: write
    steps:
      - name: Reconcile immutable release tag
        uses: GAMP/Gizmo.Infra/.github/actions/package-release-tag@<40-character-infra-commit-sha>
        with:
          release-tag: ${{ needs.prepare.outputs.release-tag }}
          calculated-state: ${{ needs.prepare.outputs.calculated-state }}
          tag-state-fingerprint: ${{ needs.prepare.outputs.tag-state-fingerprint }}
```

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

Configure NuGet.org Trusted Publishing for the caller repository to trust the
caller's own publishing workflow file, because the OIDC-requesting job is
defined by that caller workflow. Bind the exact caller repository and the
approved immutable Gizmo.Infra revision used to pin `package-public-publish`,
without wildcards. The repository or organization variable `NUGET_USER` remains
a NuGet.org profile identifier, not a secret or API key.

Do not use `secrets: inherit`, pass a permanent API key, or create a
`NUGET_API_KEY` secret. The public path obtains a short-lived API key through
OIDC and masks it before use.
