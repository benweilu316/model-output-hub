# -*- coding: utf-8 -*-
"""
topic_rules.py — Universal AI Output Scanner, Physical A4 vs Single-Panel PDF Classifier,
Figure-Number Parser (`Fig 1..N / Fig S1..N / Table / GA`), and Live Session Guard.
"""
import datetime as _dt
import glob
import json
import os
import pathlib
import re
import shutil
import subprocess
import time

try:
    import fitz as _fitz
    HAS_FITZ = True
except ImportError:
    HAS_FITZ = False

HUB_APP_DIR = pathlib.Path(__file__).resolve().parent
MODEL_OUT_ROOT = pathlib.Path(os.environ.get("MODEL_OUTPUT_ROOT") or HUB_APP_DIR.parent)
USER_META_FILE = HUB_APP_DIR / "user_meta.json"
SCAN_CACHE_FILE = HUB_APP_DIR / "scan_cache.json"
PDF_DIM_CACHE_FILE = HUB_APP_DIR / "pdf_dim_cache.json"

DEFAULT_PLATFORM_DIRS = [
    ("claude输出", "Claude", "#713E50"),
    ("gemini输出", "AGY", "#2E5B88"),
    ("grok输出", "Grok", "#4A6B5D"),
    ("codex输出", "Codex", "#2D6A4F"),
    ("cursor输出", "Cursor", "#3B4252"),
    ("windsurf输出", "Windsurf", "#2A6F97"),
    ("zcode输出", "ZCode", "#8C5A32"),
    ("alma输出", "Alma", "#6B4E71"),
    ("deepseek输出", "DeepSeek", "#3D5A80"),
    ("kimi输出", "Kimi", "#5C677D"),
    ("qwen输出", "Qwen", "#5A4E7C"),
    ("group输出", "Group", "#7A7D82"),
    ("glm输出", "GLM", "#7A7D82"),
]

DEFAULT_TOPICS = [
    {"id": "01_research_survey", "folder": "01_深度调研与文献综述", "short": "深度调研 / 文献综述", "title": "领域调研报告、论文精读、技术选型对比与知识蒸馏", "color": "#2E5B88"},
    {"id": "02_data_viz", "folder": "02_数据分析与可视化组图", "short": "数据分析 / 科学组图", "title": "统计建模、数据清洗、出版级矢量图表与实验图谱", "color": "#1E513F"},
    {"id": "03_slides_docs", "folder": "03_演示汇报与文档撰写", "short": "演示汇报 / 讲义文档", "title": "PPTX 幻灯片、项目汇报、方案总结与长文手稿", "color": "#8C5A32"},
    {"id": "04_web_apps", "folder": "04_前端界面与交互应用", "short": "前端界面 / 交互原型", "title": "交互式看板、桌面小程序、UI 设计稿与全栈原型", "color": "#713E50"},
    {"id": "05_code_automation", "folder": "05_工程代码与自动化脚本", "short": "工程架构 / 自动脚本", "title": "后端工具、数据管线、爬虫自动化与性能调优", "color": "#4A6B5D"},
    {"id": "06_agent_workflow", "folder": "06_Agent工作流与提示词", "short": "Agent架构 / 规则技能", "title": "多智能体编排、Skill 技能包、评测基准与配置优化", "color": "#4A5568"},
    {"id": "07_daily_ops", "folder": "07_日常巡检与个人资料", "short": "日常值守 / 个人资料", "title": "日报周报、系统巡检、学习笔记与个人档案", "color": "#5C6B57"},
    {"id": "08_misc_projects", "folder": "08_其他独立探索项目", "short": "独立探索 / 其他项目", "title": "未归入固定主线的独立任务与临时实验", "color": "#78716C"},
]

DEFAULT_RULES = [
    {"any": ["调研", "综述", "论文", "文献", "精读", "survey", "review", "paper", "benchmark", "蒸馏"], "topic_id": "01_research_survey", "branches": [
        {"any": ["论文", "paper", "精读", "文献"], "name": "01_文献精读与前沿追踪"},
        {"default": "02_专题深度调研与对比"}
    ]},
    {"any": ["数据", "分析", "统计", "图", "可视化", "plot", "fig", "chart", "umap", "rna", "seq", "组学", "实验"], "topic_id": "02_data_viz", "branches": [
        {"any": ["fig", "组图", "美化", "拼图", "plot", "chart"], "name": "01_图表绘制与版面精修"},
        {"default": "02_数据建模与定量分析"}
    ]},
    {"any": ["ppt", "slide", "汇报", "演示", "答辩", "总结", "讲义", "稿", "doc", "report"], "topic_id": "03_slides_docs", "branches": [
        {"any": ["ppt", "slide", "演示", "汇报"], "name": "01_演示文稿迭代主线"},
        {"default": "02_报告文档与长文写作"}
    ]},
    {"any": ["前端", "界面", "网页", "应用", "ui", "ux", "web", "app", "dashboard", "看板", "主题", "设计"], "topic_id": "04_web_apps", "branches": [
        {"default": "01_交互应用与界面设计"}
    ]},
    {"any": ["脚本", "工具", "自动", "代码", "重构", "api", "cli", "server", "bot", "爬", "pipeline"], "topic_id": "05_code_automation", "branches": [
        {"default": "01_自动化工具与脚本工程"}
    ]},
    {"any": ["agent", "skill", "prompt", "workflow", "claude", "gemini", "agy", "grok", "mcp", "hook", "记忆", "调度", "管家"], "topic_id": "06_agent_workflow", "branches": [
        {"default": "01_智能体协同与工作流优化"}
    ]},
    {"any": ["日报", "周报", "值守", "巡检", "笔记", "课程", "学习", "简历", "resume", "note"], "topic_id": "07_daily_ops", "branches": [
        {"default": "01_日常记录与个人知识库"}
    ]},
]


def load_profile_config() -> dict:
    for fn in ("private_profile.json", "custom_topics.json"):
        fp = HUB_APP_DIR / fn
        if fp.is_file():
            try:
                cfg = json.loads(fp.read_text(encoding="utf-8-sig"))
                if isinstance(cfg, dict) and cfg.get("topics"):
                    return cfg
            except Exception:
                pass
    return {
        "topics": DEFAULT_TOPICS,
        "rules": DEFAULT_RULES,
        "fallback_topic_id": "08_misc_projects",
        "fallback_branch": "01_独立任务与探索归档",
        "claude_group_names": [],
        "known_dup_targets": {},
        "herdr_hints": [],
    }


PROFILE = load_profile_config()
TOPICS = PROFILE["topics"]
TOPIC_BY_ID = {t["id"]: t for t in TOPICS}
PLATFORM_DIRS = DEFAULT_PLATFORM_DIRS
PLATFORM_MAP = {k: (label, color) for k, label, color in PLATFORM_DIRS}

# Strictly noise/backup directories to skip for deliverables (note: `panels` is NO LONGER discarded here so single-panel PDFs are captured cleanly!)
HARD_NOISE_DIR_PAT = re.compile(
    r"^(?:_backups?|backups?|_snapshots?|snapshots?|\.archive.*|_archive.*|_draft_fail|"
    r"__pycache__|\.git|node_modules|render.*|inspect.*|panel_pngs.*|"
    r"offslide_imgs|lint_pages|checker_pngs|_slides_.*|_workroom|_probe.*|qa.*|logs|原版配色备份.*|_全量.*备份.*)$",
    re.I,
)
BACKUP_SIZE_DIR_PAT = re.compile(
    r"(?:^|_)(?:backups?|snapshots?|archive|pre_edit|rejected|render|inspect|panels_600dpi|offslide_imgs)(?:_|$)|^\.archive|备份",
    re.I,
)
PANEL_SUBDIR_PAT = re.compile(r"^(?:panels?|pdf_panels?|single_panels?|单面板.*|.*单图.*)$", re.I)
INTERMEDIATE_SLICE_PAT = re.compile(r"(?:_notag|_noletter|_raw_tmp|_NA)$", re.I)


def load_pdf_dim_cache() -> dict:
    if PDF_DIM_CACHE_FILE.is_file():
        try:
            return json.loads(PDF_DIM_CACHE_FILE.read_text(encoding="utf-8-sig"))
        except Exception:
            pass
    return {}


def save_pdf_dim_cache(cache: dict) -> None:
    try:
        HUB_APP_DIR.mkdir(parents=True, exist_ok=True)
        PDF_DIM_CACHE_FILE.write_text(json.dumps(cache, ensure_ascii=False), encoding="utf-8")
    except Exception:
        pass


def get_pdf_physical_info(fp: pathlib.Path, st_mtime: float, st_size: int, dim_cache: dict) -> tuple[float, float, int]:
    """Return (width_mm, height_mm, page_count) with fast persistent caching."""
    key = f"{fp}|{int(st_mtime)}|{st_size}"
    hit = dim_cache.get(key)
    if hit:
        return hit[0], hit[1], hit[2]

    w_mm, h_mm, pages = 0.0, 0.0, 1
    if HAS_FITZ:
        try:
            doc = _fitz.open(fp)
            pages = len(doc)
            if pages > 0:
                rc = doc[0].rect
                w_mm = round(rc.width * 25.4 / 72.0, 1)
                h_mm = round(rc.height * 25.4 / 72.0, 1)
            doc.close()
        except Exception:
            pass

    if w_mm <= 0:
        try:
            with open(fp, "rb") as f:
                b = f.read(8192)
                if st_size > 8192:
                    f.seek(max(0, st_size - 8192))
                    b += f.read(8192)
            boxes = re.findall(rb"/MediaBox\s*\[\s*([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s*\]", b)
            if boxes:
                x0, y0, x1, y1 = [float(v) for v in boxes[0]]
                w_mm = round(abs(x1 - x0) * 25.4 / 72.0, 1)
                h_mm = round(abs(y1 - y0) * 25.4 / 72.0, 1)
        except Exception:
            pass

    dim_cache[key] = [w_mm, h_mm, pages]
    return w_mm, h_mm, pages


def parse_figure_tag(filename: str, rel_path: str) -> tuple[str, str, int]:
    """
    Extract canonical `(fig_group, fig_panel, fig_order)` from filename or parent path.
    Order:
      GA / Graphical Abstract -> 0
      Fig 1 .. Fig 19         -> 1 .. 19
      专题与汇总图              -> 90
      Fig S1 .. Fig S20       -> 101 .. 120
      Table 1 .. Table 9      -> 181 .. 189
      Table S1 .. Table S15   -> 201 .. 215
    """
    stem = pathlib.Path(filename).stem
    combined = f"{rel_path} {stem}"

    # 1) Graphical Abstract / Fig 0
    if re.search(r"(?:graphical[_\s-]*abstract|图形摘要|(?<![a-z0-9])ga(?![a-z0-9])|fig[_\s-]*0\b)", combined, re.I):
        return "GA 图形摘要", "", 0

    # 2) Supplementary Table S1..S15
    m_ts = re.search(r"(?:table|tab|表)[_\s-]*s(\d+)", combined, re.I)
    if m_ts:
        n = int(m_ts.group(1))
        return f"Table S{n}", "", 200 + n

    # 3) Main Table 1..9
    m_tm = re.search(r"(?:table|tab|表)[_\s-]*(\d+)", combined, re.I)
    if m_tm:
        n = int(m_tm.group(1))
        return f"Table {n}", "", 180 + n

    # 4) Supplementary Figure: Fig S1..S20 / 图S1..S20 / Extended Data Fig
    m_fs = re.search(
        r"(?:fig(?:ure)?|图|extended[_\s-]*data[_\s-]*fig(?:ure)?)[_\s-]*s(\d+)(?:[_\s.-]*(?:panel[_\s-]*)?([a-z]{1,2})(?![a-z]))?",
        stem, re.I
    )
    if not m_fs:
        m_fs = re.search(r"(?:fig(?:ure)?|图)[_\s-]*s(\d+)", rel_path, re.I)
        if m_fs:
            n = int(m_fs.group(1))
            mp = re.search(r"(?:panel[_\s-]*|_ )?([A-Z])(?:\b|_)", stem)
            p_let = mp.group(1).upper() if mp else ""
            return f"Fig S{n}", p_let, 100 + n
    if m_fs:
        n = int(m_fs.group(1))
        raw_p = (m_fs.group(2) or "").upper() if m_fs.lastindex and m_fs.lastindex >= 2 else ""
        # Avoid mistaking words like 'stg' or 'all' as panel letters
        if len(raw_p) == 2 and raw_p not in ("AD", "AB", "BC", "CD", "EF", "FG", "GH", "HI", "IJ", "JK", "KL", "MN"):
            raw_p = ""
        # Check trailing _A .. _R panel suffix if raw_p empty
        if not raw_p:
            mt_end = re.search(r"(?:_panel_|_)([A-Z])$", stem)
            if mt_end:
                raw_p = mt_end.group(1)
        return f"Fig S{n}", raw_p, 100 + n

    # 5) Main Figure: Fig 1..19 / 图1..19
    m_fm = re.search(
        r"(?:fig(?:ure)?|图)[_\s-]*(\d+)(?:[_\s.-]*(?:panel[_\s-]*)?([a-z]{1,3})(?![a-z]))?",
        stem, re.I
    )
    if not m_fm:
        m_fm = re.search(r"(?:fig(?:ure)?|图)[_\s-]*(\d+)", rel_path, re.I)
        if m_fm:
            n = int(m_fm.group(1))
            mt_end = re.search(r"(?:panel[_\s-]*|_)([A-Z])$", stem, re.I)
            return f"Fig {n}", (mt_end.group(1).upper() if mt_end else ""), n
    if m_fm:
        n = int(m_fm.group(1))
        raw_p = (m_fm.group(2) or "").upper() if m_fm.lastindex and m_fm.lastindex >= 2 else ""
        if len(raw_p) >= 2 and raw_p not in ("AD", "AB", "BC", "CD", "EF", "FG", "FGH", "GH", "HI", "IJ", "IJK", "JK", "KL", "MN"):
            raw_p = ""
        if not raw_p:
            mt_end = re.search(r"(?:_panel_|_)([A-Z])$", stem)
            if mt_end:
                raw_p = mt_end.group(1)
        return f"Fig {n}", raw_p, n

    # 6) Standalone panel_A.pdf inside a project
    m_pan = re.search(r"(?:^|_)panel[_\s-]*([A-Z])$", stem, re.I)
    if m_pan:
        return "独立子面板", m_pan.group(1).upper(), 95

    return "专题与汇总图", "", 90


def is_full_page_a4_pdf(w_mm: float, h_mm: float, pages: int, stem: str, in_panel_dir: bool) -> bool:
    """
    Determine whether a PDF is a true multi-panel assembled A4 / Full-Slide figure
    vs a single-panel / cropped sub-figure.
    """
    if in_panel_dir and re.search(r"_[A-Z]$|panel_[A-Z]", stem):
        return False
    if w_mm > 0 and h_mm > 0:
        short_side = min(w_mm, h_mm)
        long_side = max(w_mm, h_mm)
        # Standard A4 (210x297), Letter (216x279), PPT Slide Full-Page (190.5x275.1), Full Table (186x231+)
        if short_side >= 165.0 and long_side >= 225.0:
            return True
        if pages >= 2 and long_side >= 180.0 and short_side >= 120.0:
            return True
        return False
    # Fallback when PDF parser unavailable
    if in_panel_dir:
        return False
    if re.search(r"(?:_a4\b|a4拼图|整页|汇总|all_figs)", stem, re.I):
        return True
    return False


def load_user_meta() -> dict:
    if USER_META_FILE.is_file():
        try:
            return json.loads(USER_META_FILE.read_text(encoding="utf-8-sig"))
        except Exception:
            pass
    return {"starred": [], "notes": {}, "topic_overrides": {}}


def save_user_meta(meta: dict) -> None:
    HUB_APP_DIR.mkdir(parents=True, exist_ok=True)
    USER_META_FILE.write_text(json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8")


def classify_project(name: str, parent_group: str = "", rel_key: str = "", user_overrides: dict = None) -> tuple[str, str]:
    if user_overrides and rel_key in user_overrides:
        ov = user_overrides[rel_key]
        if isinstance(ov, dict) and ov.get("topic_id") in TOPIC_BY_ID:
            return ov["topic_id"], ov.get("sub_branch") or "手动归类分支"
        elif isinstance(ov, str) and ov in TOPIC_BY_ID:
            return ov, "手动归类分支"

    s = f"{parent_group} {name}".lower()
    for rule in PROFILE.get("rules", []):
        any_kws = [k.lower() for k in rule.get("any", [])]
        not_kws = [k.lower() for k in rule.get("not_any", [])]
        if any_kws and not any(k in s for k in any_kws):
            continue
        if not_kws and any(k in s for k in not_kws):
            continue
        tid = rule["topic_id"]
        for br in rule.get("branches", []):
            if "default" in br:
                return tid, br["default"]
            b_kws = [k.lower() for k in br.get("any", [])]
            if b_kws and any(k in s for k in b_kws):
                return tid, br["name"]
        return tid, "01_默认演进主线"

    fb_tid = PROFILE.get("fallback_topic_id") or TOPICS[-1]["id"]
    fb_br = PROFILE.get("fallback_branch") or "01_独立项目归档"
    return fb_tid, fb_br


def extract_version_sort_key(name: str, mtime: float) -> tuple:
    m_ver = re.search(r"(?:_v|rev|gen)(\d+)(?:[._](\d+))?", name, re.I)
    ver_num = int(m_ver.group(1)) * 100 + int(m_ver.group(2) or 0) if m_ver else 0
    return (mtime, ver_num, name.lower())


def get_active_locks(heartbeat_sec: int = 7200) -> dict:
    now = time.time()
    locked = {}

    def add_lock(p_str: str, reason: str, agent: str = "", gen: str = ""):
        try:
            np = os.path.normcase(os.path.abspath(p_str))
        except Exception:
            return
        rec = locked.setdefault(np, {"reasons": [], "agent": "", "gen": ""})
        if reason not in rec["reasons"]:
            rec["reasons"].append(reason)
        if agent and not rec["agent"]:
            rec["agent"] = agent
        if gen and not rec["gen"]:
            rec["gen"] = gen

    sends_map = {}
    sends_path_str = PROFILE.get("sends_log") or os.environ.get("HERDR_SENDS_LOG") or ""
    if sends_path_str:
        sends_log = pathlib.Path(sends_path_str)
        if sends_log.is_file():
            try:
                for ln in sends_log.read_text(encoding="utf-8", errors="replace").splitlines():
                    if not ln.strip():
                        continue
                    obj = json.loads(ln)
                    if obj.get("dry_run") or obj.get("status") in ("rejected", "dry_run_rejected"):
                        continue
                    ag = (obj.get("agent") or "").strip()
                    proj = (obj.get("project") or "").strip()
                    if ag and proj:
                        sends_map[ag] = proj
            except Exception:
                pass

    handoff_proj_refs = {}
    handoff_root_str = PROFILE.get("handoff_root") or os.environ.get("MODEL_HANDOFF_ROOT") or ""
    if handoff_root_str:
        handoff_root = pathlib.Path(handoff_root_str)
        closed_pat = re.compile(r"^状态\s*[:：]\s*(?:已完结|已归档|已完成|完结|closed|done|archived)\b", re.I | re.M)
        closed_body = re.compile(r"(?:状态\s*[:：]\s*已完结|本链.{0,16}已完结|无需(?:新开会话|下一棒|交接))", re.I)
        gen_pat = re.compile(r"代号\s*[:：]\s*(第\s*\d+\s*代)")
        root_name_low = MODEL_OUT_ROOT.name.lower()

        if handoff_root.is_dir():
            for hd in handoff_root.iterdir():
                if not hd.is_dir():
                    continue
                sp = hd / "STATE.md"
                if not sp.is_file():
                    continue
                try:
                    txt = sp.read_text(encoding="utf-8", errors="replace")
                    st_mtime = sp.stat().st_mtime
                except Exception:
                    continue
                is_closed = bool(closed_pat.search(txt[:600]) or closed_body.search(txt[:800]))
                gm = gen_pat.search(txt[:600])
                gen_str = gm.group(1).replace(" ", "") if gm else ""
                refs = set()
                for m in re.finditer(r"([A-Za-z]:[\\/][^\s`\"'()\[\]<>，。；]+)", txt):
                    raw_p = m.group(1).rstrip("\\/.,;:")
                    parts = pathlib.Path(raw_p).parts
                    low_parts = [pt.lower() for pt in parts]
                    if root_name_low in low_parts:
                        idx = low_parts.index(root_name_low)
                        if idx + 2 < len(parts):
                            if parts[idx + 1].lower() == "claude输出" and idx + 3 < len(parts):
                                proj_dir = pathlib.Path(*parts[: idx + 4])
                            else:
                                proj_dir = pathlib.Path(*parts[: idx + 3])
                            if proj_dir.exists():
                                refs.add(str(proj_dir))
                handoff_proj_refs[hd.name] = {"refs": list(refs), "is_closed": is_closed, "gen": gen_str, "mtime": st_mtime}
                if not is_closed and (now - st_mtime < 48 * 3600):
                    for rp in refs:
                        add_lock(rp, f"活跃交接单: {hd.name} ({gen_str or '进行中'})", gen=gen_str)

    herdr_agents = []
    try:
        herdr_bin = shutil.which("herdr")
        if not herdr_bin:
            herdr_glob = str(pathlib.Path.home() / ".herdr" / "packages" / "standalone" / "releases" / "*" / "herdr.exe")
            cands = sorted(glob.glob(herdr_glob), reverse=True)
            if cands:
                herdr_bin = cands[0]
        if herdr_bin:
            r = subprocess.run(
                [herdr_bin, "agent", "list"],
                capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=4,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0x08000000),
            )
            if r.returncode == 0 and r.stdout.strip():
                obj = json.loads(r.stdout)
                ag_list = (obj.get("result", {}).get("agents") if isinstance(obj.get("result"), dict) else obj.get("agents")) if isinstance(obj, dict) else obj
                if isinstance(ag_list, list):
                    for a in ag_list:
                        ag_name = a.get("name") or a.get("pane_id") or a.get("id") or ""
                        status = a.get("agent_status") or a.get("status") or a.get("state") or ""
                        pane = a.get("pane_id") or a.get("pane") or ""
                        proj = sends_map.get(ag_name, "")
                        herdr_agents.append({"name": ag_name, "status": status, "pane": pane, "project": proj})
                        if status in ("working", "blocked", "running"):
                            if proj and proj in handoff_proj_refs:
                                hinfo = handoff_proj_refs[proj]
                                for rp in hinfo["refs"]:
                                    add_lock(rp, f"Herdr运行中: {ag_name} [{status}]", agent=ag_name, gen=hinfo["gen"])
                            if proj:
                                cand_g = MODEL_OUT_ROOT / "gemini输出" / proj
                                if cand_g.is_dir():
                                    add_lock(str(cand_g), f"Herdr运行中: {ag_name} [{status}]", agent=ag_name)
    except Exception:
        pass

    return {
        "herdr_agents": herdr_agents,
        "locked_paths": locked,
        "checked_at": _dt.datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
        "heartbeat_sec": heartbeat_sec,
    }


def _extract_html_title(fp: pathlib.Path) -> str:
    try:
        with open(fp, "r", encoding="utf-8", errors="ignore") as f:
            head = f.read(6144)
        m = re.search(r"<title>(.*?)</title>", head, re.I | re.S)
        if m:
            t = re.sub(r"\s+", " ", m.group(1)).strip()
            if t:
                return t[:90]
    except Exception:
        pass
    return fp.stem


def _infer_platform_info(platform_dir: str) -> tuple[str, str]:
    if platform_dir in PLATFORM_MAP:
        return PLATFORM_MAP[platform_dir]
    low = platform_dir.lower()
    for kw, label, col in (
        ("claude", "Claude", "#713E50"),
        ("gemini", "Gemini", "#2E5B88"),
        ("agy", "AGY", "#2E5B88"),
        ("gpt", "OpenAI", "#2D6A4F"),
        ("codex", "Codex", "#2D6A4F"),
        ("cursor", "Cursor", "#3B4252"),
        ("grok", "Grok", "#4A6B5D"),
        ("deepseek", "DeepSeek", "#3D5A80"),
        ("kimi", "Kimi", "#5C677D"),
        ("qwen", "Qwen", "#5A4E7C"),
    ):
        if kw in low:
            return label, col
    clean = re.sub(r"(?:输出|[-_]?output|[-_]?out)$", "", platform_dir, flags=re.I).strip()
    return (clean[:12] or "本地归档"), "#5C6B57"


def scan_single_project_dir(pdir: pathlib.Path, platform_dir: str, parent_group: str, user_meta: dict, lock_info: dict, now_ts: float, dim_cache: dict) -> dict:
    rel_key = str(pdir.relative_to(MODEL_OUT_ROOT)).replace("\\", "/")
    overrides = user_meta.get("topic_overrides") or user_meta.get("overrides") or {}
    topic_id, sub_branch = classify_project(pdir.name, parent_group, rel_key, overrides)
    plat_label, plat_color = _infer_platform_info(platform_dir)

    total_files = 0
    total_bytes = 0
    cache_bytes = 0
    latest_mtime = 0.0
    latest_rel_file = ""

    htmls, pptxs, raw_a4_pdfs, raw_single_pdfs, docs = [], [], [], [], []

    for root, dirs, files in os.walk(pdir):
        rp = pathlib.Path(root)
        if rp != pdir:
            try:
                if (hasattr(rp, "is_junction") and rp.is_junction()) or rp.is_symlink():
                    dirs[:] = []
                    continue
            except Exception:
                pass
        rel_parts = rp.relative_to(pdir).parts
        in_hard_noise = any(HARD_NOISE_DIR_PAT.match(pt) or BACKUP_SIZE_DIR_PAT.search(pt) for pt in rel_parts)
        in_panel_dir = any(PANEL_SUBDIR_PAT.match(pt) for pt in rel_parts)
        depth = len(rel_parts)

        for fn in files:
            if fn.startswith("~$"):
                continue
            fp = rp / fn
            try:
                st = fp.stat()
            except OSError:
                continue
            sz = st.st_size
            mt = st.st_mtime
            total_files += 1
            total_bytes += sz
            if in_hard_noise or fn.endswith((".b64", ".tgz", ".bak", ".pyc", ".log")):
                cache_bytes += sz
            if mt > latest_mtime and not fn.endswith(".pyc"):
                latest_mtime = mt
                latest_rel_file = str(fp.relative_to(pdir)).replace("\\", "/")

            if in_hard_noise or depth > 4 or sz == 0:
                continue

            ext = fp.suffix.lower()
            stem = fp.stem
            rel_str = str(fp.relative_to(pdir)).replace("\\", "/")
            item_base = {
                "name": fn,
                "path": str(fp),
                "rel": rel_str,
                "size_mb": round(sz / 1048576.0, 2),
                "mtime": mt,
                "date": _dt.datetime.fromtimestamp(mt).strftime("%Y-%m-%d %H:%M"),
            }
            if ext in (".html", ".htm") and depth <= 3:
                item_base["title"] = _extract_html_title(fp)
                htmls.append(item_base)
            elif ext == ".pptx" and depth <= 3:
                pptxs.append(item_base)
            elif ext == ".pdf":
                if INTERMEDIATE_SLICE_PAT.search(stem):
                    continue
                w_mm, h_mm, pages = get_pdf_physical_info(fp, mt, sz, dim_cache)
                fig_group, fig_panel, fig_order = parse_figure_tag(fn, rel_str)
                dim_label = f"{int(round(w_mm))}×{int(round(h_mm))}mm" if w_mm > 0 and h_mm > 0 else ""
                if pages > 1 and dim_label:
                    dim_label += f" ({pages}p)"

                item_base["dim_mm"] = dim_label
                item_base["fig_group"] = fig_group
                item_base["fig_panel"] = fig_panel
                item_base["fig_order"] = fig_order
                item_base["_depth"] = depth
                item_base["_sz"] = sz

                if is_full_page_a4_pdf(w_mm, h_mm, pages, stem, in_panel_dir):
                    item_base["kind"] = "a4"
                    raw_a4_pdfs.append(item_base)
                else:
                    item_base["kind"] = "single"
                    raw_single_pdfs.append(item_base)
            elif depth <= 2 and (
                ext in (".docx", ".xlsx")
                or (ext in (".md", ".txt") and re.search(r"(?:legend|图注|稿件|manuscript|blueprint|蓝图|台账|readme|summary)", stem, re.I))
            ):
                docs.append(item_base)

    # Deduplicate identical PDF copies within the same project (keep shallowest / newest copy)
    def dedup_pdfs(arr: list[dict], limit: int) -> list[dict]:
        arr.sort(key=lambda x: (x["_depth"], -x["mtime"]))
        seen_sig = set()
        out = []
        for it in arr:
            sig = (it["name"].lower(), it["_sz"])
            if sig in seen_sig:
                continue
            seen_sig.add(sig)
            it.pop("_depth", None)
            it.pop("_sz", None)
            out.append(it)
        out.sort(key=lambda x: x["mtime"], reverse=True)
        return out[:limit]

    a4_pdfs = dedup_pdfs(raw_a4_pdfs, 40)
    single_pdfs = dedup_pdfs(raw_single_pdfs, 60)
    htmls.sort(key=lambda x: x["mtime"], reverse=True)
    pptxs.sort(key=lambda x: x["mtime"], reverse=True)
    docs.sort(key=lambda x: x["mtime"], reverse=True)

    if latest_mtime <= 0:
        latest_mtime = pdir.stat().st_mtime

    np = os.path.normcase(os.path.abspath(str(pdir)))
    lock_hit = dict(lock_info["locked_paths"].get(np, {}))
    reasons = list(lock_hit.get("reasons", []))
    age_sec = max(0.0, now_ts - latest_mtime)
    if age_sec <= lock_info.get("heartbeat_sec", 7200) and not pdir.name.startswith("_根目录散件归档"):
        mins = max(1, int(age_sec // 60))
        reasons.insert(0, f"最近写入 ({mins}分钟前: {latest_rel_file or pdir.name})")
        if not lock_hit.get("agent"):
            low_n = pdir.name.lower()
            for ha in lock_info.get("herdr_agents", []):
                if ha["status"] not in ("working", "blocked"):
                    continue
                an = ha["name"].lower()
                for hint in PROFILE.get("herdr_hints", []):
                    if hint.get("agent_kw", "") in an and hint.get("dir_kw", "") in low_n:
                        lock_hit["agent"] = ha["name"]
                        reasons.append(f"Herdr活跃关联: {ha['name']}")
                        break
                if lock_hit.get("agent"):
                    break

    is_locked = len(reasons) > 0
    starred_list = user_meta.get("starred") or []
    stars_dict = user_meta.get("stars") or {}
    starred = (rel_key in starred_list) or bool(stars_dict.get(rel_key))
    note = (user_meta.get("notes") or {}).get(rel_key, "")

    return {
        "key": rel_key,
        "name": pdir.name,
        "parent_group": parent_group,
        "path": str(pdir),
        "platform_dir": platform_dir,
        "platform": plat_label,
        "platform_color": plat_color,
        "topic_id": topic_id,
        "sub_branch": sub_branch,
        "total_files": total_files,
        "size_mb": round(total_bytes / 1048576.0, 2),
        "cache_mb": round(cache_bytes / 1048576.0, 2),
        "mtime": latest_mtime,
        "date": _dt.datetime.fromtimestamp(latest_mtime).strftime("%Y-%m-%d"),
        "datetime": _dt.datetime.fromtimestamp(latest_mtime).strftime("%Y-%m-%d %H:%M"),
        "latest_file": latest_rel_file,
        "locked": is_locked,
        "lock_reasons": reasons,
        "lock_agent": lock_hit.get("agent", ""),
        "lock_gen": lock_hit.get("gen", ""),
        "starred": starred,
        "note": note,
        "htmls": htmls[:10],
        "pptxs": pptxs[:8],
        "a4_pdfs": a4_pdfs,
        "single_pdfs": single_pdfs,
        "pdfs": (a4_pdfs + single_pdfs)[:20],
        "docs": docs[:8],
        "counts": {
            "html": len(htmls),
            "pptx": len(pptxs),
            "a4_pdf": len(a4_pdfs),
            "single_pdf": len(single_pdfs),
            "pdf": len(a4_pdfs) + len(single_pdfs),
            "doc": len(docs),
        },
        "_schema_ver": 2,
    }


def scan_all_projects(use_cache: bool = True) -> dict:
    t0 = time.time()
    user_meta = load_user_meta()
    lock_info = get_active_locks()
    dim_cache = load_pdf_dim_cache()
    dim_cache_before = len(dim_cache)
    now_ts = time.time()

    old_cache = {}
    if use_cache and SCAN_CACHE_FILE.is_file():
        try:
            raw_c = json.loads(SCAN_CACHE_FILE.read_text(encoding="utf-8-sig"))
            for p in raw_c.get("projects", []):
                if p.get("_schema_ver") == 2:
                    old_cache[p["path"]] = p
        except Exception:
            old_cache = {}

    projects = []
    overrides = user_meta.get("topic_overrides") or user_meta.get("overrides") or {}
    starred_set = set(user_meta.get("starred") or [])

    def process_dir(pdir: pathlib.Path, platform_dir: str, parent_group: str = ""):
        try:
            if (hasattr(pdir, "is_junction") and pdir.is_junction()) or pdir.is_symlink():
                return
            d_mt = pdir.stat().st_mtime
        except OSError:
            return
        p_str = str(pdir)
        cached = old_cache.get(p_str)
        if (
            cached
            and abs(cached.get("_dir_mtime", 0) - d_mt) < 0.01
            and (now_ts - cached.get("mtime", now_ts) > 21600)
        ):
            rel_key = str(pdir.relative_to(MODEL_OUT_ROOT)).replace("\\", "/")
            tid, sbr = classify_project(pdir.name, parent_group, rel_key, overrides)
            np = os.path.normcase(os.path.abspath(p_str))
            lhit = lock_info["locked_paths"].get(np, {})
            cached["key"] = rel_key
            cached["topic_id"] = tid
            cached["sub_branch"] = sbr
            cached["starred"] = (rel_key in starred_set) or bool((user_meta.get("stars") or {}).get(rel_key))
            cached["note"] = (user_meta.get("notes") or {}).get(rel_key, "")
            cached["locked"] = len(lhit.get("reasons", [])) > 0
            cached["lock_reasons"] = lhit.get("reasons", [])
            cached["lock_agent"] = lhit.get("agent", "")
            cached["lock_gen"] = lhit.get("gen", "")
            projects.append(cached)
            return

        rec = scan_single_project_dir(pdir, platform_dir, parent_group, user_meta, lock_info, now_ts, dim_cache)
        rec["_dir_mtime"] = d_mt
        projects.append(rec)

    claude_groups = set(PROFILE.get("claude_group_names") or [])
    discovered_plat_dirs = []
    seen_pnames = set()

    for p_name, _, _ in PLATFORM_DIRS:
        if (MODEL_OUT_ROOT / p_name).is_dir():
            discovered_plat_dirs.append(p_name)
            seen_pnames.add(p_name.lower())

    if MODEL_OUT_ROOT.is_dir():
        for item in sorted(MODEL_OUT_ROOT.iterdir(), key=lambda x: x.name.lower()):
            if not item.is_dir() or item.name.lower() in seen_pnames:
                continue
            if item.name.startswith(("00_", "_", ".")):
                continue
            if item.name.endswith("输出") or re.search(r"[-_](?:output|out)s?$", item.name, re.I):
                discovered_plat_dirs.append(item.name)
                seen_pnames.add(item.name.lower())

    if discovered_plat_dirs:
        for plat_dir_name in discovered_plat_dirs:
            mdir = MODEL_OUT_ROOT / plat_dir_name
            for child in sorted(mdir.iterdir(), key=lambda x: x.name.lower()):
                if not child.is_dir():
                    continue
                if child.name.startswith(("_", ".")) and not child.name.startswith("_根目录散件归档"):
                    continue
                if plat_dir_name == "claude输出" and child.name in claude_groups:
                    sub_dirs = [c for c in sorted(child.iterdir(), key=lambda x: x.name.lower()) if c.is_dir() and not c.name.startswith(("_", "."))]
                    for sd in sub_dirs:
                        process_dir(sd, plat_dir_name, parent_group=child.name)
                else:
                    process_dir(child, plat_dir_name, parent_group="")
    else:
        for child in sorted(MODEL_OUT_ROOT.iterdir(), key=lambda x: x.name.lower()):
            if child.is_dir() and not child.name.startswith(("00_", "_", ".")):
                process_dir(child, "本地项目", parent_group="")

    misc_hist = MODEL_OUT_ROOT / "_归档与杂项" / "根目录历史课题散件"
    if misc_hist.is_dir():
        for child in sorted(misc_hist.iterdir(), key=lambda x: x.name.lower()):
            if child.is_dir() and not child.name.startswith("."):
                process_dir(child, "根目录归档", parent_group="")

    dup_dir = MODEL_OUT_ROOT / "_归档与杂项" / "待确认重复副本"
    duplicates_info = []
    if dup_dir.is_dir():
        for child in sorted(dup_dir.iterdir()):
            if child.is_dir():
                sz = sum(f.stat().st_size for r, _, fs in os.walk(child) for f in [pathlib.Path(r) / x for x in fs] if f.exists())
                duplicates_info.append({
                    "name": child.name,
                    "path": str(child),
                    "size_mb": round(sz / 1048576.0, 2),
                })

    if len(dim_cache) != dim_cache_before:
        save_pdf_dim_cache(dim_cache)

    projects.sort(key=lambda x: extract_version_sort_key(x["name"], x["mtime"]), reverse=True)

    topic_tree = []
    for t in TOPICS:
        tid = t["id"]
        t_projs = [p for p in projects if p["topic_id"] == tid]
        branches = {}
        for p in t_projs:
            branches.setdefault(p["sub_branch"], []).append(p)
        branch_list = []
        for bname in sorted(branches.keys()):
            b_projs = sorted(branches[bname], key=lambda x: x["mtime"], reverse=True)
            branch_list.append({
                "name": bname,
                "count": len(b_projs),
                "locked_count": sum(1 for x in b_projs if x["locked"]),
                "projects": b_projs,
            })
        topic_tree.append({
            **t,
            "project_count": len(t_projs),
            "locked_count": sum(1 for x in t_projs if x["locked"]),
            "starred_count": sum(1 for x in t_projs if x["starred"]),
            "html_count": sum(x["counts"]["html"] for x in t_projs),
            "pptx_count": sum(x["counts"]["pptx"] for x in t_projs),
            "a4_pdf_count": sum(x["counts"]["a4_pdf"] for x in t_projs),
            "single_pdf_count": sum(x["counts"]["single_pdf"] for x in t_projs),
            "pdf_count": sum(x["counts"]["pdf"] for x in t_projs),
            "size_mb": round(sum(x["size_mb"] for x in t_projs), 1),
            "cache_mb": round(sum(x["cache_mb"] for x in t_projs), 1),
            "latest_mtime": max((x["mtime"] for x in t_projs), default=0),
            "branches": branch_list,
        })

    data = {
        "generated_at": _dt.datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
        "scan_ms": int((time.time() - t0) * 1000),
        "root_path": str(MODEL_OUT_ROOT),
        "summary": {
            "total_projects": len(projects),
            "locked_projects": sum(1 for p in projects if p["locked"]),
            "starred_projects": sum(1 for p in projects if p["starred"]),
            "total_html": sum(p["counts"]["html"] for x in projects for p in [x]),
            "total_pptx": sum(p["counts"]["pptx"] for x in projects for p in [x]),
            "total_a4_pdf": sum(p["counts"]["a4_pdf"] for x in projects for p in [x]),
            "total_single_pdf": sum(p["counts"]["single_pdf"] for x in projects for p in [x]),
            "total_pdf": sum(p["counts"]["pdf"] for x in projects for p in [x]),
            "total_size_gb": round(sum(p["size_mb"] for p in projects) / 1024.0, 2),
            "total_cache_gb": round(sum(p["cache_mb"] for p in projects) / 1024.0, 2),
            "dup_size_mb": round(sum(d["size_mb"] for d in duplicates_info), 1),
        },
        "herdr_agents": lock_info["herdr_agents"],
        "duplicates": duplicates_info,
        "topics": topic_tree,
        "projects": projects,
    }

    try:
        HUB_APP_DIR.mkdir(parents=True, exist_ok=True)
        SCAN_CACHE_FILE.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
    except Exception:
        pass
    return data


if __name__ == "__main__":
    res = scan_all_projects(use_cache=False)
    s = res["summary"]
    print(f"Scanned {s['total_projects']} projects in {res['scan_ms']} ms | A4={s['total_a4_pdf']} | Single={s['total_single_pdf']}")
