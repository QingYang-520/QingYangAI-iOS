# -*- coding: utf-8 -*-
r"""
下载 GitHub Actions 产出的 iOS IPA，并放到 Z:\ 根目录。

为什么需要它
------------
GitHub Actions 跑在云端 Mac，产物 IPA 作为 Artifact 存在 GitHub 云端，
够不到本机的 Z 盘（\\qingyang\NAS）。所以分两步：
  1. Actions 打包 → IPA 作为 Artifact（工作流 ios-build.yml）
  2. 本脚本下载最新 Artifact 里的 IPA → 复制到 Z:\ 根目录

网络说明
--------
本机只能连 api.github.com，连不上 github.com / codeload。
Artifact 下载链接返回的是 objects.githubusercontent.com 的重定向 ——
如果那个域名也超时，脚本会明确报错并给出「手动下载」兜底路径。

用法
----
    python 下载IPA并放Z盘.py            # 试运行：列出最新 run / artifact / 目标路径
    python 下载IPA并放Z盘.py --go        # 真下载并放到 Z:\ 根目录
    python 下载IPA并放Z盘.py --go --run 123456789   # 指定 run id

token 来源（按优先级）：
  1. 环境变量 GITHUB_TOKEN / GH_TOKEN
  2. 同目录 _tok.txt
  3. 任意项目 .git/config 的 remote.origin.url 里 ://user:TOKEN@ 的 TOKEN
"""
import argparse
import os
import re
import shutil
import subprocess
import sys
import urllib.request
import urllib.error
import json
import zipfile
import tempfile

REPO = "QingYang-520/QingYangAI-Agent"
WORKFLOW = "ios-build.yml"
Z_ROOT = r"Z:\ "
Z_ROOT = Z_ROOT.strip()          # "Z:\"
ARTIFACT_NAME = "qingyang-ios-ipa"


def get_token():
    for k in ("GITHUB_TOKEN", "GH_TOKEN"):
        if os.environ.get(k):
            return os.environ[k].strip()
    here = os.path.dirname(os.path.abspath(__file__))
    tokfile = os.path.join(here, "_tok.txt")
    if os.path.exists(tokfile):
        t = open(tokfile, encoding="utf-8").read().strip()
        if t:
            return t
    # 扫附近项目的 .git/config
    for base in (os.path.dirname(here), r"D:\project", os.path.expanduser("~")):
        for root, dirs, files in os.walk(base):
            if ".git" in dirs:
                cfg = os.path.join(root, ".git", "config")
                if os.path.exists(cfg):
                    txt = open(cfg, encoding="utf-8", errors="replace").read()
                    m = re.search(r"://[^:/\s]+:([^@/\s]+)@github\.com", txt)
                    if m:
                        return m.group(1)
            dirs[:] = [d for d in dirs if d not in ("android-sdk", "node_modules", "bin", "obj")]
    raise RuntimeError("找不到 GitHub token（设 GITHUB_TOKEN 环境变量或放 _tok.txt）")


def api(tok, method, path, raw=False):
    url = "https://api.github.com/repos/%s%s" % (REPO, path)
    req = urllib.request.Request(url, method=method, headers={
        "Authorization": "Bearer " + tok,
        "Accept": "application/vnd.github+json",
        "User-Agent": "qingyang-ios-downloader",
        "X-GitHub-Api-Version": "2022-11-28",
    })
    try:
        with urllib.request.urlopen(req, timeout=90) as r:
            data = r.read()
            return data if raw else (json.loads(data) if data else {})
    except urllib.error.HTTPError as e:
        raise RuntimeError("API %s %s -> HTTP %d\n%s" % (method, path, e.code,
                           e.read().decode("utf-8", "replace")[:400]))


def list_runs(tok, limit=5):
    d = api(tok, "GET", "/actions/runs?per_page=%d" % limit)
    return [r for r in d.get("workflow_runs", [])
            if r.get("name", "").startswith("打包 iOS")]


def list_artifacts(tok, run_id):
    d = api(tok, "GET", "/actions/runs/%d/artifacts" % run_id)
    return d.get("artifacts", [])


def download_artifact(tok, artifact, dest_dir):
    # 拿下载链接（返回 302 → objects.githubusercontent.com）
    url = artifact["archive_download_url"]
    req = urllib.request.Request(url, headers={
        "Authorization": "Bearer " + tok,
        "User-Agent": "qingyang-ios-downloader",
    })
    try:
        with urllib.request.urlopen(req, timeout=300) as r:
            data = r.read()
    except urllib.error.HTTPError as e:
        raise RuntimeError("下载 artifact 失败 HTTP %d" % e.code)
    except Exception as e:
        raise RuntimeError("下载 artifact 失败（可能 objects.githubusercontent.com 超时）：%s" % e)
    tmpzip = os.path.join(dest_dir, "artifact.zip")
    with open(tmpzip, "wb") as f:
        f.write(data)
    with zipfile.ZipFile(tmpzip) as z:
        names = [n for n in z.namelist() if n.lower().endswith(".ipa")]
        if not names:
            raise RuntimeError("artifact 里没有 .ipa 文件")
        extracted = []
        for n in names:
            out = os.path.join(dest_dir, os.path.basename(n))
            with z.open(n) as src, open(out, "wb") as dst:
                shutil.copyfileobj(src, dst)
            extracted.append(out)
    os.remove(tmpzip)
    return extracted


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--go", action="store_true", help="真下载并放 Z 盘")
    ap.add_argument("--run", type=int, help="指定 run id")
    args = ap.parse_args()

    tok = get_token()
    if args.run:
        run_id = args.run
        print("使用指定 run id: %d" % run_id)
    else:
        runs = list_runs(tok)
        if not runs:
            print("没有找到 iOS 打包的 workflow run（先去 Actions 手动触发一次）")
            return
        runs.sort(key=lambda r: r.get("created_at", ""), reverse=True)
        run = runs[0]
        run_id = run["id"]
        print("最新 run: #%s  status=%s  created=%s" %
              (run.get("run_number"), run.get("status"), run.get("created_at")))

    arts = list_artifacts(tok, run_id)
    arts = [a for a in arts if a.get("name") == ARTIFACT_NAME]
    if not arts:
        print("该 run 没有名为 %s 的 artifact（可能还在打包，或打包失败）" % ARTIFACT_NAME)
        return
    a = arts[0]
    print("artifact: %s  size=%.1f MB" % (a["name"], a.get("size_in_bytes", 0) / 1048576))

    if not args.go:
        print("[试运行] 目标：%s\\<IPA 文件名>" % Z_ROOT)
        print("确认无误后加 --go 真正下载并放到 Z 盘根目录。")
        return

    if not os.path.exists(Z_ROOT):
        raise RuntimeError("Z 盘不可用：%s" % Z_ROOT)

    with tempfile.TemporaryDirectory() as td:
        ipas = download_artifact(tok, a, td)
        for p in ipas:
            dst = os.path.join(Z_ROOT, os.path.basename(p))
            shutil.copyfile(p, dst)
            print("已放到 Z 盘：%s  [%.1f MB]" % (dst, os.path.getsize(dst) / 1048576))


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        print("错误：" + str(e), file=sys.stderr)
        sys.exit(1)
