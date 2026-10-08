#!/usr/bin/env python3
"""Canonical Gizmo.Infra package state planner and validator.

This module is the single checked-in source of truth for the opaque package
state consumed only by Gizmo.Infra composite actions. State is canonical
``base64url(UTF-8(JSON))`` with an explicit schema version and an exact key set,
so a tampered, malformed, or unknown-version payload fails closed structurally
instead of through shell substring matching.

The state carries only bounded routing/identity facts and a tag-state
fingerprint; it never embeds the unbounded tag snapshot. Every mutation action
revalidates the security-sensitive facts it needs from live GitHub/feed/tag
state and treats this payload as an API simplification, never as a signature.

Subcommands
-----------
``plan``      build state + routing outputs from a request document.
``seal``      bind the packed artifact digest into the planned state.
``validate``  strictly validate state for a mutation action and export fields.
``fingerprint`` canonical tag-state fingerprint for a live tag refetch.
"""

import argparse
import base64
import binascii
import hashlib
import json
import os
import re
import shlex
import sys

SCHEMA_VERSION = 1

# Exact top-level state keys -> required type. An unknown or missing key is a
# schema failure, so accidental protocol drift cannot pass as valid state.
STATE_KEYS = {
    "v": int,
    "repo": str,
    "sha": str,
    "ref": str,
    "run": str,
    "attempt": str,
    "event": str,
    "dev": str,
    "prod": str,
    "role": str,
    "publisher": str,
    "tag": bool,
    "packageId": str,
    "compatibilityLine": str,
    "baseVersion": str,
    "packageVersion": str,
    "releaseTag": str,
    "releaseTagState": str,
    "currentShaTags": list,
    "artifactPath": str,
    "artifactDigest": str,
    "tagStateFingerprint": str,
    "repositoryVisibility": str,
}

REQUEST_KEYS = {
    "repo": str,
    "sha": str,
    "ref": str,
    "run": str,
    "attempt": str,
    "event": str,
    "dev": str,
    "prod": str,
    "role": str,
    "packageId": str,
    "compatibilityLine": str,
    "runNumber": str,
    "visibility": str,
    "tags": list,
}

TAG_ROW_KEYS = {"ref": str, "objectSha": str, "commit": str}

HEX40 = re.compile(r"^[0-9a-f]{40}$")
HEX64 = re.compile(r"^[0-9a-f]{64}$")
DEC = r"(?:0|[1-9][0-9]*)"
COMPAT = re.compile(r"^([1-9][0-9]*)\.(" + DEC + r")$")
STABLE = re.compile(r"^(" + DEC + r")\.(" + DEC + r")\.(" + DEC + r")$")
VERSION = re.compile(
    r"^(" + DEC + r")\.(" + DEC + r")\.(" + DEC + r")"
    r"(?:-(pr|dev)\.(" + DEC + r"))?$"
)
TAG_LEAF = re.compile(r"^v(" + DEC + r")\.(" + DEC + r")\.(" + DEC + r")$")
PACKAGE_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*$")
REPO = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
REF = re.compile(r"^refs/[A-Za-z0-9._/-]+$")
NUMERIC = re.compile(r"^[0-9]+$")
ROLES = ("development", "production", "none")
PUBLISHERS = ("nuget", "internal", "none")
VISIBILITIES = ("public", "private", "internal")
TAG_STATES = ("missing", "present", "not-applicable")
EVENTS = ("push", "pull_request")

MAX_STATE_BYTES = 65536


class StateError(Exception):
    """A structural or semantic state/request violation, reported as a token."""


class _DuplicateKey(Exception):
    """A JSON object repeated a key; JSON silence on this is unsafe."""


def _pair_hook(pairs):
    obj = {}
    for key, value in pairs:
        if key in obj:
            raise _DuplicateKey(key)
        obj[key] = value
    return obj


def _decode_json(text):
    try:
        return json.loads(text, object_pairs_hook=_pair_hook)
    except _DuplicateKey as error:
        raise StateError("duplicate-key") from error
    except ValueError as error:
        raise StateError("malformed-json") from error


def _type_ok(value, expected):
    if expected is bool:
        return isinstance(value, bool)
    if expected is int:
        return isinstance(value, int) and not isinstance(value, bool)
    return isinstance(value, expected)


def _check_keys(obj, keys, token):
    if not isinstance(obj, dict):
        raise StateError(token)
    if set(obj.keys()) != set(keys.keys()):
        raise StateError(token)
    for key, expected in keys.items():
        if not _type_ok(obj[key], expected):
            raise StateError(token)


def _valid_branch(name):
    if not name or len(name) > 255:
        return False
    if not re.match(r"^[A-Za-z0-9][A-Za-z0-9._/-]*$", name):
        return False
    if name.endswith("/") or name.endswith(".") or name.endswith(".lock"):
        return False
    if ".." in name or "@{" in name or "//" in name:
        return False
    return True


def _derive_role(ref, dev, prod):
    if ref == "refs/heads/" + dev:
        return "development"
    if ref == "refs/heads/" + prod:
        return "production"
    return "none"


def _route(visibility):
    if visibility == "public":
        return "nuget"
    if visibility == "private":
        return "internal"
    raise StateError("unsupported-routing")


def _snapshot(rows):
    lines = sorted("{0}={1}".format(row["ref"], row["objectSha"]) for row in rows)
    return "".join(line + "\n" for line in lines)


def fingerprint_rows(rows):
    return hashlib.sha256(_snapshot(rows).encode("utf-8")).hexdigest()


def encode_state(state):
    body = json.dumps(state, separators=(",", ":"), ensure_ascii=True)
    return base64.urlsafe_b64encode(body.encode("utf-8")).decode("ascii")


def decode_state(raw):
    raw = raw.strip()
    if not raw or len(raw) > MAX_STATE_BYTES:
        raise StateError("malformed")
    padded = raw + "=" * (-len(raw) % 4)
    try:
        data = base64.b64decode(padded.encode("ascii"), altchars=b"-_", validate=True)
    except (binascii.Error, ValueError, UnicodeEncodeError) as error:
        raise StateError("malformed") from error
    try:
        text = data.decode("utf-8")
    except UnicodeDecodeError as error:
        raise StateError("malformed") from error
    state = _decode_json(text)
    return state


def _validate_request(request):
    _check_keys(request, REQUEST_KEYS, "malformed-request")
    if request["event"] not in EVENTS:
        raise StateError("malformed-request")
    if request["visibility"] not in VISIBILITIES:
        raise StateError("malformed-request")
    if request["role"] not in ROLES:
        raise StateError("malformed-request")
    if not REPO.match(request["repo"]) or not HEX40.match(request["sha"]):
        raise StateError("malformed-request")
    if not REF.match(request["ref"]):
        raise StateError("malformed-request")
    if not NUMERIC.match(request["run"]) or not NUMERIC.match(request["attempt"]):
        raise StateError("malformed-request")
    if not NUMERIC.match(request["runNumber"]):
        raise StateError("malformed-request")
    if not _valid_branch(request["dev"]) or not _valid_branch(request["prod"]):
        raise StateError("malformed-request")
    if request["dev"] == request["prod"]:
        raise StateError("malformed-request")
    if not PACKAGE_ID.match(request["packageId"]):
        raise StateError("malformed-request")
    if not COMPAT.match(request["compatibilityLine"]):
        raise StateError("malformed-request")
    for row in request["tags"]:
        _check_keys(row, TAG_ROW_KEYS, "malformed-request")
        if not REF.match(row["ref"]):
            raise StateError("malformed-request")
        if not HEX40.match(row["objectSha"]) or not HEX40.match(row["commit"]):
            raise StateError("malformed-request")


def plan(request):
    _validate_request(request)
    role = request["role"]
    derived = _derive_role(request["ref"], request["dev"], request["prod"])
    if role != derived:
        raise StateError("role-mismatch")
    if role == "none":
        raise StateError("role-none")

    package_id = request["packageId"]
    prefix = "refs/tags/" + package_id + "/"
    parsed = []
    for row in request["tags"]:
        ref = row["ref"]
        if not ref.startswith(prefix):
            raise StateError("foreign-tag")
        leaf = ref[len(prefix):]
        match = TAG_LEAF.match(leaf)
        if not match:
            raise StateError("malformed-tag")
        parsed.append(
            (
                leaf,
                int(match.group(1)),
                int(match.group(2)),
                int(match.group(3)),
                row["objectSha"],
                row["commit"],
            )
        )

    line_match = COMPAT.match(request["compatibilityLine"])
    declared_major = int(line_match.group(1))
    declared_minor = int(line_match.group(2))
    if parsed:
        governed = max((item[1], item[2]) for item in parsed)
        allowed = (
            (declared_major, declared_minor) == governed
            or (declared_major, declared_minor) == (governed[0], governed[1] + 1)
            or (declared_major, declared_minor) == (governed[0] + 1, 0)
        )
        if not allowed:
            raise StateError("line-transition")

    max_patch = None
    current = []
    for leaf, major, minor, patch, _object_sha, commit in parsed:
        if (major, minor) == (declared_major, declared_minor):
            if max_patch is None or patch > max_patch:
                max_patch = patch
            if commit == request["sha"]:
                current.append(leaf)
    current_sorted = sorted(current)

    event = request["event"]
    role = request["role"]
    if event == "pull_request":
        patch = 0 if max_patch is None else max_patch + 1
        base = "{0}.{1}".format(request["compatibilityLine"], patch)
        package_version = "{0}-pr.{1}".format(base, request["runNumber"])
        publisher = "none"
        tag_requested = False
        release_tag_state = "not-applicable"
        current_out = []
    elif role == "development":
        patch = 0 if max_patch is None else max_patch + 1
        base = "{0}.{1}".format(request["compatibilityLine"], patch)
        package_version = "{0}-dev.{1}".format(base, request["runNumber"])
        publisher = _route(request["visibility"])
        tag_requested = False
        release_tag_state = "not-applicable"
        current_out = []
    else:
        if len(current_sorted) == 0:
            patch = 0 if max_patch is None else max_patch + 1
            base = "{0}.{1}".format(request["compatibilityLine"], patch)
            release_tag_state = "missing"
        elif len(current_sorted) == 1:
            leaf_match = TAG_LEAF.match(current_sorted[0])
            base = "{0}.{1}.{2}".format(
                leaf_match.group(1), leaf_match.group(2), leaf_match.group(3)
            )
            release_tag_state = "present"
        else:
            raise StateError("ambiguous-current-tag")
        package_version = base
        publisher = _route(request["visibility"])
        tag_requested = True
        current_out = current_sorted

    release_tag = "{0}/v{1}".format(package_id, base)
    artifact_path = "artifacts/{0}.{1}.nupkg".format(package_id, package_version)
    tag_fingerprint = fingerprint_rows(request["tags"])

    state = {
        "v": SCHEMA_VERSION,
        "repo": request["repo"],
        "sha": request["sha"],
        "ref": request["ref"],
        "run": request["run"],
        "attempt": request["attempt"],
        "event": event,
        "dev": request["dev"],
        "prod": request["prod"],
        "role": role,
        "publisher": publisher,
        "tag": tag_requested,
        "packageId": package_id,
        "compatibilityLine": request["compatibilityLine"],
        "baseVersion": base,
        "packageVersion": package_version,
        "releaseTag": release_tag,
        "releaseTagState": release_tag_state,
        "currentShaTags": current_out,
        "artifactPath": artifact_path,
        "artifactDigest": "",
        "tagStateFingerprint": tag_fingerprint,
        "repositoryVisibility": request["visibility"],
    }
    return {
        "publisher": publisher,
        "tag": "true" if tag_requested else "false",
        "planned-state": encode_state(state),
        "base-version": base,
        "package-version": package_version,
        "release-tag": release_tag,
        "release-tag-state": release_tag_state,
        "tag-state-fingerprint": tag_fingerprint,
        "package-artifact": artifact_path,
    }


def _validate_state(state, role_mode, expect_publisher, allow_empty_digest):
    _check_keys(state, STATE_KEYS, "malformed")
    if state["v"] != SCHEMA_VERSION:
        raise StateError("unknown-version")
    if not REPO.match(state["repo"]) or not HEX40.match(state["sha"]):
        raise StateError("malformed")
    if not REF.match(state["ref"]):
        raise StateError("malformed")
    if not NUMERIC.match(state["run"]) or not NUMERIC.match(state["attempt"]):
        raise StateError("malformed")
    if state["event"] not in EVENTS:
        raise StateError("malformed")
    if not _valid_branch(state["dev"]) or not _valid_branch(state["prod"]):
        raise StateError("malformed")
    if state["dev"] == state["prod"]:
        raise StateError("malformed")
    if state["role"] not in ROLES or state["publisher"] not in PUBLISHERS:
        raise StateError("malformed")
    if state["repositoryVisibility"] not in VISIBILITIES:
        raise StateError("malformed")
    if not PACKAGE_ID.match(state["packageId"]):
        raise StateError("malformed")
    if not COMPAT.match(state["compatibilityLine"]):
        raise StateError("malformed")
    if not STABLE.match(state["baseVersion"]):
        raise StateError("malformed")
    version_match = VERSION.match(state["packageVersion"])
    if not version_match:
        raise StateError("malformed")
    version_base = "{0}.{1}.{2}".format(
        version_match.group(1), version_match.group(2), version_match.group(3)
    )
    if version_base != state["baseVersion"]:
        raise StateError("malformed")
    if state["releaseTag"] != "{0}/v{1}".format(state["packageId"], state["baseVersion"]):
        raise StateError("malformed")
    if state["releaseTagState"] not in TAG_STATES:
        raise StateError("malformed")
    if not isinstance(state["currentShaTags"], list):
        raise StateError("malformed")
    if len(state["currentShaTags"]) > 1:
        raise StateError("malformed")
    for leaf in state["currentShaTags"]:
        if not isinstance(leaf, str) or not TAG_LEAF.match(leaf):
            raise StateError("malformed")
    if state["artifactPath"] != "artifacts/{0}.{1}.nupkg".format(
        state["packageId"], state["packageVersion"]
    ):
        raise StateError("malformed")
    if allow_empty_digest and state["artifactDigest"] == "":
        pass
    elif not HEX64.match(state["artifactDigest"]):
        raise StateError("malformed")
    if not HEX64.match(state["tagStateFingerprint"]):
        raise StateError("malformed")

    if _derive_role(state["ref"], state["dev"], state["prod"]) != state["role"]:
        raise StateError("role-mismatch")

    if state["event"] == "pull_request":
        if state["publisher"] != "none" or state["tag"] is not False:
            raise StateError("malformed")
        if state["role"] not in ("development", "production"):
            raise StateError("role-mismatch")
        if state["releaseTagState"] != "not-applicable" or state["currentShaTags"]:
            raise StateError("malformed")
    else:
        if state["role"] == "none":
            raise StateError("role-mismatch")
        expected_publisher = _route(state["repositoryVisibility"])
        if state["publisher"] != expected_publisher:
            raise StateError("malformed")
        if state["tag"] is not (state["role"] == "production"):
            raise StateError("malformed")
        if state["role"] == "production":
            if state["releaseTagState"] == "present" and len(state["currentShaTags"]) != 1:
                raise StateError("malformed")
            if state["releaseTagState"] == "missing" and state["currentShaTags"]:
                raise StateError("malformed")
        else:
            if state["releaseTagState"] != "not-applicable" or state["currentShaTags"]:
                raise StateError("malformed")

    expected = {
        "repo": os.environ.get("GITHUB_REPOSITORY", ""),
        "sha": os.environ.get("GITHUB_SHA", "").lower(),
        "ref": os.environ.get("GITHUB_REF", ""),
        "run": os.environ.get("GITHUB_RUN_ID", ""),
        "attempt": os.environ.get("GITHUB_RUN_ATTEMPT", ""),
    }
    for key, value in expected.items():
        if value and state[key] != value:
            raise StateError("binding-mismatch")

    if role_mode == "production" and state["role"] != "production":
        raise StateError("role-mismatch")
    if role_mode == "publishable" and state["role"] not in ("development", "production"):
        raise StateError("role-mismatch")

    if expect_publisher == "publishable":
        if state["publisher"] not in ("nuget", "internal"):
            raise StateError("publisher-mismatch")
    elif expect_publisher is not None and state["publisher"] != expect_publisher:
        raise StateError("publisher-mismatch")


def seal(raw_state, digest):
    state = decode_state(raw_state)
    _validate_state(state, None, None, allow_empty_digest=True)
    if not HEX64.match(digest):
        raise StateError("malformed-digest")
    state["artifactDigest"] = digest
    _validate_state(state, None, None, allow_empty_digest=False)
    return encode_state(state)


def fingerprint(document):
    _check_keys(document, {"packageId": str, "tags": list}, "malformed-request")
    if not PACKAGE_ID.match(document["packageId"]):
        raise StateError("malformed-request")
    prefix = "refs/tags/" + document["packageId"] + "/"
    rows = []
    for row in document["tags"]:
        _check_keys(row, {"ref": str, "objectSha": str}, "malformed-request")
        ref = row["ref"]
        if not ref.startswith(prefix):
            raise StateError("foreign-tag")
        if not TAG_LEAF.match(ref[len(prefix):]):
            raise StateError("malformed-tag")
        if not HEX40.match(row["objectSha"]):
            raise StateError("malformed-request")
        rows.append({"ref": ref, "objectSha": row["objectSha"]})
    return fingerprint_rows(rows)


_ENV_KEYS = [
    ("GIZMO_STATE_VERSION", "v"),
    ("GIZMO_REPO", "repo"),
    ("GIZMO_SHA", "sha"),
    ("GIZMO_REF", "ref"),
    ("GIZMO_RUN", "run"),
    ("GIZMO_ATTEMPT", "attempt"),
    ("GIZMO_EVENT", "event"),
    ("GIZMO_DEV", "dev"),
    ("GIZMO_PROD", "prod"),
    ("GIZMO_ROLE", "role"),
    ("GIZMO_PUBLISHER", "publisher"),
    ("GIZMO_TAG", "tag"),
    ("GIZMO_PACKAGE_ID", "packageId"),
    ("GIZMO_COMPATIBILITY_LINE", "compatibilityLine"),
    ("GIZMO_BASE_VERSION", "baseVersion"),
    ("GIZMO_PACKAGE_VERSION", "packageVersion"),
    ("GIZMO_RELEASE_TAG", "releaseTag"),
    ("GIZMO_RELEASE_TAG_STATE", "releaseTagState"),
    ("GIZMO_CURRENT_SHA_TAGS", "currentShaTags"),
    ("GIZMO_ARTIFACT_PATH", "artifactPath"),
    ("GIZMO_ARTIFACT_DIGEST", "artifactDigest"),
    ("GIZMO_TAG_STATE_FINGERPRINT", "tagStateFingerprint"),
    ("GIZMO_REPOSITORY_VISIBILITY", "repositoryVisibility"),
]


def _stringify(value):
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, list):
        return ",".join(value)
    return str(value)


def _emit_env(state, github_env_path):
    shell_lines = []
    raw_lines = []
    for name, key in _ENV_KEYS:
        value = _stringify(state[key])
        shell_lines.append("{0}={1}".format(name, shlex.quote(value)))
        raw_lines.append("{0}={1}".format(name, value))
    if github_env_path:
        with open(github_env_path, "w", encoding="utf-8") as handle:
            handle.write("\n".join(raw_lines) + "\n")
    return "\n".join(shell_lines) + "\n"


def _read_stdin():
    return sys.stdin.read()


def main(argv=None):
    parser = argparse.ArgumentParser(prog="state.py")
    sub = parser.add_subparsers(dest="command", required=True)

    plan_parser = sub.add_parser("plan")
    plan_parser.add_argument("--github-output", default=None)

    seal_parser = sub.add_parser("seal")
    seal_parser.add_argument("--digest", required=True)

    validate_parser = sub.add_parser("validate")
    validate_parser.add_argument(
        "--role", choices=["production", "publishable"], required=True
    )
    validate_parser.add_argument(
        "--expect-publisher",
        choices=["nuget", "internal", "publishable"],
        required=True,
    )
    validate_parser.add_argument("--github-env", default=None)

    fingerprint_parser = sub.add_parser("fingerprint")

    arguments = parser.parse_args(argv)

    try:
        if arguments.command == "plan":
            outputs = plan(_decode_json(_read_stdin()))
            body = "".join("{0}={1}\n".format(key, value) for key, value in outputs.items())
            if arguments.github_output:
                with open(arguments.github_output, "a", encoding="utf-8") as handle:
                    handle.write(body)
            sys.stdout.write(body)
            return 0
        if arguments.command == "seal":
            state = seal(_read_stdin(), arguments.digest)
            sys.stdout.write("state={0}\n".format(state))
            return 0
        if arguments.command == "validate":
            state = decode_state(_read_stdin())
            _validate_state(
                state,
                arguments.role,
                arguments.expect_publisher,
                allow_empty_digest=False,
            )
            sys.stdout.write(_emit_env(state, arguments.github_env))
            return 0
        if arguments.command == "fingerprint":
            sys.stdout.write(fingerprint(_decode_json(_read_stdin())) + "\n")
            return 0
    except StateError as error:
        sys.stderr.write("state verdict: {0}\n".format(error))
        return 1
    except Exception:  # noqa: BLE001 - any surprise must fail closed
        sys.stderr.write("state verdict: malformed\n")
        return 1
    return 1


if __name__ == "__main__":
    sys.exit(main())
