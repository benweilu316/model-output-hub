# -*- coding: utf-8 -*-
"""
topic_rules.py — Universal AI Output Scanner (v2.0 Full-Deliverable Edition)
Indexes all 9 deliverable categories (HTML, PPTX, A4 PDF, Single PDF, Images/PNG/SVG,
Docs/Legends/Manuscripts, Data Tables, ZIP Archives, Scripts) with accurate NTFS subfolder
change detection, physical A4 mm classification, Fig-tag parsing, and Live Session Guard.
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

SCHEMA_VER = 4
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
NOISE_IMG_NAME_PAT = re.compile(
    r"(?:^page_?\d+|^slide_?\d+|^p\d+_s\d+|_thumb\b|^diff_|^mask_|^render_|_raw_tmp)",
    re.I,
)
PLUMBING_DOC_NAMES = {
    "state.md", "ledger.md", "donotretry.md", "manifest.txt", "prompt.txt",
    "cmakelists.txt", "requirements.txt", "license.txt", "package-lock.json",
}
PLUMBING_TABLE_PAT = re.compile(r"(?:_positions\.csv|_manifest\.tsv|_lint\.tsv)$", re.I)


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


_MEDIABOX_RE = re.compile(rb"/MediaBox\s*\[\s*([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s*\]")


def get_pdf_physical_info(fp: pathlib.Path, mtime: float, size_b: int, dim_cache: dict) -> tuple[float, float, int]:
    key = str(fp)
    hit = dim_cache.get(key)
    if hit and abs(hit.get("m", 0) - mtime) < 0.05 and hit.get("s") == size_b:
        return hit.get("w", 0.0), hit.get("h", 0.0), hit.get("p", 1)

    w_mm, h_mm, pages = 0.0, 0.0, 1
    if HAS_FITZ:
        try:
            doc = _fitz.open(str(fp))
            pages = len(doc)
            if pages > 0:
                r = doc[0].rect
                w_mm = round(r.width * 25.4 / 72.0, 1)
                h_mm = round(r.height * 25.4 / 72.0, 1)
            doc.close()
        except Exception:
            pass

    if w_mm <= 0:
        try:
            with open(fp, "rb") as f:
                chunk = f.read(16384)
                if size_b > 16384:
                    f.seek(max(0, size_b - 16384))
                    chunk += b" " + f.read(16384)
            m = _MEDIABOX_RE.search(chunk)
            if m:
                x0, y0, x1, y1 = (float(m.group(i)) for i in range(1, 5))
                w_mm = round(abs(x1 - x0) * 25.4 / 72.0, 1)
                h_mm = round(abs(y1 - y0) * 25.4 / 72.0, 1)
        except Exception:
            pass

    dim_cache[key] = {"m": round(mtime, 2), "s": size_b, "w": w_mm, "h": h_mm, "p": pages}
    return w_mm, h_mm, pages


_SLIDE_IMG_DIR_PAT = re.compile(r"(?:^|[/\\])(?:slides?[_-]?hd\d*|slides?[_-]?pair|slide[_-]?preview|offslide|deck[_-]?pages)(?:[/\\]|$)", re.I)
_SINGLE_FIG_RANGE_PAT = re.compile(r"(?:fig(?:ure)?|图)[_\s-]*s?\d+\s*[-~至到_]\s*s?\d+", re.I)


def parse_figure_tag(filename: str, rel_path: str = "", proj_name: str = "", is_doc_like: bool = False) -> tuple[str, str, int]:
    stem = pathlib.Path(filename).stem
    rel_norm = (rel_path or "").replace("\\", "/")
    rel_dir = rel_norm.rsplit("/", 1)[0] if "/" in rel_norm else ""
    combined = f"{rel_norm} {stem}"

    # Guard: PPT slide exports (e.g. slides_hd2000/v1_1125/s27.png) are slide pages, NOT Fig S27
    if _SLIDE_IMG_DIR_PAT.search(rel_norm) and re.fullmatch(r"(?:s|slide|page|p)?0*\d{1,3}(?:_.*)?", stem, re.I):
        return ("PPT 幻灯片页", "", 310)

    if re.search(r"(?:graphical[_\s-]*abstract|图形摘要|总览机制|fig[_\s-]*0\b|^GA(?:_|$))", combined, re.I):
        return ("GA 图形摘要", "", 0)

    m_ts = re.search(r"(?:table|tab|表)[_\s-]*s(\d+)", combined, re.I)
    if m_ts:
        num = int(m_ts.group(1))
        return (f"Table S{num}", "", 200 + num)

    m_tm = re.search(r"(?:table|tab|表)[_\s-]*(\d+)", combined, re.I)
    if m_tm:
        num = int(m_tm.group(1))
        return (f"Table {num}", "", 180 + num)

    def _extract_panel_from_stem(st: str, tail_hint: str = "") -> str:
        if tail_hint and re.fullmatch(r"[A-Za-z]{1,4}", tail_hint) and tail_hint.lower() not in ("v", "s", "dpi", "mm", "pt", "pdf", "png", "rev"):
            return tail_hint.upper()
        m_p = re.search(r"(?:^|_|-)(?:panel[_\s-]*)?([A-Z]{1,3})(?:_v\d+|_rev\d*|_10X|_clean|_revised|_exact|_standalone)?$", st)
        if m_p and m_p.group(1) not in ("A4", "QC", "LR", "GO", "KM", "ROC", "DEG", "UMI", "PDF", "PPT", "SOP", "RNA", "DNA", "LNP", "GA", "AI", "ML"):
            return m_p.group(1)
        return ""

    # 1) Supplementary Fig in stem -> parent rel_dir -> single-Fig proj_name
    m_fs = re.search(r"(?:fig(?:ure)?|图)[_\s-]*s(\d+)([A-Za-z])?", stem, re.I)
    if not m_fs and not _SLIDE_IMG_DIR_PAT.search(rel_norm):
        m_fs2 = re.search(r"(?:^|[_\s-])s(\d{1,2})(?:[_\s-]|$)", stem, re.I)
        if m_fs2 and int(m_fs2.group(1)) <= 20 and not is_doc_like:
            num = int(m_fs2.group(1))
            return (f"Fig S{num}", _extract_panel_from_stem(stem), 100 + num)
    if not m_fs and rel_dir:
        m_fs = re.search(r"(?:fig(?:ure)?|图)[_\s-]*s(\d+)", rel_dir, re.I)
    if not m_fs and proj_name and not _SINGLE_FIG_RANGE_PAT.search(proj_name):
        all_s = re.findall(r"(?:fig(?:ure)?|图)[_\s-]*s(\d+)", proj_name, re.I)
        if len(set(all_s)) == 1:
            m_fs = re.search(r"(?:fig(?:ure)?|图)[_\s-]*s(\d+)", proj_name, re.I)

    if m_fs:
        num = int(m_fs.group(1))
        sub = (m_fs.group(2) or "").upper() if m_fs.lastindex and m_fs.lastindex >= 2 else ""
        panel = _extract_panel_from_stem(stem, sub)
        return (f"Fig S{num}", panel, 100 + num)

    # 2) Main Fig in stem -> parent rel_dir -> single-Fig proj_name
    m_fm = re.search(r"(?:fig(?:ure)?|图)[_\s-]*(\d+)([A-Za-z]{0,4})", stem, re.I)
    if not m_fm and rel_dir:
        m_fm = re.search(r"(?:fig(?:ure)?|图)[_\s-]*(\d+)", rel_dir, re.I)
    if not m_fm and proj_name and not _SINGLE_FIG_RANGE_PAT.search(proj_name):
        all_m = re.findall(r"(?:fig(?:ure)?|图)[_\s-]*(\d+)", proj_name, re.I)
        if len(set(all_m)) == 1:
            m_fm = re.search(r"(?:fig(?:ure)?|图)[_\s-]*(\d+)", proj_name, re.I)

    if m_fm:
        num = int(m_fm.group(1))
        tail = m_fm.group(2) if m_fm.lastindex and m_fm.lastindex >= 2 else ""
        panel = _extract_panel_from_stem(stem, tail)
        return (f"Fig {num}", panel, num)

    m_sl = re.search(r"(?:slide|page)[_\s-]*0*(\d+)", stem, re.I)
    if m_sl:
        num = int(m_sl.group(1))
        return ("PPT 幻灯片页", f"P{num}", 310)

    if is_doc_like:
        return ("综合报告与整本交付", "", 999)
    return ("专题与汇总图", "", 90)


def is_full_page_a4_pdf(w_mm: float, h_mm: float, pages: int, stem: str, in_panel_dir: bool) -> bool:
    if in_panel_dir and re.search(r"_[A-Z]$|panel_[A-Z]", stem):
        return False
    if pages >= 2 and min(w_mm, h_mm) >= 140:
        return True
    if w_mm > 0 and h_mm > 0:
        short_side = min(w_mm, h_mm)
        long_side = max(w_mm, h_mm)
        if short_side >= 165.0 and long_side >= 225.0:
            return True
        return False
    if in_panel_dir:
        return False
    return bool(re.search(r"(?:_a4\b|a4拼图|整页|all_figs|汇总)", stem, re.I))


def load_user_meta() -> dict:
    if USER_META_FILE.is_file():
        try:
            return json.loads(USER_META_FILE.read_text(encoding="utf-8-sig"))
        except Exception:
            pass
    return {"starred": [], "starred_files": [], "topic_overrides": {}, "notes": {}}


def save_user_meta(meta: dict) -> None:
    HUB_APP_DIR.mkdir(parents=True, exist_ok=True)
    USER_META_FILE.write_text(json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8")


def classify_project(dir_name: str, parent_group: str = "", rel_key: str = "", user_overrides: dict | None = None) -> tuple[str, str]:
    if user_overrides and rel_key in user_overrides:
        ov = user_overrides[rel_key]
        if isinstance(ov, dict) and ov.get("topic_id") in TOPIC_BY_ID:
            return ov["topic_id"], ov.get("sub_branch", "00_手动指定分组")
        if isinstance(ov, str) and ov in TOPIC_BY_ID:
            return ov, "00_手动指定分组"

    hay = f"{parent_group} {dir_name}".lower()
    for rule in PROFILE.get("rules", []):
        kws = rule.get("any", [])
        if any(kw.lower() in hay for kw in kws):
            tid = rule["topic_id"]
            for br in rule.get("branches", []):
                if "default" in br:
                    return tid, br["default"]
                if any(bkw.lower() in hay for bkw in br.get("any", [])):
                    return tid, br["name"]
            return tid, "01_主线迭代归档"

    return (
        PROFILE.get("fallback_topic_id", "08_misc_projects"),
        PROFILE.get("fallback_branch", "01_独立任务与探索归档"),
    )


def extract_version_sort_key(name: str, mtime: float) -> tuple:
    m_v = re.findall(r"(?:^|[_\-\s])v(\d+)(?:\.(\d+))?", name, re.I)
    v_num = (int(m_v[-1][0]), int(m_v[-1][1] or 0)) if m_v else (0, 0)
    return (round(mtime, 1), v_num[0], v_num[1])


def get_active_locks(heartbeat_sec: int = 7200) -> dict:
    locked_paths: dict[str, dict] = {}
    herdr_agents: list[dict] = []

    def add_lock(path_str: str, reason: str, agent_name: str = "", gen: str = ""):
        try:
            p = pathlib.Path(path_str)
            if not p.exists():
                return
            norm = os.path.normcase(os.path.abspath(str(p)))
            entry = locked_paths.setdefault(norm, {"path": str(p), "reasons": [], "agent": "", "gen": ""})
            if reason not in entry["reasons"]:
                entry["reasons"].append(reason)
            if agent_name and not entry["agent"]:
                entry["agent"] = agent_name
            if gen and not entry["gen"]:
                entry["gen"] = gen
        except Exception:
            pass

    herdr_exe = os.path.join(os.environ.get("APPDATA", ""), "alma", "bin", "herdr.exe")
    if not os.path.isfile(herdr_exe):
        herdr_exe = shutil.which("herdr") or ""
    if herdr_exe:
        try:
            r = subprocess.run([herdr_exe, "agent", "list"], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=4)
            if r.returncode == 0 and r.stdout.strip().startswith("["):
                arr = json.loads(r.stdout)
                for a in arr:
                    st = a.get("status") or a.get("agent_status") or a.get("state") or ""
                    nm = a.get("name") or a.get("title") or a.get("id") or ""
                    pane = a.get("pane_id") or a.get("pane") or ""
                    herdr_agents.append({"name": nm, "status": st, "pane": pane})
        except Exception:
            pass

    ho_cfg = PROFILE.get("handoff_root") or os.environ.get("HANDOFFS_DIR") or ""
    handoffs_root = pathlib.Path(ho_cfg) if ho_cfg else None
    pane_bind_file = (handoffs_root / "pane_bindings.json") if handoffs_root else None
    if pane_bind_file and pane_bind_file.is_file():
        try:
            pb = json.loads(pane_bind_file.read_text(encoding="utf-8-sig"))
            working_panes = {a["pane"]: a for a in herdr_agents if a["status"] in ("working", "blocked")}
            entries = pb.values() if isinstance(pb, dict) else (pb if isinstance(pb, list) else [])
            for val in entries:
                if not isinstance(val, dict):
                    continue
                pane_id = val.get("pane") or val.get("pane_id") or ""
                proj = val.get("project") or val.get("topic") or ""
                wdir = val.get("work_dir") or val.get("output_dir") or ""
                if pane_id in working_panes and wdir:
                    ag = working_panes[pane_id]
                    add_lock(wdir, f"Herdr窗口运行中 ({ag['name']})", ag["name"])
                elif pane_id in working_panes and proj:
                    for cand in glob.glob(str(MODEL_OUT_ROOT / "*输出" / f"*{proj}*")):
                        add_lock(cand, f"Herdr活跃课题 ({working_panes[pane_id]['name']})", working_panes[pane_id]["name"])
        except Exception:
            pass

    if handoffs_root and handoffs_root.is_dir():
        path_rx = re.compile(r"[A-Za-z]:\\[^\r\n`\"'<>|*?\t]+Desktop\\模型输出\\[^\r\n`\"'<>|*?\t]+")
        now = time.time()
        for st_file in handoffs_root.glob("*/STATE.md"):
            try:
                age_h = (now - st_file.stat().st_mtime) / 3600.0
                if age_h > 36:
                    continue
                txt = st_file.read_text(encoding="utf-8", errors="ignore")
                head = txt[:1500]
                is_closed = bool(re.search(r"status\s*[:=]\s*(?:closed|completed|done)|已完结归档", head, re.I))
                if is_closed and age_h > 2.0:
                    continue
                m_gen = re.search(r"第\s*(\d+)\s*代", head)
                gen_str = f"第{m_gen.group(1)}代" if m_gen else ""
                for mp in path_rx.findall(txt):
                    clean_p = mp.rstrip("\\/.,;:)）]】 ")
                    p_obj = pathlib.Path(clean_p)
                    if p_obj.is_file():
                        p_obj = p_obj.parent
                    for anc in [p_obj] + list(p_obj.parents):
                        if anc.parent and anc.parent.parent == MODEL_OUT_ROOT:
                            add_lock(str(anc), f"交接链活跃 ({st_file.parent.name})", "", gen_str)
                            break
                        if anc.parent and anc.parent.parent and anc.parent.parent.parent == MODEL_OUT_ROOT:
                            add_lock(str(anc), f"交接链活跃 ({st_file.parent.name})", "", gen_str)
                            break
            except Exception:
                pass

    return {
        "locked_paths": locked_paths,
        "herdr_agents": herdr_agents,
        "heartbeat_sec": heartbeat_sec,
    }


def _extract_html_title(fp: pathlib.Path) -> str:
    try:
        with open(fp, "rb") as f:
            raw = f.read(8192).decode("utf-8", errors="ignore")
        m = re.search(r"<title>(.*?)</title>", raw, re.I | re.S)
        if m:
            t = re.sub(r"\s+", " ", m.group(1)).strip()
            if t:
                return t[:100]
    except Exception:
        pass
    return fp.stem


def _extract_project_change_hint(pdir: pathlib.Path, htmls: list[dict], proj_name: str) -> str:
    """Extracts a concise (<=46 chars) human-readable revision hint without dumping raw prompts."""
    if htmls:
        t = (htmls[0].get("title") or "").strip()
        if t and t != htmls[0].get("name") and len(t) >= 6:
            t_clean = re.sub(r"\s+", " ", t)
            if len(t_clean) > 46:
                t_clean = t_clean[:44] + "…"
            return t_clean
    for fname in ("polish-log.md", "CHANGELOG.md", "README.md", "_INDEX.md"):
        fp = pdir / fname
        if fp.is_file():
            try:
                head = fp.read_text(encoding="utf-8", errors="ignore")[:2400]
                for ln in head.splitlines():
                    s = ln.strip().lstrip("#>-*0123456789. ").strip()
                    if 6 <= len(s) <= 58 and not re.search(r"(?:http|C:\\|/data|用户原话|verbatim|sha1)", s, re.I):
                        return s[:46]
            except Exception:
                pass
    parts = [pt for pt in re.split(r"[_\-\s]+", proj_name) if pt and not re.fullmatch(r"20\d{6}|\d{4}", pt)]
    if len(parts) >= 2:
        return " / ".join(parts[-2:])[:44]
    return proj_name[:44]


def _infer_platform_info(platform_dir: str) -> tuple[str, str]:
    if platform_dir in PLATFORM_MAP:
        return PLATFORM_MAP[platform_dir]
    low = platform_dir.lower()
    for k, (lbl, col) in PLATFORM_MAP.items():
        if k.replace("输出", "") in low:
            return lbl, col
    clean = re.sub(r"(?:输出|[-_]?outputs?)$", "", platform_dir, flags=re.I).strip() or platform_dir
    return clean[:12], "#4A5568"


def _compute_quick_tree_sig(pdir: pathlib.Path) -> tuple[float, int]:
    """Checks mtime of pdir AND immediate child folders so subfolder writes on NTFS never stay stale."""
    max_mt = 0.0
    child_cnt = 0
    try:
        max_mt = pdir.stat().st_mtime
        with os.scandir(pdir) as it:
            for entry in it:
                child_cnt += 1
                try:
                    mt = entry.stat(follow_symlinks=False).st_mtime
                    if mt > max_mt:
                        max_mt = mt
                    if entry.is_dir(follow_symlinks=False) and not HARD_NOISE_DIR_PAT.match(entry.name):
                        with os.scandir(entry.path) as it2:
                            for e2 in it2:
                                child_cnt += 1
                                mt2 = e2.stat(follow_symlinks=False).st_mtime
                                if mt2 > max_mt:
                                    max_mt = mt2
                except OSError:
                    pass
    except OSError:
        pass
    return (round(max_mt, 2), child_cnt)


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

    htmls, pptxs, raw_a4_pdfs, raw_single_pdfs = [], [], [], []
    images, docs, tables, archives, scripts = [], [], [], [], []

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
            if in_hard_noise or fn.endswith((".b64", ".bak", ".pyc", ".log")):
                cache_bytes += sz
            if mt > latest_mtime and not fn.endswith(".pyc"):
                latest_mtime = mt
                latest_rel_file = str(fp.relative_to(pdir)).replace("\\", "/")

            if in_hard_noise or depth > 5 or sz == 0:
                continue

            ext = fp.suffix.lower()
            stem = fp.stem
            fn_low = fn.lower()
            rel_str = str(fp.relative_to(pdir)).replace("\\", "/")
            item_base = {
                "name": fn,
                "path": str(fp),
                "rel": rel_str,
                "size_mb": round(sz / 1048576.0, 2),
                "mtime": mt,
                "date": _dt.datetime.fromtimestamp(mt).strftime("%Y-%m-%d %H:%M"),
            }
            if ext in (".html", ".htm") and depth <= 4:
                item_base["title"] = _extract_html_title(fp)
                fg, fpnl, ford = parse_figure_tag(fn, rel_str, pdir.name, is_doc_like=True)
                item_base["fig_group"] = fg
                item_base["fig_panel"] = fpnl
                item_base["fig_order"] = ford
                htmls.append(item_base)
            elif ext in (".pptx", ".ppt") and depth <= 4:
                fg, fpnl, ford = parse_figure_tag(fn, rel_str, pdir.name, is_doc_like=True)
                item_base["fig_group"] = fg
                item_base["fig_panel"] = fpnl
                item_base["fig_order"] = ford
                pptxs.append(item_base)
            elif ext == ".pdf":
                if INTERMEDIATE_SLICE_PAT.search(stem):
                    continue
                w_mm, h_mm, pages = get_pdf_physical_info(fp, mt, sz, dim_cache)
                fig_group, fig_panel, fig_order = parse_figure_tag(fn, rel_str, pdir.name, is_doc_like=False)
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
            elif ext in (".png", ".jpg", ".jpeg", ".svg", ".webp") and depth <= 4 and not in_panel_dir:
                if not NOISE_IMG_NAME_PAT.search(stem):
                    fig_group, fig_panel, fig_order = parse_figure_tag(fn, rel_str, pdir.name, is_doc_like=False)
                    item_base["fig_group"] = fig_group
                    item_base["fig_panel"] = fig_panel
                    item_base["fig_order"] = fig_order
                    images.append(item_base)
            elif ext in (".docx", ".doc", ".md", ".txt", ".rtf") and depth <= 4:
                if fn_low not in PLUMBING_DOC_NAMES and not fn_low.endswith(("_lint.txt", ".log.txt")):
                    fg, fpnl, ford = parse_figure_tag(fn, rel_str, pdir.name, is_doc_like=True)
                    item_base["fig_group"] = fg
                    item_base["fig_panel"] = fpnl
                    item_base["fig_order"] = ford
                    docs.append(item_base)
            elif ext in (".xlsx", ".xls", ".csv", ".tsv") and depth <= 4:
                if not PLUMBING_TABLE_PAT.search(fn_low):
                    fg, fpnl, ford = parse_figure_tag(fn, rel_str, pdir.name, is_doc_like=True)
                    item_base["fig_group"] = fg
                    item_base["fig_panel"] = fpnl
                    item_base["fig_order"] = ford
                    tables.append(item_base)
            elif (ext in (".zip", ".7z", ".rar", ".tgz") or fn_low.endswith(".tar.gz")) and depth <= 4:
                item_base["fig_group"] = "综合报告与整本交付"
                item_base["fig_panel"] = ""
                item_base["fig_order"] = 999
                archives.append(item_base)
            elif ext in (".r", ".py", ".sh", ".ipynb") and depth <= 4:
                fg, fpnl, ford = parse_figure_tag(fn, rel_str, pdir.name, is_doc_like=True)
                item_base["fig_group"] = fg
                item_base["fig_panel"] = fpnl
                item_base["fig_order"] = ford
                scripts.append(item_base)

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

    a4_pdfs = dedup_pdfs(raw_a4_pdfs, 80)
    single_pdfs = dedup_pdfs(raw_single_pdfs, 120)
    for arr in (htmls, pptxs, images, docs, tables, archives, scripts):
        arr.sort(key=lambda x: x["mtime"], reverse=True)

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
        "change_hint": _extract_project_change_hint(pdir, htmls, pdir.name),
        "htmls": htmls[:30],
        "pptxs": pptxs[:20],
        "a4_pdfs": a4_pdfs,
        "single_pdfs": single_pdfs,
        "pdfs": (a4_pdfs + single_pdfs)[:30],
        "images": images[:40],
        "docs": docs[:40],
        "tables": tables[:25],
        "archives": archives[:20],
        "scripts": scripts[:25],
        "counts": {
            "html": len(htmls),
            "pptx": len(pptxs),
            "a4_pdf": len(a4_pdfs),
            "single_pdf": len(single_pdfs),
            "pdf": len(a4_pdfs) + len(single_pdfs),
            "image": len(images),
            "doc": len(docs),
            "table": len(tables),
            "archive": len(archives),
            "script": len(scripts),
        },
        "_schema_ver": SCHEMA_VER,
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
                if p.get("_schema_ver") == SCHEMA_VER:
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
            sig_mt, sig_cnt = _compute_quick_tree_sig(pdir)
        except OSError:
            return
        p_str = str(pdir)
        cached = old_cache.get(p_str)
        if (
            cached
            and abs(cached.get("_sig_mt", 0) - sig_mt) < 0.02
            and cached.get("_sig_cnt") == sig_cnt
            and (now_ts - cached.get("mtime", now_ts) > 14400)
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
        rec["_sig_mt"] = sig_mt
        rec["_sig_cnt"] = sig_cnt
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
            "image_count": sum(x["counts"].get("image", 0) for x in t_projs),
            "doc_count": sum(x["counts"].get("doc", 0) for x in t_projs),
            "table_count": sum(x["counts"].get("table", 0) for x in t_projs),
            "archive_count": sum(x["counts"].get("archive", 0) for x in t_projs),
            "script_count": sum(x["counts"].get("script", 0) for x in t_projs),
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
            "total_html": sum(p["counts"]["html"] for p in projects),
            "total_pptx": sum(p["counts"]["pptx"] for p in projects),
            "total_a4_pdf": sum(p["counts"]["a4_pdf"] for p in projects),
            "total_single_pdf": sum(p["counts"]["single_pdf"] for p in projects),
            "total_pdf": sum(p["counts"]["pdf"] for p in projects),
            "total_image": sum(p["counts"].get("image", 0) for p in projects),
            "total_doc": sum(p["counts"].get("doc", 0) for p in projects),
            "total_table": sum(p["counts"].get("table", 0) for p in projects),
            "total_archive": sum(p["counts"].get("archive", 0) for p in projects),
            "total_script": sum(p["counts"].get("script", 0) for p in projects),
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
    print(
        f"Scanned {s['total_projects']} projects in {res['scan_ms']} ms | "
        f"HTML={s['total_html']} PPTX={s['total_pptx']} A4={s['total_a4_pdf']} Single={s['total_single_pdf']} "
        f"IMG={s['total_image']} DOC={s['total_doc']} TBL={s['total_table']} ZIP={s['total_archive']}"
    )
