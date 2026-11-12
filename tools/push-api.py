#!/usr/bin/env python3
"""Push the current working tree to GitHub using only the REST API.

Why: some networks cannot reach github.com:443 (git push fails with CONNECT 502)
while api.github.com is reachable. This script builds blobs / tree / commit via
the Git Data API, so binary files (the release exe, if ever needed) also work.

Token: GITHUB_TOKEN / GH_TOKEN env var, `gh auth token`, or `git credential fill`.
The local git history is NOT used as a parent: the remote branch head is always
authoritative. That also means a forked local history (different commit SHAs)
never causes a non-fast-forward rejection.

Usage:
  python tools/push-api.py                      # commit message from local git log
  python tools/push-api.py -m "fix something"
  python tools/push-api.py --repo owner/name --branch main
"""

import argparse
import base64
import json
import os
import shutil
import subprocess
import sys
import urllib.error
import urllib.request

API = "https://api.github.com"


def get_token():
    for name in ("GITHUB_TOKEN", "GH_TOKEN"):
        if os.environ.get(name):
            return os.environ[name].strip()
    # Git Credential Manager usually holds a classic PAT, which has full Git Data
    # API write access. `gh auth token` can return an OAuth/app token that is
    # accepted for blobs but rejected (404) for trees/commits, so try it last.
    p = subprocess.run(
        ["git", "credential", "fill"],
        input="protocol=https\nhost=github.com\n\n",
        capture_output=True, text=True,
    )
    for line in p.stdout.splitlines():
        if line.startswith("password=") and line[len("password="):].strip():
            return line[len("password="):].strip()
    if shutil.which("gh"):
        p = subprocess.run(["gh", "auth", "token"], capture_output=True, text=True)
        if p.returncode == 0 and p.stdout.strip():
            return p.stdout.strip()
    print("ERROR: no GitHub token found. Set GITHUB_TOKEN or run `gh auth login`.", file=sys.stderr)
    sys.exit(2)


TOKEN = None


def api(method, path, payload=None, allow_404=False):
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8") if payload is not None else None

    # `gh` handles corporate proxies / TLS far better than urllib (urllib POST gets
    # CONNECT 502 on some proxies). Prefer it whenever available.
    if shutil.which("gh"):
        env = dict(os.environ)
        env["GH_TOKEN"] = TOKEN
        cmd = ["gh", "api", "--method", method, path]
        if data is not None:
            cmd += ["--input", "-"]
        p = subprocess.run(cmd, input=data.decode("utf-8") if data else None,
                           capture_output=True, text=True, encoding="utf-8", env=env)
        if p.returncode == 0:
            return json.loads(p.stdout) if p.stdout.strip() else {}
        if allow_404 and ("404" in p.stderr or "Not Found" in p.stderr):
            return None
        if "git/trees" in path and ("404" in p.stderr or "Not Found" in p.stderr):
            print("HINT: 404 on git/trees usually means the token lacks the "
                  "'workflow' scope and the tree contains .github/workflows/*.yml. "
                  "Run `gh auth refresh -h github.com -s workflow` or use a PAT "
                  "with repo+workflow scopes.", file=sys.stderr)
        print("ERROR %s %s -> %s" % (method, path, p.stderr.strip()[:400]), file=sys.stderr)
        sys.exit(1)

    req = urllib.request.Request(
        API + path, data=data, method=method,
        headers={
            "Authorization": "Bearer " + TOKEN,
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
            "Content-Type": "application/json",
            "User-Agent": "SeerHover-push-api",
        },
    )
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            body = r.read()
            return json.loads(body) if body.strip() else {}
    except urllib.error.HTTPError as e:
        if allow_404 and e.code == 404:
            return None
        print("ERROR %s %s -> %s %s" % (method, path, e.code, e.read().decode()[:400]), file=sys.stderr)
        sys.exit(1)
    except Exception as e:
        print("ERROR %s %s -> %s" % (method, path, e), file=sys.stderr)
        print("If a proxy blocks raw HTTPS, install GitHub CLI (`gh`) and retry.", file=sys.stderr)
        sys.exit(1)


def git(*args):
    return subprocess.run(["git"] + list(args), capture_output=True, text=True,
                          encoding="utf-8").stdout


def main():
    global TOKEN
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", default="lza0312/SeerHover")
    ap.add_argument("--branch", default="main")
    ap.add_argument("-m", "--message", default=None)
    ap.add_argument("--token", default=None,
                    help="explicit token; useful right after `gh auth refresh -s workflow`")
    args = ap.parse_args()

    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    os.chdir(root)
    TOKEN = args.token or get_token()

    files = [f.replace("\\", "/") for f in git("ls-files").split()]
    if not files:
        print("ERROR: no tracked files (run from inside the repository).", file=sys.stderr)
        sys.exit(1)

    repo = "repos/" + args.repo
    # GET a single reference uses singular /git/ref/{ref}, but UPDATE requires
    # plural /git/refs/{ref}. PATCH on the singular path returns 404 even with a
    # valid token -- a silent failure that leaves blobs/tree/commit orphaned.
    get_ref = repo + "/git/ref/heads/" + args.branch
    patch_ref = repo + "/git/refs/heads/" + args.branch

    # An empty repository cannot accept blobs (409). Seed it with one placeholder
    # commit, then force the real commit in as a single root commit.
    ref = api("GET", get_ref, allow_404=True)
    head = ref["object"]["sha"] if ref else None
    if head is None:
        api("PUT", repo + "/contents/.gitkeep", {"message": "init", "content": ""})
        api("PATCH", repo, {"default_branch": args.branch})
        ref = api("GET", get_ref, allow_404=True)
        head = ref["object"]["sha"] if ref else None

    print("remote head: %s" % (head[:7] if head else "(empty repo)"))
    entries = []
    for rel in files:
        with open(rel, "rb") as f:
            data = f.read()
        sha = api("POST", repo + "/git/blobs", {
            "content": base64.b64encode(data).decode(), "encoding": "base64"})["sha"]
        entries.append({"path": rel, "mode": "100644", "type": "blob", "sha": sha})
        print("  %-42s %8d B" % (rel, len(data)))

    tree = api("POST", repo + "/git/trees", {"tree": entries})["sha"]
    message = args.message or git("log", "-1", "--pretty=%B").strip() or "update"
    commit = api("POST", repo + "/git/commits",
                 {"message": message, "tree": tree,
                  "parents": [head] if head else []})["sha"]
    if head:
        api("PATCH", patch_ref, {"sha": commit, "force": False})
    else:
        api("POST", repo + "/git/refs",
            {"ref": "refs/heads/" + args.branch, "sha": commit})
    print("pushed: %s" % commit, flush=True)


if __name__ == "__main__":
    main()
