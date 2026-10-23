# Caller-owned package publishing

This is the canonical caller contract for publishing a NuGet package through
Gizmo.Infra. The deterministic discovery, versioning, artifact, collision, and
tag logic stays in Gizmo.Infra behind one read-only plan workflow, while each
GitHub job identity that owns a mutation credential is a normal job in the
caller repository.

## Why the mutation jobs are caller-owned

A reusable `workflow_call` job runs with the called workflow's identity, so it
cannot present caller-owned OIDC claims to NuGet.org and cannot hold a
caller-scoped package or tag credential. The reusable
`.github/workflows/package.yml` is therefore a non-mutating preparation
authority: it resolves the branch role and authenticated visibility, discovers
the package, calculates the version and tag state, builds and packs the exact
artifact, and emits routing outputs. It never requests OIDC, contacts a package
feed, publishes, or creates a tag.

The caller then routes to exactly one destination capability. Only `nuget` is
implemented: the canonical `internal` job fails closed, and the `none` route runs
no publication or tag work.

## Canonical caller shape

A package repository needs exactly one package automation file:

```text
.github/
  workflows/
    package.yml
```

The versioned, tested template is
[`.github/templates/package.yml`](../.github/templates/package.yml). Copy it to
the caller repository as `.github/workflows/package.yml` and replace both branch
placeholders with real branch names. Do not rename the file: the caller workflow
file is part of the NuGet.org Trusted Publishing binding.

The single workflow triggers on `pull_request` and `push`. It declares the two
physical branch names exactly once as YAML anchors and passes both to
Gizmo.Infra as the required reusable `prev` and `prod` inputs. Only the input
names are fixed: the preview branch is passed as `prev`, which is the reusable
development-channel role rather than the caller's branch name. The workflow is
referenced exactly once, through the maintained `@stable` entrypoint:

```yaml
env:
  PREV: &prev pre-release
  PROD: &prod release

jobs:
  plan:
    uses: GAMP/Gizmo.Infra/.github/workflows/package.yml@stable
    with:
      prev: *prev
      prod: *prod
```

**Warning: breaking input rename.** A caller that still passes `with: dev` must
change that key to `with: prev` in the same change that adopts this `@stable`
contract or a new explicit Infra SHA; a caller pinned to an older Infra SHA keeps
the old contract. There is no `dev` alias, and schema version 2 rejects a
version 1 state with no fallback.

`@stable` must resolve to the Gizmo.Infra `refs/tags/stable` tag, so no branch
named `stable` may exist. The only supported opt-out is replacing `@stable` with
one explicit, complete 40-character Gizmo.Infra commit SHA. Never use an
arbitrary branch, another tag, an abbreviated SHA, or an expression.

There is no separate `.github/package.yml` descriptor and no separate caller
`package-validation.yml` or `package-publish.yml`. The branch names live only in
this one file, so changing a package branch requires editing only the single
caller workflow, and the caller never duplicates those names in trigger filters.

The plan job exports `infra-sha`, the verified 40-character Infra commit for the
run. The nuget publisher and tag job must check out `GAMP/Gizmo.Infra` at that
exact commit into `.gizmo-infra`, verify the checkout HEAD, and run the local
composite action from `./.gizmo-infra/.github/actions/`. The internal placeholder
fails before any checkout or publication. Do not add separate remote `@stable`
action references, a dynamic `uses` ref, or a caller SHA input. Use the
explicit 40-character SHA opt-out when an exact Infra commit must survive reruns.

The caller is the only owner of the non-cancelling caller-repository concurrency
group `nuget-${{ github.repository }}` around planning, publication, and
tagging. The reusable workflow declares no concurrency of its own, so one run is
never evaluated against the same lock twice.

### Event and role contract

Gizmo.Infra resolves the logical role from the event plus the two declared branch
inputs:

- `pull_request` — compare the pull-request base branch. Base equal to the
  preview branch resolves `development`, base equal to the production branch
  resolves `production`, and any other base resolves `none`. A pull request only
  validates; it never publishes and never tags.
- `push` — compare the effective branch. The preview branch resolves
  `development`, the production branch resolves `production`, and any other
  branch resolves `none`.

There is no `workflow_dispatch` trigger. Publication is driven by `push` only,
and a same-SHA recovery is a re-run of an existing production `push` workflow
run rather than a new manually dispatched run.

An unrelated ref is a cheap successful no-op: the role is resolved before any
checkout, toolchain setup, project discovery, tag lookup, build, pack, publish,
or tag work, and the plan emits `publisher=none` and `tag=false`. A publishable
ref whose visibility cannot be routed fails the plan job, so no publisher and no
tag job run; unsupported routing is never reported as a silent `none`
publication result.

### One-file jobs

The canonical template contains:

- `plan` — calls the reusable plan workflow with `contents: read`. It builds and
  packs the calculated `-dev.N` validation version for a pull request, prepares
  the development or stable version for a push, and exports `publisher`, `tag`,
  the opaque `state`, and the verified `infra-sha`.
- `nuget` — caller-owned, `contents: read` plus `id-token: write`, and only for
  `publisher == 'nuget'`. It requires the planned `infra-sha`, checks out
  Infra at that exact commit into `.gizmo-infra`, verifies HEAD, and runs the
  local `./.gizmo-infra/.github/actions/nuget` action.
- `internal` — caller-owned, `contents: read`, and only for
  `publisher == 'internal'`. It is a fail-closed not-implemented placeholder: it
  exits non-zero, publishes nothing, and holds no package or OIDC credential.
- `tag` — caller-owned, `contents: write`, and only when the plan requested a tag
  and `needs.nuget.result == 'success'`. It uses the same planned-commit checkout
  and the local `./.gizmo-infra/.github/actions/tag` action, which reconciles the
  immutable package-qualified tag and fails closed unless the planned role is
  exactly `production` and the planned publisher is `nuget`.

## Routing and authentication

The caller must condition the destination jobs mutually exclusively from the
plan `publisher` output:

- `publisher == 'none'` runs no publication and no tag work, and a pull request
  never publishes or tags regardless of routing.
- `publisher == 'nuget'` runs only the public job: `contents: read` plus
  `id-token: write`, and it must never be granted `packages: write`.
- `publisher == 'internal'` runs only the internal placeholder: `contents: read`
  and no write credential. It exits non-zero before any publication, and it must
  never be granted `id-token` or `packages: write`.

The routing signal is only `needs.plan.outputs.publisher`. Do not use
`github.event.repository.visibility`, a repository variable, a workflow input,
or any independent visibility query. An `internal` selection must not be treated
as `none`, routed to NuGet, or skipped successfully. The `nuget` action
independently revalidates the authenticated current visibility and destination,
so a caller wiring mistake fails closed instead of silently publishing to the
wrong feed.

The tag job runs only when the plan requested a tag and `needs.nuget.result ==
'success'`, so neither a development run nor the failing internal placeholder can
tag. The `tag` action also independently revalidates the production policy role,
the planned publisher, the push event, and the protected branch ref, so a caller
wiring mistake cannot produce a tag from a development or unresolved run.

## Collision, provenance, and same-SHA recovery

The nuget publisher downloads the exact prepared artifact, verifies its digest
against the plan state, refetches the complete tag state under the package
prefix, compares the tag-state fingerprint, and then queries NuGet.org. A
calculated version that already exists is inspected: if the published package
embeds the caller commit as `RepositoryCommit`, it treats the package as already
published, skips the push, and succeeds so the tag job can reconcile the
immutable release tag. A version that exists without matching provenance fails
closed.

The public publisher determines new-versus-existing only from the structured
HTTP status of a `PUT` to the NuGet.org `PackagePublish` endpoint: `2xx` means
the feed accepted the new package and the step succeeds with no readback, `409`
means the exact ID and version already exists and the step continues to a
bounded provenance readback, and any other status fails closed. It never parses
human-readable push output. NuGet.org repository-signs stored archives, so the
downloaded package is not required to be byte-identical to the prepared
artifact; a duplicate is accepted only after reading provenance from the single
expected nuspec whose package ID, version, and full 40-character
`RepositoryCommit` all match the calculated values. The untrusted nuspec is
parsed structurally and namespace-aware by the shared checked-in provenance
module: it requires exactly one namespaced `package`, `metadata`, `id`,
`version`, and `repository` with a single `commit` attribute, and rejects
malformed XML, DTDs and entities, duplicate or decoy elements, and non-nuspec
namespaces. Multiple nuspec entries, a decoy or unexpected nuspec name, missing
or malformed provenance, or a metadata mismatch fails closed. Every publish and
readback request carries explicit connect and total time budgets, and the
key-bearing `PUT` never follows a redirect. The duplicate readback is finite: at
most 13 flat-container reads, 10 seconds apart, with a 120-second total-delay
cap; an unreadable package fails closed once the budget is exhausted. This
preserves same-SHA rerun recovery without a permanent key and without moving a
tag.

Recovery is performed by re-running all jobs of an existing production `push`
workflow run: the rerun keeps that run's pushed commit and evaluates the same
immutable tag state, the plan re-seals the attempt-scoped state and artifact, the
nuget publisher recognizes the already-published same-SHA package, and the tag job
reconciles the immutable release tag. There is no `workflow_dispatch` trigger, so
recovery never starts a new manually dispatched run and never needs one.

### Reruns, stable tag motion, and partial retries

With the `@stable` entrypoint, a rerun of all jobs re-executes `plan`, and GitHub
resolves the `stable` tag again, so that rerun can use a newer Infra commit than
the original attempt. Every job in the rerun still consumes the same new
`infra-sha`, so same-run consistency is preserved within that rerun. Pin the
explicit 40-character SHA when an exact Infra commit must survive reruns.

A partial retry of only failed nuget or tag jobs does not re-execute `plan`.
The sealed state binds the original `GITHUB_RUN_ATTEMPT` and the artifact name
includes `github.run_attempt`, so such a retry can fail closed with a binding
mismatch or a missing attempt-scoped artifact. Re-run all jobs (or re-run the
whole workflow run) instead. The nuget publisher that already succeeded is
idempotent: the same-SHA rerun recognizes the published package and proceeds to reconcile
the immutable release tag.

## Caller dependency baseline and audit gate

The approved Version 3 customer baseline keeps the actual MessagePack and
SignalR package versions resolved at the authoritative product commit. It is not
a universal package version, and a caller must not automatically upgrade or reset
those packages to make a run appear clean.

Known vulnerabilities in the frozen packages are reported as accepted customer
risk, with package, version, severity, and advisory URL. The reusable plan
workflow's existing high/critical audit gate remains unchanged, and a conflict
between the freeze and that gate is reported for an explicit decision rather than
silently waived. `NU1605` downgrade diagnostics remain distinct from audit
findings. Do not suppress the audit, claim zero vulnerabilities, or describe the
frozen baseline as remediated when it is not.

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
   from the start: the workflow derives the first `<major>.<minor>.0`, the nuget
   publisher publishes it, and the caller-owned `tag` action creates
   `<package-id>/v<major>.<minor>.0`. No manual step is required.
3. **Any stable line package — keep steady state disabled until migration.** If
   the active line has one or more stable packages in the selected registry, do
   not enable or run steady state yet. A tag-derived calculation can sit below
   the published packages and the runtime will not catch it: for example, with
   registry versions `<major>.<minor>.4` and `<major>.<minor>.5` and no line tags,
   the workflow calculates `<major>.<minor>.0`, and the nuget publisher sees no package
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

- **Runtime fail-closed** — the workflow and nuget publisher abort on malformed,
  foreign, or ambiguous tag state, on tag-state drift, and on an existing package
  at the calculated version whose provenance is not the caller commit. This
  guards the calculated version and the governed tags only.
- **Migration fail-closed** — the operator keeps steady state disabled whenever
  the registry shows a stable line package that cannot be adopted. The runtime
  never sees those higher versions, so this is an operator obligation and not a
  workflow guarantee.

### Gizmo.Shared 1.0 migration and adoption

The following historical adoption scenario illustrates version/tag calculations; it is not a statement that the 1.0.13 baseline is still current. Under the updated policy its *hypothetical next* development package uses `-preview.N`.

`Gizmo.Shared` is on compatibility line 1.0, and the stable `Gizmo.Shared 1.0.13`
package already exists in the selected registry, so the line is not a natural
bootstrap: keep steady state disabled until the migration procedure above adopts
that package. Prove the published `1.0.13` embeds a valid 40-character
`RepositoryCommit` that is a real commit in the caller repository and produced
that package, then deliberately create the immutable `Gizmo.Shared/v1.0.13` tag.
Steady state then resumes at the next patch: the next development build is
`1.0.14-preview.N`, the next stable release publishes `1.0.14`, and the caller-owned
`tag` action creates the immutable `Gizmo.Shared/v1.0.14` tag.

The evaluated project `<Version>` stays the compatibility line only: the pilot
must ultimately use `<Version>1.0</Version>`. The `1.0.14` patch remains
infrastructure-owned and is never encoded in the project `Version`.

An existing package is never adopted as a side effect of a run. Adoption is a
separate, deliberate, one-time caller action, outside the workflow; it is not
part of the reusable workflow, the caller template, or any publisher action.
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

Do not bind the Gizmo.Infra SHA in the NuGet.org policy. The single `@stable`
entrypoint, its explicit 40-character SHA opt-out, and the plan's verified
`infra-sha` local checkout are a separate GitHub supply-chain invariant and must
not appear in the Trusted Publishing binding.

The repository or organization variable `NUGET_USER` remains a NuGet.org profile
identifier, not a secret or API key.

Do not use `secrets: inherit`, pass a permanent API key, or create a
`NUGET_API_KEY` secret. The public path obtains a short-lived API key through
OIDC and masks it before use.
