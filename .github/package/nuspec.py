#!/usr/bin/env python3
"""Structurally verify NuGet nuspec provenance.

The nuspec is untrusted, so it is parsed as namespace-aware XML rather than
scanned with substring or regex extraction that a decoy element could satisfy.
This module is the single checked-in source shared by the NuGet and internal
publishers; the internal path uses it instead of regex commit extraction.
"""

import os
import re
import sys
import xml.parsers.expat as expat

_NAMESPACE_SEPARATOR = "\x1f"
# dotnet pack emits the nuspec schema namespace; a root outside it is a decoy.
_NUSPEC_NAMESPACE = re.compile(
    r"^http://schemas\.microsoft\.com/packaging/[0-9]{4}/[0-9]{2}/nuspec\.xsd$"
)
_FULL_COMMIT = re.compile(r"^[0-9a-fA-F]{40}$")


class _Malformed(Exception):
    """XML is not well-formed or carries a forbidden DTD/entity construct."""


class _Ambiguous(Exception):
    """A required element is absent or appears more than once."""


def _split(expanded_name):
    if _NAMESPACE_SEPARATOR in expanded_name:
        namespace, local = expanded_name.split(_NAMESPACE_SEPARATOR, 1)
        return namespace, local
    return "", expanded_name


def _parse(data):
    try:
        text = data.decode("utf-8-sig")
    except UnicodeDecodeError as error:
        raise _Malformed("not UTF-8") from error

    # Entity handlers reject before any entity can be expanded or resolved.
    parser = expat.ParserCreate(namespace_separator=_NAMESPACE_SEPARATOR)
    parser.SetParamEntityParsing(expat.XML_PARAM_ENTITY_PARSING_NEVER)
    root = None
    stack = []

    def reject(*_arguments):
        raise _Malformed("DTD or entity declaration")

    def start(name, attributes):
        nonlocal root
        node = [name, attributes, [], []]
        if stack:
            stack[-1][2].append(node)
        else:
            if root is not None:
                raise _Malformed("multiple root elements")
            root = node
        stack.append(node)

    def end(_name):
        stack.pop()

    def characters(content):
        if stack:
            stack[-1][3].append(content)

    parser.StartDoctypeDeclHandler = reject
    parser.EntityDeclHandler = reject
    parser.ExternalEntityRefHandler = reject
    parser.StartElementHandler = start
    parser.EndElementHandler = end
    parser.CharacterDataHandler = characters

    try:
        parser.Parse(text, True)
    except _Malformed:
        raise
    except expat.ExpatError as error:
        raise _Malformed(str(error)) from error

    if root is None:
        raise _Malformed("empty document")
    return root


def _text(node):
    return "".join(node[3]).strip()


def _sole_child(parent, namespace, local):
    # Only direct children count, so a nested decoy id/repository cannot satisfy this.
    matches = [child for child in parent[2] if _split(child[0])[1] == local]
    if len(matches) != 1:
        raise _Ambiguous(local)
    if _split(matches[0][0]) != (namespace, local):
        raise _Ambiguous(local + " namespace")
    return matches[0]


def _verdict(data):
    root = _parse(data)
    namespace, root_local = _split(root[0])
    if root_local != "package" or not _NUSPEC_NAMESPACE.match(namespace):
        raise _Malformed("unexpected root element")

    metadata = _sole_child(root, namespace, "metadata")
    identifier = _text(_sole_child(metadata, namespace, "id"))
    version = _text(_sole_child(metadata, namespace, "version"))
    repository = _sole_child(metadata, namespace, "repository")
    commit = repository[1].get("commit", "")

    expected_id = os.environ.get("EXPECTED_PACKAGE_ID", "").strip()
    expected_version = os.environ.get("PACKAGE_VERSION", "").strip()
    expected_commit = os.environ.get("GITHUB_SHA", "").strip().lower()
    if not expected_id or not expected_version or not _FULL_COMMIT.match(expected_commit):
        raise _Malformed("expected identity unavailable")

    if not identifier:
        raise _Ambiguous("id")
    if identifier.casefold() != expected_id.casefold():
        return "id"
    if not version:
        raise _Ambiguous("version")
    if version.casefold() != expected_version.casefold():
        return "version"
    if not _FULL_COMMIT.match(commit):
        raise _Ambiguous("repository commit")
    if commit.lower() != expected_commit:
        return "commit"
    return "ok"


def main():
    try:
        token = _verdict(sys.stdin.buffer.read())
    except (_Malformed, _Ambiguous):
        token = "malformed"
    except Exception:  # noqa: BLE001 - any parser surprise must fail closed
        token = "malformed"

    if token != "ok":
        sys.stderr.write("nuspec provenance verdict: %s\n" % token)
    sys.stdout.write(token)
    return 0 if token == "ok" else 1


if __name__ == "__main__":
    sys.exit(main())
