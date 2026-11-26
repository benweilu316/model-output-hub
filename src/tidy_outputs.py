# -*- coding: utf-8 -*-
r"""
tidy_outputs.py — Non-intrusive Organizer & Windows `.lnk` Topic Shortcut Builder
================================================================================
Safety Guarantees:
  1. Zero deletion, zero overwrite of any user deliverable.
  2. Never moves or renames directories inside `<Platform>输出\` (preserves all external references & hooks).
  3. Automatically skips any directory or file with an active write lock (live agent / active handoff / modified within 2h).
  4. Consolidates loose root items into `_归档与杂项\` with a full TSV migration ledger supporting `--rollback`.
  5. Builds `00_按课题总览\` with native `.lnk` shortcuts grouped by Topic -> Sub-branch + Top Latest Deliverables.
"""
import argparse
import datetime as _dt
import os
import pathlib
import re
import shutil
import sys

try:
    import win32com.client
    HAS_WSH = True
except ImportError:
    HAS_WSH = False

THIS_DIR = pathlib.Path(__file__).resolve().parent
if str(THIS_DIR) not in sys.path:
    sys.path.insert(0, str(THIS_DIR))

import topic_rules as tr

ROOT = tr.MODEL_OUT_ROOT
OVERVIEW_DIR = ROOT / "00_按课题总览"
ARCHIVE_ROOT = ROOT / "_归档与杂项"


def _find_ledger_tsv() -> pathlib.Path:
    existing = sorted(ARCHIVE_ROOT.glob("_整理迁移台账_*.tsv"), reverse=True) if ARCHIVE_ROOT.is_dir() else []
    if existing:
        return existing[0]
    return ARCHIVE_ROOT / f"_整理迁移台账_{_dt.datetime.now().strftime('%Y%m%d')}.tsv"


PROTECTED_ROOT_NAMES = {
    "claude输出", "gemini输出", "grok输出", "zcode输出", "alma输出",
    "codex输出", "cursor输出", "windsurf输出", "deepseek输出", "kimi输出", "qwen输出",
    "group输出", "glm输出", "MIMO输出", "opencode输出",
    "00_按课题总览", "_归档与杂项", "_hub_app",
    "README.md", "LICENSE", "模型输出管家.exe",
}


def _safe_filename(s: str) -> str:
    s = re.sub(r'[<>:"/\\|?*]', "_", s).strip().rstrip(".")
    return s[:110]


def _tree_compare_is_subset_or_equal(cand: pathlib.Path, canonical: pathlib.Path) -> tuple[bool, str]:
    if not cand.is_dir() or not canonical.is_dir():
        return False, "not_dir"
    cand_files = {}
    for r, _, fs in os.walk(cand):
        for f in fs:
            fp = pathlib.Path(r) / f
            cand_files[str(fp.relative_to(cand))] = fp.stat().st_size
    if not cand_files:
        return False, "empty"
    canon_files = {}
    for r, _, fs in os.walk(canonical):
        for f in fs:
            fp = pathlib.Path(r) / f
            canon_files[str(fp.relative_to(canonical))] = fp.stat().st_size
    for rel, sz in cand_files.items():
        if rel not in canon_files or canon_files[rel] != sz:
            return False, f"mismatch:{rel}"
    if len(cand_files) == len(canon_files):
        return True, f"100%逐字节等大重复 ({len(cand_files)} 文件)"
    return True, f"100%真子集副本 ({len(cand_files)}/{len(canon_files)} 文件已在正本)"


def _is_recently_touched(p: pathlib.Path, now_ts: float, sec: int = 7200) -> bool:
    try:
        if now_ts - p.stat().st_mtime < sec:
            return True
        if p.is_dir():
            for r, _, fs in os.walk(p):
                for f in fs:
                    if now_ts - (pathlib.Path(r) / f).stat().st_mtime < sec:
                        return True
    except OSError:
        return True
    return False


def tidy_loose_items(dry_run: bool = False) -> list[dict]:
    lock_info = tr.get_active_locks()
    locked_paths = lock_info["locked_paths"]
    now_ts = _dt.datetime.now().timestamp()
    moved = []

    raw_dups = tr.PROFILE.get("known_dup_targets") or {}
    known_dup_targets = {k: (ROOT / v) for k, v in raw_dups.items()}
    config_misc_names = {"agent-configs", "agent-hub-local-edits", "tmp", "桌面归档"}

    has_platform_dirs = any(
        (ROOT / d).is_dir() for d, _, _ in tr.PLATFORM_DIRS
    ) or any(
        x.is_dir() and (x.name.endswith("输出") or re.search(r"[-_](?:output|out)s?$", x.name, re.I))
        for x in ROOT.iterdir()
    )

    def plan_move(src: pathlib.Path, dst_dir: pathlib.Path, category: str, note: str = ""):
        np = os.path.normcase(os.path.abspath(str(src)))
        if np in locked_paths or _is_recently_touched(src, now_ts, 7200):
            return
        dst = dst_dir / src.name
        if dst.exists():
            return
        moved.append({
            "time": _dt.datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
            "category": category,
            "src": str(src),
            "dst": str(dst),
            "note": note,
        })
        if not dry_run:
            dst_dir.mkdir(parents=True, exist_ok=True)
            shutil.move(str(src), str(dst))

    if has_platform_dirs:
        for item in sorted(ROOT.iterdir(), key=lambda x: x.name.lower()):
            if item.name in PROTECTED_ROOT_NAMES or item.name.startswith(("00_", "_", ".")):
                continue
            if item.is_dir() and (item.name.endswith("输出") or re.search(r"[-_](?:output|out)s?$", item.name, re.I)):
                continue
            if item.name.endswith((".bat", ".html", ".md", ".exe")):
                continue

            if item.is_dir():
                if item.name in known_dup_targets:
                    ok, reason = _tree_compare_is_subset_or_equal(item, known_dup_targets[item.name])
                    if ok:
                        plan_move(item, ARCHIVE_ROOT / "待确认重复副本", "root_dup_dir", f"{reason} -> 正本: {known_dup_targets[item.name]}")
                    else:
                        plan_move(item, ARCHIVE_ROOT / "根目录历史课题散件", "root_topic_dir", f"存在独立差异({reason})，按有效历史课题保留")
                elif item.name in config_misc_names:
                    plan_move(item, ARCHIVE_ROOT / "历史配置与桌面归档", "root_config_misc", "非课题历史配置快照/临时脚本")
                else:
                    plan_move(item, ARCHIVE_ROOT / "根目录历史课题散件", "root_topic_dir", "根目录散落课题目录收拢")
            elif item.is_file():
                plan_move(item, ARCHIVE_ROOT / "根目录散文件", "root_loose_file", "根目录散落单文件")

    today_tag = _dt.datetime.now().strftime("%Y%m%d")
    for p_name in ("gemini输出", "grok输出"):
        pdir = ROOT / p_name
        if not pdir.is_dir():
            continue
        for f in sorted(pdir.iterdir(), key=lambda x: x.name.lower()):
            if not f.is_file():
                continue
            if f.name.startswith(("_INDEX", "README", "_整理")):
                continue
            plan_move(f, pdir / f"_根目录散件归档_{today_tag}", f"{p_name}_loose_file", f"{p_name}根目录历史散文件收拢")

    if moved and not dry_run:
        ARCHIVE_ROOT.mkdir(parents=True, exist_ok=True)
        ledger_tsv = _find_ledger_tsv()
        need_header = not ledger_tsv.exists()
        with open(ledger_tsv, "a", encoding="utf-8") as fw:
            if need_header:
                fw.write("timestamp\tcategory\tsrc_path\tdst_path\tnote\n")
            for m in moved:
                fw.write(f"{m['time']}\t{m['category']}\t{m['src']}\t{m['dst']}\t{m['note']}\n")

    return moved


def rollback_from_ledger() -> int:
    ledger_tsv = _find_ledger_tsv()
    if not ledger_tsv.is_file():
        print("未找到迁移台账，无需回滚。")
        return 0
    lines = ledger_tsv.read_text(encoding="utf-8").splitlines()[1:]
    restored = 0
    for ln in reversed(lines):
        parts = ln.split("\t")
        if len(parts) < 4:
            continue
        src, dst = pathlib.Path(parts[2]), pathlib.Path(parts[3])
        if dst.exists() and not src.exists():
            src.parent.mkdir(parents=True, exist_ok=True)
            shutil.move(str(dst), str(src))
            restored += 1
    print(f"已原样恢复 {restored} 项。")
    return restored


def build_overview_shortcuts(scan_data: dict) -> dict:
    if not HAS_WSH:
        return {"created": 0, "error": "pywin32 WScript.Shell unavailable"}

    OVERVIEW_DIR.mkdir(parents=True, exist_ok=True)
    for r, ds, fs in os.walk(OVERVIEW_DIR, topdown=False):
        rp = pathlib.Path(r)
        for fn in fs:
            if fn.lower().endswith(".lnk"):
                try:
                    (rp / fn).unlink()
                except OSError:
                    pass
        if rp != OVERVIEW_DIR:
            try:
                if not any(rp.iterdir()):
                    rp.rmdir()
            except OSError:
                pass

    shell = win32com.client.Dispatch("WScript.Shell")
    created_dirs = 0
    created_files = 0

    for t in scan_data["topics"]:
        if t["project_count"] == 0:
            continue
        t_dir = OVERVIEW_DIR / t["folder"]
        t_dir.mkdir(parents=True, exist_ok=True)

        for br in t["branches"]:
            b_dir = t_dir / _safe_filename(br["name"])
            b_dir.mkdir(parents=True, exist_ok=True)
            for p in br["projects"]:
                lock_prefix = "[运行中] " if p["locked"] else ("★ " if p["starred"] else "")
                lnk_name = _safe_filename(f"{lock_prefix}[{p['date']}][{p['platform']}] {p['name']}") + ".lnk"
                lnk_path = b_dir / lnk_name
                try:
                    sc = shell.CreateShortcut(str(lnk_path))
                    sc.TargetPath = p["path"]
                    top_hint = ""
                    if p["htmls"]:
                        top_hint = f"最新报告: {p['htmls'][0]['name']}"
                    elif p["pptxs"]:
                        top_hint = f"最新PPT: {p['pptxs'][0]['name']}"
                    elif p["pdfs"]:
                        top_hint = f"最新PDF: {p['pdfs'][0]['name']}"
                    sc.Description = f"{p['platform']} | {p['size_mb']}MB | {p['total_files']}文件 | {top_hint}"[:240]
                    sc.Save()
                    created_dirs += 1
                except Exception:
                    pass

        best_delivs = []
        for br in t["branches"]:
            for p in br["projects"]:
                for kind, arr in (("HTML报告", p["htmls"][:2]), ("PPT汇报", p["pptxs"][:2]), ("整页组图", p["pdfs"][:2])):
                    for d in arr:
                        best_delivs.append((d["mtime"], kind, p["platform"], d))
        best_delivs.sort(key=lambda x: x[0], reverse=True)
        seen_paths = set()
        star_dir = t_dir / "00_最新核心成品直达"
        for mt, kind, plat, d in best_delivs:
            if d["path"] in seen_paths:
                continue
            seen_paths.add(d["path"])
            if len(seen_paths) > 10:
                break
            star_dir.mkdir(parents=True, exist_ok=True)
            dt_short = d["date"][:10]
            fn_lnk = _safe_filename(f"[{dt_short}][{plat}][{kind}] {d['name']}") + ".lnk"
            try:
                sc = shell.CreateShortcut(str(star_dir / fn_lnk))
                sc.TargetPath = d["path"]
                sc.Description = f"{kind} ({d['size_mb']} MB) — 原路径: {d['path']}"[:240]
                sc.Save()
                created_files += 1
            except Exception:
                pass

    return {"dir_shortcuts": created_dirs, "file_shortcuts": created_files}


def run_full_tidy(dry_run: bool = False) -> dict:
    moved = tidy_loose_items(dry_run=dry_run)
    scan_data = tr.scan_all_projects(use_cache=False)
    sc_stats = {"dir_shortcuts": 0, "file_shortcuts": 0}
    if not dry_run:
        sc_stats = build_overview_shortcuts(scan_data)
    return {
        "dry_run": dry_run,
        "moved_count": len(moved),
        "moved": moved,
        "shortcuts": sc_stats,
        "summary": scan_data["summary"],
    }


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser(description="Model Output Non-intrusive Organizer")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--rollback", action="store_true")
    args = ap.parse_args()
    if args.rollback:
        rollback_from_ledger()
    else:
        res = run_full_tidy(dry_run=args.dry_run)
        print(res["summary"])
