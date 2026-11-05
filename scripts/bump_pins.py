#!/usr/bin/env python3
"""Re-pin the releases the mpv-AnimeJaNai assembler downloads: scripts/bump_pins.py <source> <tag>.

Sources (constants in BuildMpvUpscale2xAnimeJaNai/Program.cs):
  libaji        the-database/animejanai-inference release: AjiVersion
  mpv-winbuild  the-database/mpv-winbuild release: MpvForkVersion, plus MpvForkBuildDate and
                MpvForkGitHash read from its archive filenames
  mpv-linux     the-database/mpv Linux bundle release: MpvForkLinuxVersion
  manager       the-database/AnimeJaNaiManager release: ManagerVersion

None of these carry a SHA-256 pin; each release is checked for every asset the assembler fetches
from it. .github/workflows/bump-pins.yml runs this when an upstream release sends a
repository_dispatch, then opens a PR; it also runs by hand. --out DIR writes the PR title and
body. Standard library only. docs/RELEASE.md has the release order.
"""
import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
UA = "janai-bump-pins"
CHECKED = []  # assets confirmed on the release (downloaded or not)


class Fail(Exception):
    pass


def gh(path):
    """GET api.github.com/<path> (GH_TOKEN, when set, only raises the rate limit)."""
    headers = {"Accept": "application/vnd.github+json", "User-Agent": UA}
    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    if token:
        headers["Authorization"] = f"Bearer {token}"
    req = urllib.request.Request(f"https://api.github.com/{path}", headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return json.load(r)
    except urllib.error.HTTPError as e:
        if e.code == 404:
            return None
        raise


def release(repo, tag):
    rel = gh(f"repos/{repo}/releases/tags/{tag}")
    if rel is None:
        raise Fail(f"{repo} has no published release {tag}")
    if rel.get("draft"):
        raise Fail(f"{repo} {tag} is still a draft; its assets 404 for the builds")
    return rel


def asset(rel, name):
    for a in rel["assets"]:
        if a["name"] == name:
            CHECKED.append(name)
            return a
    have = ", ".join(a["name"] for a in rel["assets"]) or "none"
    raise Fail(f"{name} isn't on {rel['tag_name']} (it has: {have}). Upload it, then re-run.")


def digest(a):
    """Download a release asset; return (sha256, size). Cross-checks GitHub's recorded digest."""
    print(f"  downloading {a['name']} ({a['size']:,} bytes)", flush=True)
    req = urllib.request.Request(a["browser_download_url"], headers={"User-Agent": UA})
    h, n = hashlib.sha256(), 0
    with urllib.request.urlopen(req, timeout=600) as r:
        while chunk := r.read(1 << 20):
            h.update(chunk)
            n += len(chunk)
    sha = h.hexdigest()
    if n != a["size"]:
        raise Fail(f"{a['name']}: downloaded {n} bytes, the release says {a['size']}")
    if a.get("digest") and a["digest"] != f"sha256:{sha}":
        raise Fail(f"{a['name']}: SHA-256 {sha} doesn't match GitHub's {a['digest']}")
    return sha, n


def version_key(tag):
    """Comparable key for v1.2.3 / 1.2.3 / runtime-4 / 2026-10-24-<hash> tags."""
    m = re.match(r"^(\d{4})-(\d{2})-(\d{2})-", tag)
    if m:
        return tuple(int(x) for x in m.groups())
    return tuple(int(x) for x in re.findall(r"\d+", tag))


class File:
    """One file's text, edited in place by labelled one-match substitutions. Keeps the file's
    bytes otherwise (BOM, CRLF)."""

    def __init__(self, rel, changes):
        self.rel = rel
        self.path = ROOT / rel
        self.orig = self.text = self.path.read_bytes().decode("utf-8")
        self.changes = changes

    def _match(self, label, pattern):
        found = list(re.finditer(pattern, self.text, re.M))
        if len(found) != 1:
            raise Fail(f"{label}: expected one match of {pattern!r} in {self.rel}, found {len(found)}")
        return found[0]

    def get(self, label, pattern):
        return self._match(label, pattern).group(1)

    def set(self, label, pattern, value):
        """`pattern`'s group 1 is the pinned value."""
        m = self._match(label, pattern)
        old = m.group(1)
        if old != value:
            self.text = self.text[: m.start(1)] + value + self.text[m.end(1):]
        self.changes.append((self.rel, label, old, value))
        return old

    def save(self):
        if self.text != self.orig:
            self.path.write_bytes(self.text.encode("utf-8"))


def guard_downgrade(what, old, new, allow):
    if version_key(new) < version_key(old) and not allow:
        raise Fail(f"{what}: {new} is older than the pinned {old} (pass --allow-downgrade to pin it anyway)")


def rust_int(n):
    return f"{n:_}"


def run(sources, repo_name):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("source", choices=sorted(sources))
    p.add_argument("tag", help="the upstream release tag (yosh-image: any yosh ref)")
    p.add_argument("--out", type=Path, help="write title.txt and body.md here when something changed")
    p.add_argument("--allow-downgrade", action="store_true")
    a = p.parse_args()
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,99}", a.tag):
        sys.exit(f"error: not a tag: {a.tag!r}")
    changes = []
    try:
        files, title, notes = sources[a.source](a.tag, changes, a.allow_downgrade)
    except Fail as e:
        sys.exit(f"error: {e}")
    for f in files:
        f.save()
    edited = [c for c in changes if c[2] != c[3]]
    for rel, label, old, new in changes:
        print(f"{'changed' if old != new else 'same   '}  {rel}: {label}: {old} -> {new}")
    if not edited:
        print(f"{repo_name} already pins {a.source} {a.tag}; nothing to do")
        return
    if a.out:
        a.out.mkdir(parents=True, exist_ok=True)
        (a.out / "title.txt").write_text(title + "\n", encoding="utf-8")
        rows = "\n".join(f"| `{rel}` | {label} | `{old}` | `{new}` |" for rel, label, old, new in edited)
        checked = ""
        if CHECKED:
            checked = (" Assets confirmed on the release: " + ", ".join(f"`{n}`" for n in CHECKED)
                       + ". Every SHA-256 and size above was computed from the downloaded file.")
        body = (
            f"{notes}\n\n| File | Pin | Old | New |\n|---|---|---|---|\n{rows}\n\n"
            f"Opened by `.github/workflows/bump-pins.yml` (`scripts/bump_pins.py {a.source} {a.tag}`)."
            f"{checked} A newer release of the same source updates this PR instead of opening "
            "another. Review and merge by hand.\n"
        )
        (a.out / "body.md").write_text(body, encoding="utf-8")


PROGRAM = "BuildMpvUpscale2xAnimeJaNai/Program.cs"


def const(name, value=r"[^\"]+"):
    return rf'const string {name}\s*=\s*"({value})";'


def bump_libaji(tag, changes, allow_downgrade):
    if not re.fullmatch(r"v\d+\.\d+\.\d+", tag):
        raise Fail(f"{tag} isn't a libaji release tag (vX.Y.Z)")
    f = File(PROGRAM, changes)
    guard_downgrade("AjiVersion", f.get("AjiVersion", const("AjiVersion")), tag, allow_downgrade)
    rel = release("the-database/animejanai-inference", tag)
    for name in ("aji-windows-x64.zip", "aji-linux-x64.tar.zst"):
        asset(rel, name)
    f.set("AjiVersion", const("AjiVersion"), tag)
    notes = (
        f"Pins [libaji {tag}]({rel['html_url']}). Update the comment after `AjiVersion` by hand. If "
        "`include/aji.h`'s `AJI_API_VERSION` changed, this needs an mpv fork build with the matching "
        "filter as well (`MpvForkVersion` / `MpvForkLinuxVersion`); if the TensorRT major changed, "
        "`TrtVersion` too. To test it, run the assembler (or `Deploy`) from this branch and play a "
        "clip with upscaling on."
    )
    return [f], f"Bump libaji to {tag}", notes


def bump_winbuild(tag, changes, allow_downgrade):
    if not re.fullmatch(r"\d{4}-\d{2}-\d{2}-[0-9a-f]{7,40}", tag):
        raise Fail(f"{tag} isn't an mpv-winbuild release tag (<yyyy-mm-dd>-<hash>)")
    f = File(PROGRAM, changes)
    guard_downgrade("MpvForkVersion", f.get("MpvForkVersion", const("MpvForkVersion")), tag, allow_downgrade)
    rel = release("the-database/mpv-winbuild", tag)
    dev = [m for a in rel["assets"] if (m := re.fullmatch(r"mpv-dev-x86_64-(\d{8})-git-([0-9a-f]+)\.7z", a["name"]))]
    if len(dev) != 1:
        raise Fail(f"{tag} should carry one mpv-dev-x86_64-<date>-git-<hash>.7z, found {len(dev)}")
    date, githash = dev[0].groups()
    asset(rel, dev[0].group(0))
    asset(rel, f"mpv-x86_64-{date}-git-{githash}.7z")
    f.set("MpvForkVersion", const("MpvForkVersion"), tag)
    f.set("MpvForkBuildDate", const("MpvForkBuildDate"), date)
    f.set("MpvForkGitHash", const("MpvForkGitHash"), githash)
    notes = (
        f"Pins the Windows mpv fork build [{tag}]({rel['html_url']}). The Linux bundle "
        "(`MpvForkLinuxVersion`) is its own release and PR; ship both from the same fork commit. "
        "To test it, run the assembler (or `Deploy`) from this branch and play a clip with upscaling on."
    )
    return [f], f"Bump the Windows mpv fork build to {tag}", notes


def bump_mpv_linux(tag, changes, allow_downgrade):
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*", tag):
        raise Fail(f"{tag} isn't a release tag")
    f = File(PROGRAM, changes)
    guard_downgrade("MpvForkLinuxVersion", f.get("MpvForkLinuxVersion", const("MpvForkLinuxVersion")),
                    tag, allow_downgrade)
    rel = release("the-database/mpv", tag)
    asset(rel, f"mpv-linux-x64-{tag}.tar.zst")
    f.set("MpvForkLinuxVersion", const("MpvForkLinuxVersion"), tag)
    notes = (
        f"Pins the Linux mpv fork bundle [{tag}]({rel['html_url']}). The Windows build "
        "(`MpvForkVersion`) is its own release and PR; ship both from the same fork commit, and update "
        "the comment above `MpvForkLinuxVersion` if it names the commit."
    )
    return [f], f"Bump the Linux mpv fork bundle to {tag}", notes


def bump_manager(tag, changes, allow_downgrade):
    if not re.fullmatch(r"\d+\.\d+\.\d+", tag):
        raise Fail(f"{tag} isn't an AnimeJaNai Manager release tag (X.Y.Z)")
    f = File(PROGRAM, changes)
    guard_downgrade("ManagerVersion", f.get("ManagerVersion", const("ManagerVersion")), tag, allow_downgrade)
    rel = release("the-database/AnimeJaNaiManager", tag)
    for name in ("AnimeJaNaiManager-portable-x64.zip", "AnimeJaNaiManager-portable-linux-x64.tar.zst"):
        asset(rel, name)
    f.set("ManagerVersion", const("ManagerVersion"), tag)
    notes = (
        f"Pins [AnimeJaNai Manager {tag}]({rel['html_url']}). Update the comment after "
        "`ManagerVersion` by hand. To test it, run the assembler from this branch and open the Manager "
        "from the built tree."
    )
    return [f], f"Bump AnimeJaNai Manager to {tag}", notes


SOURCES = {"libaji": bump_libaji, "mpv-winbuild": bump_winbuild, "mpv-linux": bump_mpv_linux,
           "manager": bump_manager}

if __name__ == "__main__":
    run(SOURCES, "mpv-AnimeJaNai")
