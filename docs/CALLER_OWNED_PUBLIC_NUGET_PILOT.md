# Caller-owned public NuGet publishing pilot

This pilot keeps all deterministic NuGet logic in `Gizmo.Infra` while moving the
GitHub job identity that requests the NuGet.org OIDC token back into the caller
repository.

The existing reusable workflows are intentionally left unchanged during the
pilot. New public callers compose pinned `Gizmo.Infra` composite actions in
normal caller jobs.

## Development caller shape

```yaml
concurrency:
  group: nuget-${{ github.repository }}-Gizmo.Shared
  cancel-in-progress: false

jobs:
  prepare:
    runs-on: ubuntu-latest
    permissions:
      contents: read
    outputs:
      package-artifact: ${{ steps.prepare.outputs.package-artifact }}
      package-version: ${{ steps.prepare.outputs.package-version }}
      calculated-state: ${{ steps.prepare.outputs.calculated-state }}
      tag-state-fingerprint: ${{ steps.prepare.outputs.tag-state-fingerprint }}
    steps:
      - id: prepare
        uses: GAMP/Gizmo.Infra/.github/actions/package-development-prepare@<40-character-infra-commit-sha>
        with:
          project-path: Gizmo.Shared.csproj
          package-id: Gizmo.Shared
          package-visibility: public
          nuget-user: ${{ vars.NUGET_USER }}

  publish:
    needs: prepare
    runs-on: ubuntu-latest
    permissions:
      contents: read
      id-token: write
    steps:
      - uses: GAMP/Gizmo.Infra/.github/actions/package-development-public@<40-character-infra-commit-sha>
        with:
          package-id: Gizmo.Shared
          nuget-user: ${{ vars.NUGET_USER }}
          package-artifact: ${{ needs.prepare.outputs.package-artifact }}
          package-version: ${{ needs.prepare.outputs.package-version }}
          calculated-state: ${{ needs.prepare.outputs.calculated-state }}
          tag-state-fingerprint: ${{ needs.prepare.outputs.tag-state-fingerprint }}
```

## Release caller shape

The caller has three normal jobs:

1. `prepare` uses `package-release-prepare` with `contents: read`;
2. `publish` uses `package-release-public` with `contents: read` and
   `id-token: write` when the calculated package state is `unpublished`;
3. `tag` uses `package-release-tag` with `contents: write` after publication
   succeeded, or when the prepare action proved that the same-SHA package was
   already published and only the tag needs recovery.

Keep the caller workflow itself on a protected branch and gate publish/tag jobs
to `push` or `workflow_dispatch` branch refs. The composite actions repeat this
check and fail closed.

## NuGet.org trusted publishing

For a caller such as `GAMP/Gizmo.Shared`, NuGet.org Trusted Publishing should
trust the caller workflow file (for example `package-release.yml`), because the
OIDC-requesting job is now defined by that caller workflow. The composite action
contains the mechanics, but it does not become `job_workflow_ref`.

The repository/organization variable `NUGET_USER` remains a NuGet.org profile
identifier, not a secret or API key.

## Pilot cleanup

After the caller-owned OIDC path is proven against a real NuGet.org policy,
remove the obsolete public-publisher jobs from the reusable publishing workflows
or refactor both paths onto shared deterministic scripts. Do not keep two public
publishing implementations permanently.
