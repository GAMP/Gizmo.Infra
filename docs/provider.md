# Gizmo.Infra package provider

This is the callable-workflow contract for GitHub Actions package pipelines in
Gizmo.Infra. It documents the workflow and composite actions only; it does not
configure GitHub, a package feed, or a caller repository, and the reusable plan
workflow does not perform a publication or tag write. The caller-owned routing
shape is in [caller.md](caller.md).

## Callable workflow

Gizmo.Infra exposes one direct `workflow_call` workflow,
`.github/workflows/package.yml`. It requires exactly two caller-supplied string
inputs, `prev` and `prod`, and declares exactly four outputs, `publisher`, `tag`,
`state`, and `infra-sha`. There is no mode, descriptor version, workflow version,
project, package, version, patch, visibility, or caller SHA input.

**Warning: breaking input rename.** A caller that still passes `with: dev` must
change that key to `with: prev` in the same change that adopts this `@stable`
contract or a new explicit Infra SHA; a caller pinned to an older Infra SHA keeps
the old contract. There is no `dev` alias, and schema version 2 rejects a
version 1 state with no fallback.

Callers reference the workflow exactly once through the maintained
`GAMP/Gizmo.Infra/.github/workflows/package.yml@stable` entrypoint by default.
That entrypoint must resolve to the Gizmo.Infra `refs/tags/stable` tag; a branch
named `stable` must never exist to shadow it. The only supported opt-out is to
replace `@stable` with one explicit, complete 40-character Gizmo.Infra commit
SHA. An arbitrary branch, another tag, a bare unqualified `stable` value in
`job.workflow_ref`, an abbreviated SHA, an expression, or a caller-supplied SHA
value is not an acceptable or trusted workflow reference.

The workflow runs one `contents: read` preparation job. Immediately after the
cheap role gate, and before any checkout or toolchain setup, it validates its own
trusted identity from the runner-provided `job.workflow_*` contexts and exports
the verified resolved commit as `infra-sha`. It then resolves the caller branch
role, discovers the single SDK-style packable project, resolves the authenticated
caller repository visibility, calculates the package version and tag state,
builds and packs the exact artifact, and emits the routing outputs. It never
requests OIDC, never contacts a package feed, never publishes, and never creates
a tag.

| Event / resolved role | Package work | `publisher` | `tag` |
| --- | --- | --- | --- |
| `pull_request` | build and pack `<major>.<minor>.<patch>-dev.N` | `none` | `false` |
| push to the declared preview branch | build and pack `<major>.<minor>.<patch>-preview.N` | routed | `false` |
| push to `prod` | build and pack `<major>.<minor>.<patch>` | routed | `true` |
| any other ref | none | `none` | `false` |
| publishable ref with unsupported routing | none | workflow fails closed | — |

A pull request only validates; it never publishes and never tags. An unrelated
ref is a cheap successful no-op: the role is resolved before any checkout,
toolchain setup, project discovery, tag lookup, build, pack, publish, or tag
work, and only the unconditional trusted identity guard still runs so the no-op
exports its verified `infra-sha`. A publishable ref whose visibility cannot be
routed fails the plan job, so no publisher and no tag job run; unsupported
routing is never reported as a silent `none` publication result.

Routing selects `nuget` for `public` visibility and `internal` for `private`
visibility. The `internal` capability is selected but not implemented: the
canonical internal job fails closed, publishes nothing, and no tag job can run
from it. It is never a silent skip, a private fallback, or a public NuGet route.

### Branch role and trusted source

The workflow resolves the logical role from the event and the two declared
branches. A pull request resolves the role from its base branch; a push resolves
it from the effective branch; any other ref resolves `none`. The physical branch
names must be distinct valid Git branch names.

Immediately after the cheap role gate, and before any checkout or toolchain
setup, an unconditional trusted identity guard validates four runner-provided
facts. The workflow repository must be exactly `GAMP/Gizmo.Infra`, the file path
exactly `.github/workflows/package.yml`, and `job.workflow_sha` a complete
40-character lowercase commit SHA. The suffix of `job.workflow_ref` after that
exact repository and path must be either `refs/tags/stable` or a complete
40-character lowercase SHA equal to `job.workflow_sha`. A bare `stable`, an
arbitrary branch, another tag, a malformed value, a foreign repository or path,
or a mismatched SHA fails closed. The guard trusts only those runner facts: it
does not accept a caller-supplied SHA, query a ref, or fall back to `github.sha`.

The verified `job.workflow_sha` is exported as the `infra-sha` step, job, and
workflow output. The guard runs even when the role resolves `none`, so a valid
no-op still reports its resolved provenance, while the remaining preparation
steps stay role-gated. For real work the workflow checks out the literal
`GAMP/Gizmo.Infra` repository at `infra-sha` into `.gizmo-infra` and runs the
bundled `preflight` action there; it never assumes a caller-local
`./.github/actions` path belongs to Gizmo.Infra. `job.workflow_sha` alone is a
resolved commit and cannot establish which reference the caller used; the
`job.workflow_ref` check enforces the stable-tag-or-explicit-SHA invariant.

The preflight uses the caller `GITHUB_TOKEN` and `github.repository` to read
authenticated repository metadata with bounded connect and total request
timeouts, then emits the visibility. Only `public`, `private`, and `internal`
visibility values are accepted; transport failures, timeouts, non-success
responses, malformed metadata, and unknown values fail closed. It never reads
`github.event.repository.visibility`.

### Routing outputs

The plan job exports `publisher`, `tag`, `state`, and `infra-sha`.

`publisher` is `nuget`, `internal`, or `none`. `tag` is `true` only for a
production push that selected a publisher. The planner contract is unchanged:
a private repository still seals `publisher=internal`, which the canonical
caller must route to the fail-closed placeholder. `state` is an opaque,
explicitly versioned, bounded payload consumed only by Gizmo.Infra composite
actions; the caller must forward it unchanged and must never parse it. It is an
API simplification, not a signature or trust boundary: a caller can always alter
its own workflow, so each privileged action independently revalidates the
security-sensitive facts it needs from live GitHub, feed, and tag state.

`infra-sha` is the verified 40-character Gizmo.Infra commit the run executes.
The nuget publisher and tag job must validate it as non-empty lowercase hex,
check out the literal `GAMP/Gizmo.Infra` repository at that exact commit into
`.gizmo-infra`, verify the checkout HEAD equals it, and only then run the local
composite action from `./.gizmo-infra/.github/actions/`. This keeps one run on
one Infra commit even if `refs/tags/stable` moves between jobs, and it is why the
caller has one external reusable-workflow reference instead of separate remote
publisher or tag action references.

Discovered repository visibility, selected destination capability, and
authentication are three separate concerns:

| Discovered visibility | Selected capability | Authentication |
| --- | --- | --- |
| `public` | `nuget` | Caller-owned OIDC (`id-token: write`), exchanged for a short-lived NuGet.org API key |
| `private` | `internal` (not implemented) | None; the canonical internal job fails closed and publishes nothing |
| `internal` | unsupported; fail closed | None; the plan job fails and no publication or tag work runs |

The plan workflow only discovers and exports visibility. The caller owns the
routing jobs and the public credential. Repository visibility and destination
registry are separate concerns. No internal-registry backend or composite action
is implemented; the canonical internal job fails closed before any publication
or tag work.

Callers grant permissions on the calling job; a called workflow cannot elevate
them. Do not use `secrets: inherit`, pass an API key, or create a
`NUGET_API_KEY` secret. The public publisher requests only `contents: read` and
`id-token: write`; the internal placeholder requests only `contents: read` and
no write credential; the tag job requests only `contents: write`. The plan job
requests only `contents: read` and never requests OIDC.

## Composite actions

The plan job runs the bundled `preflight` composite action for discovery. The
caller-owned nuget and tag jobs then run these Gizmo.Infra composite actions
locally from the same-run `.gizmo-infra` checkout at the plan's `infra-sha`:

| Action | Destination | Credentials | Inputs | Modes |
| --- | --- | --- | --- | --- |
| `nuget` | NuGet.org | OIDC (`id-token: write`) | `state`, `user` | development and production |
| `tag` | none | Caller `GITHUB_TOKEN` (`contents: write`) | `state` | production only; fails closed otherwise |

The internal capability has no composite action. The canonical caller's internal
job is a fail-closed placeholder with no publication credential.

Each action structurally parses and validates the versioned `state`, then
independently revalidates the push event, the protected branch ref, the
authenticated current visibility and destination, the current package version
and tag-state fingerprint, the exact prepared artifact identity and digest, and
its feed provenance before mutating anything. State that is missing, malformed,
tampered, or unknown-version fails closed. A version that already exists with
the caller SHA embedded as provenance is treated as an already-published success
so the immutable release tag can be reconciled; a version that exists without
matching provenance fails closed.

The `nuget` action pushes the prepared `.nupkg` to the NuGet.org
`PackagePublish/2.0.0` resource (`PUT https://www.nuget.org/api/v2/package`,
multipart body, authenticated by the short-lived OIDC-derived API key in the
`X-NuGet-ApiKey` header and declaring the `X-NuGet-Protocol-Version: 4.1.0`
header) and selects its path only from the structured HTTP status: any `2xx`
acceptance succeeds immediately with no readback, `409` means the exact package
ID and version already exists and continues to a bounded provenance readback,
and every other status fails closed. NuGet.org repository-signs stored archives,
so the stored package is not required to be byte-identical to the prepared
artifact. The untrusted nuspec is parsed structurally and namespace-aware by the
shared checked-in provenance module, which requires exactly one namespaced
`package`, `metadata`, `id`, `version`, and `repository` with a single `commit`
attribute and rejects malformed XML, DTDs and entities, duplicate or decoy
elements, and non-nuspec namespaces. A duplicate is accepted only when the sole
nuspec's package ID and version match the calculated values and its full
40-character `RepositoryCommit` equals the caller SHA; multiple nuspec entries, a
decoy or unexpected nuspec name, or any provenance mismatch fails closed before
any tag can be reconciled. Every publish, readback, and metadata request carries
explicit connect and total time budgets, and the key-bearing `PUT` never follows
a redirect. The `nuget` action rejects any visibility other than `public`, so a
caller wiring mistake cannot silently publish through a different route.

## Automatic versioning

The evaluated project `<Version>` is a compatibility-line input and must be
exactly `<major>.<minor>` with `major >= 1` and `minor >= 0`. Each component is
a canonical decimal: `0` or a non-zero digit followed by digits, so leading
zeros are rejected. So `1.0`, `1.1`, `3.0`, and `4.12` are valid, while `0.0`,
`0.7`, `01.0`, `1.01`, and `1.0.14` are rejected. The declared major and minor
select the active compatibility line; neither is fixed. Callers supply no patch
or prerelease version: the patch and any `-preview.N`/`-dev.N` suffix are
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
| Validation | `<major>.<minor>.<patch>-dev.${{ github.run_number }}` (packed only) |
| Development | `<major>.<minor>.<patch>-preview.${{ github.run_number }}` |
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
registry-inspection and adoption prerequisite.

The plan job emits the package version, tag-state fingerprint, and no-op or
routing outputs. When the role resolves `none`, the version calculation, restore,
build, pack, and upload are skipped and the caller performs no publication or
tag work.

The canonical single caller workflow owns the only non-cancelling
caller-repository concurrency group `nuget-${{ github.repository }}` around
validation, preparation, publication, and tagging, so development and production
do not overlap unbounded. The reusable workflow declares no concurrency of its
own: GitHub evaluates a called workflow against the caller's lock, and a nested
declaration of the same group can deadlock the run against itself. GitHub does
not guarantee FIFO: the latest pending run may replace an earlier pending run,
so this is not a durable queue.

### Stable entrypoint, reruns, and promotion

`refs/tags/stable` is the only intentionally movable Gizmo.Infra reference. It
moves only through an explicitly approved promotion of an integrated, validated
commit that has passed the applicable review and validation. The promotion
records the old and new target SHA, the actor, and a readback resolving
`refs/tags/stable^{commit}` to the new target; package-qualified release tags,
published packages, and their provenance are never moved or rewritten. Neither
the reusable workflow nor any composite action promotes the tag.

A normal rerun of all jobs re-executes the plan job. When the caller uses the
`@stable` entrypoint, GitHub resolves the tag again for the rerun, so the rerun
may execute a newer Infra commit than the original attempt. All jobs in that
rerun still share the new plan's `infra-sha`, so same-run consistency is
preserved even though the run attempt may differ. Use the explicit 40-character
SHA opt-out when an exact Infra commit must survive reruns.

A partial retry of only failed nuget or tag jobs does not re-execute the plan
job. The sealed state binds the original `GITHUB_RUN_ATTEMPT`, and the artifact
name includes `github.run_attempt`, so such a retry can fail closed with a
state-binding or artifact mismatch. The payload is also schema-versioned, so a
state from another Gizmo.Infra schema version is rejected as `unknown-version`
instead of being read through a compatibility fallback. Re-run all jobs (or
re-run the whole workflow run) so the plan re-seals state and rebuilds the
attempt-scoped artifact. The nuget publisher that already succeeded is
idempotent: a same-SHA rerun recognizes the published package and proceeds to
reconcile the immutable release tag.

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
  `<major>.<minor>.0` with `release-tag-state=missing`. The plan workflow never
  creates a synthetic tag; the immutable release tag is created only by the
  caller-owned `tag` action, in a resolved `production` run, after the nuget
  publisher succeeded, and only for the exact calculated release tag. This is the
  only case where steady state is safe without a migration step.
- **Migration required** — the active compatibility line already has any stable
  package in the selected registry. Do not enable or run steady state until the
  migration procedure in [caller.md](caller.md) completes. With no line tags the
  tag-derived calculation is `<major>.<minor>.0`, and the publisher sees no
  package at `<major>.<minor>.0`, so a steady-state run would publish a new lower
  `<major>.<minor>.0` rather than detect the higher packages. Migration adopts
  the highest stable `<major>.<minor>.<patch>` published for the line, proves its
  `RepositoryCommit` is a real caller commit, deliberately creates
  `<package-id>/v<major>.<minor>.<patch>`, and lets the next production run claim
  `patch+1`. Adoption never selects the first `<major>.<minor>.0` when higher
  stable line versions already exist.
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
and the workflow neither publishes nor creates a recovery tag.

### Gizmo.Shared 1.0 migration and adoption

This is a historical adoption scenario, not the current published baseline. Its hypothetical next development version follows the new `-preview.N` contract.

`Gizmo.Shared` is on compatibility line 1.0, and the stable `Gizmo.Shared 1.0.13`
package already exists in the selected registry, so it is not a natural bootstrap
and needs migration: keep steady state disabled until the existing package is
adopted. Prove that `Gizmo.Shared 1.0.13` embeds a real caller commit as its
`RepositoryCommit` and deliberately create the immutable `Gizmo.Shared/v1.0.13`
tag. Steady state then derives the next patch: the next development build is
`1.0.14-preview.N`, the next stable release calculates `1.0.14`, and the caller-owned
`tag` action creates the immutable `Gizmo.Shared/v1.0.14` tag.

The evaluated project `<Version>` remains the compatibility line only and the
pilot must ultimately use `<Version>1.0</Version>`; the `1.0.14` patch is
infrastructure-owned and is never encoded in the project `Version`.

## Artifacts, collision checks, and release recovery

The plan job packs the calculated version with the caller commit as repository
metadata, computes the exact `.nupkg` digest, and uploads only its exact path.
Artifact names include both `github.run_id` and `github.run_attempt`, so a rerun
can download the exact artifact that its preparation produced. The caller-owned
nuget publisher downloads that artifact by name, verifies its digest against the
plan state, and never rebuilds it; it rechecks collision and provenance
immediately before publishing. Same-SHA recovery re-runs all jobs of an existing
production `push` workflow run rather than a new manually dispatched run: the
rerun keeps the pushed caller commit and immutable tag state, the plan re-seals
the attempt-scoped state and artifact, the nuget publisher treats the matching
package as already published, and the tag job reconciles the release tag. A
partial retry of only nuget or tag jobs is not sufficient, because the state and
artifact are bound to the original run attempt; see the rerun rules above.

The public publisher's duplicate readback is deterministic and finite: it issues
at most 13 flat-container `GET` requests spaced 10 seconds apart, with a
120-second total-delay cap. Every readback `GET` carries a 5-second connect and
15-second total budget, and the push `PUT` carries a 10-second connect and
60-second total budget, so an unresponsive feed cannot hang the step. A readable
package is validated immediately; a not-yet-indexed package is retried until the
request or delay budget is exhausted and then fails closed. A new package
accepted on the push `2xx` status performs no readback, and the readback runs
only after a `409` duplicate.

Third-party action references are pinned to full commit SHAs and checkout
credentials are disabled. Gizmo.Infra's reusable-workflow entrypoint is the
`stable` tag or an explicit 40-character SHA, and its composite actions run from
the plan's same-run `infra-sha` checkout. Caller-supplied strings enter shell
commands only through quoted environment variables.

## Dependency baseline and audit reporting

The restore step keeps NuGet audit enabled over direct and transitive packages
(`-p:NuGetAudit=true -p:NuGetAuditMode=all -p:NuGetAuditLevel=low`) and passes two
percent-encoded flag sets: `-p:WarningsAsErrors=NU1605%3BNU1900%3BNU1905` and
`-p:WarningsNotAsErrors=NU1901%3BNU1902%3BNU1903%3BNU1904`, encoded because a raw
`;` would end the step's command. The second set makes **every** vulnerability
finding a visible, non-blocking warning, low through critical and wider than the
known MP198 advisory; nothing is suppressed, so each finding stays visible and
unresolved. This temporary CI alignment with local restore, by explicit user
decision, is not an allowlist, not customer acceptance of the known advisories,
and not a permanent posture: a caller cannot rely on this plan to fail a build on
a high or critical advisory and must gate it in a caller-owned job.

The first set escalates exactly `NU1605` (downgrade, named because the
command-line property replaces the SDK default that promotes it), `NU1900`
(vulnerability-data retrieval), and `NU1905` (an audit source without a
vulnerability database), which appears only when audit sources are configured, so
its absence is not coverage evidence. The escalation hard-fails only what NuGet
reports and proves no feed completeness. Both are command-line globals and replace
a caller's project-level `WarningsAsErrors` and `WarningsNotAsErrors` for that
restore; unrelated warnings keep their default severity, and `Build`/`Pack` run
with `--no-restore` and no audit properties.

The approved Version 3 customer baseline preserves the actual MessagePack and
SignalR package versions resolved at its authoritative product commit; it is not
a universal package version, and this workflow does not automatically upgrade or
reset it. No workflow or documentation claim here asserts a clean audit that the
source does not produce.

## Consumer floating-version behavior

Consumers may opt into NuGet floating versions when they intentionally want
automatic package advancement. This behavior is owned by NuGet resolution, not
by Gizmo.Infra, so callers must choose a range whose semantics match the intended
channel.

The Gizmo.Shared 1.0 pilot verified the following **historical** `-dev` package behavior against NuGet.org with
clean isolated restores. These existing published package names are retained as evidence; they are **not** the new development naming policy:

| PackageReference version | Observed behavior |
| --- | --- |
| `1.0.16-dev.*` | Resolved `1.0.16-dev.18`, then advanced to `1.0.16-dev.21` without changing the reference while no stable `1.0.16` existed. |
| `1.0.16-*` | Also resolved `1.0.16-dev.18`, then advanced to `1.0.16-dev.21`. |
| `1.0.15-dev.*` | Resolved stable `1.0.15` once that matching stable version existed. |
| `1.0.14-dev.*` | Resolved stable `1.0.14` even though matching development versions existed. |
| `1.0.*` | Resolved the latest stable patch, `1.0.15`, and did not select `1.0.16-dev.*`. |

For newly published preview versions, `<major>.<minor>.<patch>-preview.*` is suitable for following the newest
preview build for a specific next patch only while the matching stable
`<major>.<minor>.<patch>` does not exist. It is not a permanent preview-only
channel: after the matching stable version is published, NuGet may prefer that
stable version.

Existing `-dev.*` references match the historical `-dev` family and do not
advance to `-preview.*` builds; consumers must move to `-preview.*` or an exact
version.

If a consumer must remain exclusively on preview packages after a matching
stable release exists, do not rely on `<major>.<minor>.<patch>-preview.*` as that
channel boundary. Use an exact prerelease version or another explicitly managed
consumer policy instead.

A stable patch float such as `<major>.<minor>.*` follows stable packages and
does not advance to a higher prerelease patch. Exact versions remain the most
predictable choice when automatic advancement is not required.

Broader prerelease-aware floats are also possible:

| Pattern | Scope | Example with the historical Gizmo.Shared 1.x pilot set |
| --- | --- | --- |
| `1.0.*-*` | Highest version in the `1.0` line, including prereleases | Selects `1.0.16-dev.21` while stable `1.0.16` does not exist; after stable `1.0.16` is published, that stable version wins. |
| `1.*-*` | Highest version anywhere in major `1`, including prereleases | Also selects `1.0.16-dev.21` at the time of the pilot, but may later move to a higher minor such as `1.1.0-dev.N`. |
| `1.0.*` | Highest stable version in the `1.0` line | Selects `1.0.15` and ignores `1.0.16-dev.*`. |

The important distinction is that the numeric core is compared before
prerelease precedence. Therefore `1.0.16-dev.21` is newer than stable
`1.0.15`, while stable `1.0.16` is newer than `1.0.16-dev.21`.

Use `<major>.<minor>.*-*` when a consumer should stay within one compatibility
line but follow both development and stable releases automatically. Use
`<major>.*-*` only when automatically moving to later minor lines is also
acceptable. These broader patterns were not part of the controlled Gizmo.Shared
pilot matrix above; the examples follow NuGet floating-version ordering applied
to the currently published version set.

This is consumer documentation only: Gizmo.Infra does not alter NuGet floating
resolution semantics, migrate consumers, or enable Central Package Management
floating-version behavior.

## Public NuGet trusted publishing

Public publication requires the caller to configure NuGet.org Trusted
Publishing to trust the caller repository's own single publishing workflow file,
because the OIDC-requesting job is a caller-owned normal job. Bind the exact
caller owner and repository and the caller `package.yml` workflow file, without
wildcards, plus an optional GitHub environment or package scopes if the caller
uses them. The same caller policy covers both development and production
publication.

Do not bind the Gizmo.Infra commit SHA in the NuGet.org policy. Using the single
`@stable` entrypoint, or its explicit 40-character SHA opt-out, and running local
actions from the plan's verified `infra-sha` is a separate GitHub supply-chain
invariant. The repository or organization variable `NUGET_USER` is a NuGet.org
profile identifier, not a secret or API key.

## Shared state module (implementation)

The plan workflow and the `nuget` and `tag` actions share one checked-in module,
`.github/package/state.py`. It exposes `plan`, `seal --digest <sha256>`,
`validate --role production|publishable --expect-publisher nuget|internal|publishable`,
and `fingerprint`; `validate` reads the opaque state on stdin and writes the
validated routing fields to stdout, or to the path given by `--github-env`.
The `internal` value remains part of the unchanged planner and state contract;
no internal publisher action consumes it. The payload is schema version 2, and a
payload with any other version is rejected as `unknown-version` with no
compatibility fallback or alias. Consuming actions revalidate
independently and never treat the payload as a signature.
