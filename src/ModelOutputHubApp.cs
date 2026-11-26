// -*- coding: utf-8 -*-
// ModelOutputHubApp.cs — Model Output Hub (v1.2 Single-File Zero-Dependency Edition)
// 1. All WebView2 managed & native DLLs embedded inside single .exe (100% immune to Windows MOTW Zone.Identifier & ZIP-preview launch!)
// 2. Built-in pure C# NativeScanner (works out-of-the-box with ZERO Python installation required; seamlessly delegates to Python when private_profile.json is present)
// 3. Folder picker dialog + System Tray Resident + Single-Instance Wakeup + Global Crash Guard

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WinForms = System.Windows.Forms;

namespace ModelOutputHub
{
    public static class Program
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string AppID);

        private static string s_appLocalDataDir;
        private static string s_fallbackLibDir;
        private static readonly Dictionary<string, Assembly> s_loadedAssemblies = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        [STAThread]
        public static int Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    WinForms.MessageBox.Show(
                        "模型输出管家启动异常：\n" + Convert.ToString(e.ExceptionObject),
                        "模型输出管家",
                        WinForms.MessageBoxButtons.OK,
                        WinForms.MessageBoxIcon.Error);
                }
                catch { }
            };

            try
            {
                try { SetCurrentProcessExplicitAppUserModelID("ModelOutputHub.Studio.Desktop.v1_2"); } catch { }

                s_appLocalDataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ModelOutputHub");
                string nativeDir = Path.Combine(s_appLocalDataDir, @"runtime\win-x64");
                Directory.CreateDirectory(nativeDir);

                // Extract embedded native WebView2Loader.dll into LocalAppData (guaranteed writable & free of Zone.Identifier MOTW!)
                ExtractEmbeddedResourceToFile("ModelOutputHub.Wv2Loader", Path.Combine(nativeDir, "WebView2Loader.dll"));
                SetDllDirectory(nativeDir);

                string exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
                s_fallbackLibDir = Path.Combine(exeDir, @"_hub_app\lib");

                AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
                return BootRunner.Run(args ?? new string[0], exeDir, s_appLocalDataDir);
            }
            catch (Exception ex)
            {
                WinForms.MessageBox.Show(
                    "启动初始化失败：\n" + ex.Message + "\n\n请确认系统为 64 位 Windows 10/11。",
                    "模型输出管家",
                    WinForms.MessageBoxButtons.OK,
                    WinForms.MessageBoxIcon.Error);
                return 1;
            }
        }

        private static void ExtractEmbeddedResourceToFile(string resourceName, string targetPath)
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                {
                    if (s == null) return;
                    if (File.Exists(targetPath) && new FileInfo(targetPath).Length == s.Length)
                    {
                        return;
                    }
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        File.WriteAllBytes(targetPath, ms.ToArray());
                    }
                }
            }
            catch { }
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                string shortName = new AssemblyName(args.Name).Name;
                Assembly cached;
                if (s_loadedAssemblies.TryGetValue(shortName, out cached))
                    return cached;

                string resKey = null;
                if (shortName.Equals("Microsoft.Web.WebView2.Core", StringComparison.OrdinalIgnoreCase))
                    resKey = "ModelOutputHub.Wv2Core";
                else if (shortName.Equals("Microsoft.Web.WebView2.Wpf", StringComparison.OrdinalIgnoreCase))
                    resKey = "ModelOutputHub.Wv2Wpf";

                if (resKey != null)
                {
                    using (Stream rs = Assembly.GetExecutingAssembly().GetManifestResourceStream(resKey))
                    {
                        if (rs != null)
                        {
                            using (var ms = new MemoryStream())
                            {
                                rs.CopyTo(ms);
                                // Byte-array Assembly.Load bypasses Windows Zone.Identifier (MOTW) sandbox completely!
                                Assembly asm = Assembly.Load(ms.ToArray());
                                s_loadedAssemblies[shortName] = asm;
                                return asm;
                            }
                        }
                    }
                }

                // Fallback to disk bytes if present
                string candidate = Path.Combine(s_fallbackLibDir, shortName + ".dll");
                if (File.Exists(candidate))
                {
                    Assembly asm = Assembly.Load(File.ReadAllBytes(candidate));
                    s_loadedAssemblies[shortName] = asm;
                    return asm;
                }
            }
            catch { }
            return null;
        }
    }

    internal static class BootRunner
    {
        private const string MutexName = @"Local\ModelOutputHub_SingleInstance_Mutex_v1";
        private const string WakeEventName = @"Local\ModelOutputHub_Wakeup_Event_v1";

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Run(string[] args, string exeDir, string appLocalDataDir)
        {
            string rootDir = ResolveInitialRootDir(args, exeDir, appLocalDataDir);

            if (args.Any(a => a == "--selftest"))
            {
                return RunSelfTest(rootDir);
            }

            string previewDir = null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--render-preview")
                {
                    previewDir = args[i + 1];
                    break;
                }
            }

            // Check WebView2 Runtime availability gracefully before opening window
            string wvVer = null;
            try { wvVer = CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch { }
            if (string.IsNullOrEmpty(wvVer))
            {
                var dr = WinForms.MessageBox.Show(
                    "检测到当前电脑尚未安装 Microsoft Edge WebView2 运行时组件。\n\n是否立即打开微软官方页面一键安装？（安装仅需 10 秒）",
                    "模型输出管家 — 缺少系统运行组件",
                    WinForms.MessageBoxButtons.YesNo,
                    WinForms.MessageBoxIcon.Information);
                if (dr == WinForms.DialogResult.Yes)
                {
                    try { Process.Start(new ProcessStartInfo("https://go.microsoft.com/fwlink/p/?LinkId=2124703") { UseShellExecute = true }); } catch { }
                }
                return 2;
            }

            Mutex instanceMutex = null;
            EventWaitHandle wakeEvent = null;

            if (string.IsNullOrEmpty(previewDir))
            {
                bool createdNew;
                instanceMutex = new Mutex(true, MutexName, out createdNew);
                wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName);
                if (!createdNew)
                {
                    try { wakeEvent.Set(); } catch { }
                    return 0;
                }
            }

            try
            {
                var app = new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                var win = new StudioWindow(rootDir, appLocalDataDir, previewDir, wakeEvent);
                app.MainWindow = win;
                app.Run(win);
                return win.ExitCode;
            }
            finally
            {
                if (wakeEvent != null) { try { wakeEvent.Dispose(); } catch { } }
                if (instanceMutex != null) { try { instanceMutex.ReleaseMutex(); instanceMutex.Dispose(); } catch { } }
            }
        }

        private static string ResolveInitialRootDir(string[] args, string exeDir, string appLocalDataDir)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--root" && Directory.Exists(args[i + 1]))
                {
                    return Path.GetFullPath(args[i + 1]);
                }
            }

            string envRoot = Environment.GetEnvironmentVariable("MODEL_OUTPUT_ROOT");
            if (!string.IsNullOrEmpty(envRoot) && Directory.Exists(envRoot))
            {
                return Path.GetFullPath(envRoot);
            }

            // If exe sits directly inside a configured Model Output directory (has _hub_app or *输出 subdirs), use exeDir!
            if (Directory.Exists(Path.Combine(exeDir, "_hub_app")) || HasPlatformSubdirs(exeDir))
            {
                return exeDir;
            }

            // Otherwise check if user previously picked a root folder via FolderBrowserDialog
            string settingsPath = Path.Combine(appLocalDataDir, "app_settings.json");
            if (File.Exists(settingsPath))
            {
                try
                {
                    var js = new JavaScriptSerializer();
                    var cfg = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(settingsPath, Encoding.UTF8));
                    if (cfg != null && cfg.ContainsKey("watched_root"))
                    {
                        string saved = Convert.ToString(cfg["watched_root"]);
                        if (!string.IsNullOrEmpty(saved) && Directory.Exists(saved))
                        {
                            return saved;
                        }
                    }
                }
                catch { }
            }

            // If launched from a temporary ZIP preview folder (%TEMP%) or Downloads, default to Desktop\模型输出 if it exists, else exeDir
            string tempDir = Path.GetTempPath().TrimEnd('\\', '/');
            if (exeDir.StartsWith(tempDir, StringComparison.OrdinalIgnoreCase))
            {
                string deskCandidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "模型输出");
                if (Directory.Exists(deskCandidate)) return deskCandidate;
                return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            }

            return exeDir;
        }

        private static bool HasPlatformSubdirs(string dir)
        {
            try
            {
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    string n = Path.GetFileName(sub);
                    if (n.EndsWith("输出", StringComparison.OrdinalIgnoreCase) ||
                        n.EndsWith("_output", StringComparison.OrdinalIgnoreCase) ||
                        n.EndsWith("-output", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static int RunSelfTest(string rootDir)
        {
            try
            {
                string ver = CoreWebView2Environment.GetAvailableBrowserVersionString();
                bool hasRes;
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ModelOutputHub.StudioUI"))
                {
                    hasRes = (s != null && s.Length > 2048);
                }
                string sampleJson = NativeScanner.ScanDirectoryToJson(rootDir, new Dictionary<string, object>());
                bool scanOk = !string.IsNullOrEmpty(sampleJson) && sampleJson.Contains("\"summary\"");
                Console.WriteLine(string.Format("SELFTEST_OK wv2={0} single_file=True native_scanner={1}", ver, scanOk));
                return (!string.IsNullOrEmpty(ver) && hasRes && scanOk) ? 0 : 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("SELFTEST_ERR: " + ex);
                return 1;
            }
        }
    }

    /// <summary>
    /// Built-in Zero-Python High-Speed Pure C# Scanner.
    /// Guarantees full functionality on any clean Windows PC without Python installed!
    /// </summary>
    internal static class NativeScanner
    {
        private static readonly Regex s_noiseDirRx = new Regex(
            @"^(?:_backups?|backups?|_snapshots?|snapshots?|\.archive.*|_archive.*|_draft_fail|__pycache__|\.git|node_modules|render.*|inspect.*|panel_pngs.*|offslide_imgs|lint_pages|checker_pngs|_slides_.*|_workroom|_probe.*|qa.*|logs|.*备份.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex s_panelDirRx = new Regex(
            @"^(?:panels?|pdf_panels?|single_panels?|单面板.*|.*单图.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex s_sliceRx = new Regex(
            @"(?:_notag|_noletter|_raw_tmp|_NA)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex s_titleRx = new Regex(
            @"<title>(.*?)</title>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex s_mediaBoxRx = new Regex(
            @"/MediaBox\s*\[\s*([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s*\]",
            RegexOptions.Compiled);

        private class TopicDef
        {
            public string id;
            public string folder;
            public string shortName;
            public string title;
            public string color;
            public string[] keywords;
        }

        private static readonly TopicDef[] s_defaultTopics = new[]
        {
            new TopicDef { id = "01_research_survey", folder = "01_深度调研与文献综述", shortName = "深度调研 / 文献综述", title = "领域调研报告、论文精读、技术选型对比与知识蒸馏", color = "#2E5B88", keywords = new[] { "调研", "综述", "论文", "文献", "精读", "survey", "review", "paper", "benchmark", "蒸馏" } },
            new TopicDef { id = "02_data_viz", folder = "02_数据分析与可视化组图", shortName = "数据分析 / 科学组图", title = "统计建模、数据清洗、出版级矢量图表与实验图谱", color = "#1E513F", keywords = new[] { "数据", "分析", "统计", "图", "可视化", "plot", "fig", "chart", "umap", "rna", "seq", "组学", "实验" } },
            new TopicDef { id = "03_slides_docs", folder = "03_演示汇报与文档撰写", shortName = "演示汇报 / 讲义文档", title = "PPTX 幻灯片、项目汇报、方案总结与长文手稿", color = "#8C5A32", keywords = new[] { "ppt", "slide", "汇报", "演示", "答辩", "总结", "讲义", "稿", "doc", "report" } },
            new TopicDef { id = "04_web_apps", folder = "04_前端界面与交互应用", shortName = "前端界面 / 交互原型", title = "交互式看板、桌面小程序、UI 设计稿与全栈原型", color = "#713E50", keywords = new[] { "前端", "界面", "网页", "应用", "ui", "ux", "web", "app", "dashboard", "看板", "主题", "设计" } },
            new TopicDef { id = "05_code_automation", folder = "05_工程代码与自动化脚本", shortName = "工程架构 / 自动脚本", title = "后端工具、数据管线、爬虫自动化与性能调优", color = "#4A6B5D", keywords = new[] { "脚本", "工具", "自动", "代码", "重构", "api", "cli", "server", "bot", "爬", "pipeline" } },
            new TopicDef { id = "06_agent_workflow", folder = "06_Agent工作流与提示词", shortName = "Agent架构 / 规则技能", title = "多智能体编排、Skill 技能包、评测基准与配置优化", color = "#4A5568", keywords = new[] { "agent", "skill", "prompt", "workflow", "claude", "gemini", "agy", "grok", "mcp", "hook", "记忆", "调度", "管家" } },
            new TopicDef { id = "07_daily_ops", folder = "07_日常巡检与个人资料", shortName = "日常值守 / 个人资料", title = "日报周报、系统巡检、学习笔记与个人档案", color = "#5C6B57", keywords = new[] { "日报", "周报", "值守", "巡检", "笔记", "课程", "学习", "简历", "resume", "note" } },
            new TopicDef { id = "08_misc_projects", folder = "08_其他独立探索项目", shortName = "独立探索 / 其他项目", title = "未归入固定主线的独立任务与临时实验", color = "#78716C", keywords = new string[0] }
        };

        public static string ScanDirectoryToJson(string rootDir, Dictionary<string, object> userMeta)
        {
            var sw = Stopwatch.StartNew();
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var starredSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (userMeta != null && userMeta.ContainsKey("starred") && userMeta["starred"] is System.Collections.IEnumerable)
            {
                foreach (object o in (System.Collections.IEnumerable)userMeta["starred"])
                {
                    if (o != null) starredSet.Add(Convert.ToString(o));
                }
            }
            var notesMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (userMeta != null && userMeta.ContainsKey("notes") && userMeta["notes"] is Dictionary<string, object>)
            {
                foreach (var kv in (Dictionary<string, object>)userMeta["notes"])
                {
                    notesMap[kv.Key] = Convert.ToString(kv.Value ?? "");
                }
            }

            var projects = new List<Dictionary<string, object>>();
            double nowUnix = ToUnixTime(DateTime.UtcNow);

            if (Directory.Exists(rootDir))
            {
                var platDirs = new List<string>();
                foreach (string d in Directory.GetDirectories(rootDir))
                {
                    string dn = Path.GetFileName(d);
                    if (dn.StartsWith("00_") || dn.StartsWith("_") || dn.StartsWith(".")) continue;
                    if (dn.EndsWith("输出", StringComparison.OrdinalIgnoreCase) ||
                        dn.EndsWith("_output", StringComparison.OrdinalIgnoreCase) ||
                        dn.EndsWith("-output", StringComparison.OrdinalIgnoreCase))
                    {
                        platDirs.Add(d);
                    }
                }

                if (platDirs.Count > 0)
                {
                    foreach (string pd in platDirs)
                    {
                        string pName = Path.GetFileName(pd);
                        try
                        {
                            foreach (string child in Directory.GetDirectories(pd))
                            {
                                string cn = Path.GetFileName(child);
                                if (cn.StartsWith(".") || cn.StartsWith("_")) continue;
                                projects.Add(ScanSingleProject(child, rootDir, pName, starredSet, notesMap, nowUnix));
                            }
                        }
                        catch { }
                    }
                }
                else
                {
                    try
                    {
                        foreach (string child in Directory.GetDirectories(rootDir))
                        {
                            string cn = Path.GetFileName(child);
                            if (cn.StartsWith("00_") || cn.StartsWith("_") || cn.StartsWith(".") ||
                                cn.Equals("runtime", StringComparison.OrdinalIgnoreCase) ||
                                cn.Equals("wv2_profile", StringComparison.OrdinalIgnoreCase))
                                continue;
                            projects.Add(ScanSingleProject(child, rootDir, "本地项目", starredSet, notesMap, nowUnix));
                        }
                    }
                    catch { }
                }
            }

            projects.Sort((a, b) => Convert.ToDouble(b["mtime"]).CompareTo(Convert.ToDouble(a["mtime"])));

            var topicTree = new List<Dictionary<string, object>>();
            foreach (var td in s_defaultTopics)
            {
                var tProjs = projects.Where(p => Convert.ToString(p["topic_id"]) == td.id).ToList();
                var brGroups = new Dictionary<string, List<Dictionary<string, object>>>();
                foreach (var p in tProjs)
                {
                    string sb = Convert.ToString(p["sub_branch"]);
                    if (!brGroups.ContainsKey(sb)) brGroups[sb] = new List<Dictionary<string, object>>();
                    brGroups[sb].Add(p);
                }

                var brList = new List<Dictionary<string, object>>();
                foreach (var kv in brGroups.OrderBy(k => k.Key))
                {
                    brList.Add(new Dictionary<string, object>
                    {
                        { "name", kv.Key },
                        { "count", kv.Value.Count },
                        { "locked_count", kv.Value.Count(x => Convert.ToBoolean(x["locked"])) },
                        { "projects", kv.Value }
                    });
                }

                int htmlCnt = tProjs.Sum(x => ((Dictionary<string, int>)x["counts"])["html"]);
                int pptCnt = tProjs.Sum(x => ((Dictionary<string, int>)x["counts"])["pptx"]);
                int a4Cnt = tProjs.Sum(x => ((Dictionary<string, int>)x["counts"])["a4_pdf"]);
                int sgCnt = tProjs.Sum(x => ((Dictionary<string, int>)x["counts"])["single_pdf"]);

                topicTree.Add(new Dictionary<string, object>
                {
                    { "id", td.id },
                    { "folder", td.folder },
                    { "short", td.shortName },
                    { "title", td.title },
                    { "color", td.color },
                    { "project_count", tProjs.Count },
                    { "locked_count", tProjs.Count(x => Convert.ToBoolean(x["locked"])) },
                    { "starred_count", tProjs.Count(x => Convert.ToBoolean(x["starred"])) },
                    { "html_count", htmlCnt },
                    { "pptx_count", pptCnt },
                    { "a4_pdf_count", a4Cnt },
                    { "single_pdf_count", sgCnt },
                    { "pdf_count", a4Cnt + sgCnt },
                    { "size_mb", Math.Round(tProjs.Sum(x => Convert.ToDouble(x["size_mb"])), 1) },
                    { "cache_mb", Math.Round(tProjs.Sum(x => Convert.ToDouble(x["cache_mb"])), 1) },
                    { "branches", brList }
                });
            }

            int sumHtml = projects.Sum(x => ((Dictionary<string, int>)x["counts"])["html"]);
            int sumPpt = projects.Sum(x => ((Dictionary<string, int>)x["counts"])["pptx"]);
            int sumA4 = projects.Sum(x => ((Dictionary<string, int>)x["counts"])["a4_pdf"]);
            int sumSg = projects.Sum(x => ((Dictionary<string, int>)x["counts"])["single_pdf"]);

            var result = new Dictionary<string, object>
            {
                { "generated_at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                { "scan_ms", (int)sw.ElapsedMilliseconds },
                { "root_path", rootDir },
                { "summary", new Dictionary<string, object>
                    {
                        { "total_projects", projects.Count },
                        { "locked_projects", projects.Count(x => Convert.ToBoolean(x["locked"])) },
                        { "starred_projects", projects.Count(x => Convert.ToBoolean(x["starred"])) },
                        { "total_html", sumHtml },
                        { "total_pptx", sumPpt },
                        { "total_a4_pdf", sumA4 },
                        { "total_single_pdf", sumSg },
                        { "total_pdf", sumA4 + sumSg },
                        { "total_size_gb", Math.Round(projects.Sum(x => Convert.ToDouble(x["size_mb"])) / 1024.0, 2) },
                        { "total_cache_gb", Math.Round(projects.Sum(x => Convert.ToDouble(x["cache_mb"])) / 1024.0, 2) },
                        { "dup_size_mb", 0.0 }
                    }
                },
                { "herdr_agents", new object[0] },
                { "duplicates", new object[0] },
                { "topics", topicTree },
                { "projects", projects }
            };

            return serializer.Serialize(result);
        }

        private static Dictionary<string, object> ScanSingleProject(
            string pdir, string rootDir, string platformDir,
            HashSet<string> starredSet, Dictionary<string, string> notesMap, double nowUnix)
        {
            string projName = Path.GetFileName(pdir);
            string relKey = pdir.StartsWith(rootDir, StringComparison.OrdinalIgnoreCase)
                ? pdir.Substring(rootDir.Length).TrimStart('\\', '/').Replace('\\', '/')
                : projName;

            string topicId = "08_misc_projects";
            string lowName = projName.ToLowerInvariant();
            foreach (var td in s_defaultTopics)
            {
                if (td.keywords.Any(k => lowName.Contains(k)))
                {
                    topicId = td.id;
                    break;
                }
            }
            string subBranch = "01_主线迭代归档";
            string platLabel = InferPlatformLabel(platformDir);

            int totalFiles = 0;
            long totalBytes = 0;
            long cacheBytes = 0;
            double latestMtime = 0;
            string latestFile = "";

            var htmls = new List<Dictionary<string, object>>();
            var pptxs = new List<Dictionary<string, object>>();
            var a4Pdfs = new List<Dictionary<string, object>>();
            var sgPdfs = new List<Dictionary<string, object>>();

            WalkProjectFiles(pdir, pdir, 0, false, false, ref totalFiles, ref totalBytes, ref cacheBytes, ref latestMtime, ref latestFile, htmls, pptxs, a4Pdfs, sgPdfs);

            htmls.Sort((a, b) => Convert.ToDouble(b["mtime"]).CompareTo(Convert.ToDouble(a["mtime"])));
            pptxs.Sort((a, b) => Convert.ToDouble(b["mtime"]).CompareTo(Convert.ToDouble(a["mtime"])));
            a4Pdfs.Sort((a, b) => Convert.ToDouble(b["mtime"]).CompareTo(Convert.ToDouble(a["mtime"])));
            sgPdfs.Sort((a, b) => Convert.ToDouble(b["mtime"]).CompareTo(Convert.ToDouble(a["mtime"])));

            if (latestMtime <= 0)
            {
                try { latestMtime = ToUnixTime(Directory.GetLastWriteTimeUtc(pdir)); } catch { latestMtime = nowUnix; }
            }

            bool isLocked = (nowUnix - latestMtime) <= 7200;
            var lockReasons = new List<string>();
            if (isLocked)
            {
                int mins = Math.Max(1, (int)((nowUnix - latestMtime) / 60));
                lockReasons.Add(string.Format("最近写入 ({0}分钟前: {1})", mins, string.IsNullOrEmpty(latestFile) ? projName : latestFile));
            }

            DateTime dtLocal = FromUnixTimeLocal(latestMtime);
            string noteVal;
            notesMap.TryGetValue(relKey, out noteVal);

            var counts = new Dictionary<string, int>
            {
                { "html", htmls.Count },
                { "pptx", pptxs.Count },
                { "a4_pdf", a4Pdfs.Count },
                { "single_pdf", sgPdfs.Count },
                { "pdf", a4Pdfs.Count + sgPdfs.Count },
                { "doc", 0 }
            };

            return new Dictionary<string, object>
            {
                { "key", relKey },
                { "name", projName },
                { "parent_group", "" },
                { "path", pdir },
                { "platform_dir", platformDir },
                { "platform", platLabel },
                { "platform_color", "#2E5B88" },
                { "topic_id", topicId },
                { "sub_branch", subBranch },
                { "total_files", totalFiles },
                { "size_mb", Math.Round(totalBytes / 1048576.0, 2) },
                { "cache_mb", Math.Round(cacheBytes / 1048576.0, 2) },
                { "mtime", latestMtime },
                { "date", dtLocal.ToString("yyyy-MM-dd") },
                { "datetime", dtLocal.ToString("yyyy-MM-dd HH:mm") },
                { "latest_file", latestFile },
                { "locked", isLocked },
                { "lock_reasons", lockReasons },
                { "starred", starredSet.Contains(relKey) },
                { "note", noteVal ?? "" },
                { "htmls", htmls.Take(10).ToList() },
                { "pptxs", pptxs.Take(8).ToList() },
                { "a4_pdfs", a4Pdfs.Take(40).ToList() },
                { "single_pdfs", sgPdfs.Take(60).ToList() },
                { "pdfs", a4Pdfs.Concat(sgPdfs).Take(20).ToList() },
                { "docs", new object[0] },
                { "counts", counts }
            };
        }

        private static void WalkProjectFiles(
            string rootProjDir, string curDir, int depth, bool inNoise, bool inPanel,
            ref int totalFiles, ref long totalBytes, ref long cacheBytes, ref double latestMtime, ref string latestFile,
            List<Dictionary<string, object>> htmls, List<Dictionary<string, object>> pptxs,
            List<Dictionary<string, object>> a4Pdfs, List<Dictionary<string, object>> sgPdfs)
        {
            if (depth > 5) return;
            try
            {
                foreach (string fp in Directory.GetFiles(curDir))
                {
                    string fn = Path.GetFileName(fp);
                    if (fn.StartsWith("~$")) continue;
                    FileInfo fi;
                    try { fi = new FileInfo(fp); } catch { continue; }
                    long sz = fi.Length;
                    double mt = ToUnixTime(fi.LastWriteTimeUtc);
                    totalFiles++;
                    totalBytes += sz;

                    string ext = (fi.Extension ?? "").ToLowerInvariant();
                    if (inNoise || ext == ".b64" || ext == ".tgz" || ext == ".bak" || ext == ".pyc" || ext == ".log")
                        cacheBytes += sz;

                    string relStr = fp.Substring(rootProjDir.Length).TrimStart('\\', '/').Replace('\\', '/');
                    if (mt > latestMtime && ext != ".pyc")
                    {
                        latestMtime = mt;
                        latestFile = relStr;
                    }

                    if (inNoise || depth > 4 || sz == 0) continue;

                    string stem = Path.GetFileNameWithoutExtension(fn);
                    string dtStr = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm");

                    if ((ext == ".html" || ext == ".htm") && depth <= 3)
                    {
                        htmls.Add(new Dictionary<string, object>
                        {
                            { "name", fn },
                            { "title", ExtractHtmlTitle(fp, stem) },
                            { "path", fp },
                            { "rel", relStr },
                            { "size_mb", Math.Round(sz / 1048576.0, 2) },
                            { "mtime", mt },
                            { "date", dtStr }
                        });
                    }
                    else if (ext == ".pptx" && depth <= 3)
                    {
                        pptxs.Add(new Dictionary<string, object>
                        {
                            { "name", fn },
                            { "title", fn },
                            { "path", fp },
                            { "rel", relStr },
                            { "size_mb", Math.Round(sz / 1048576.0, 2) },
                            { "mtime", mt },
                            { "date", dtStr }
                        });
                    }
                    else if (ext == ".pdf")
                    {
                        if (s_sliceRx.IsMatch(stem)) continue;
                        double wMm, hMm;
                        ReadPdfMediaBoxMm(fp, sz, out wMm, out hMm);
                        string figGroup, figPanel;
                        int figOrder;
                        ParseFigureTag(fn, relStr, out figGroup, out figPanel, out figOrder);
                        string dimLabel = (wMm > 0 && hMm > 0) ? string.Format("{0}×{1}mm", (int)Math.Round(wMm), (int)Math.Round(hMm)) : "";
                        bool isA4 = IsFullPageA4(wMm, hMm, stem, inPanel);

                        var item = new Dictionary<string, object>
                        {
                            { "name", fn },
                            { "path", fp },
                            { "rel", relStr },
                            { "size_mb", Math.Round(sz / 1048576.0, 2) },
                            { "mtime", mt },
                            { "date", dtStr },
                            { "dim_mm", dimLabel },
                            { "fig_group", figGroup },
                            { "fig_panel", figPanel },
                            { "fig_order", figOrder },
                            { "kind", isA4 ? "a4" : "single" }
                        };
                        if (isA4) a4Pdfs.Add(item); else sgPdfs.Add(item);
                    }
                }

                foreach (string sub in Directory.GetDirectories(curDir))
                {
                    FileAttributes attr;
                    try { attr = File.GetAttributes(sub); } catch { continue; }
                    if ((attr & FileAttributes.ReparsePoint) != 0) continue;

                    string dn = Path.GetFileName(sub);
                    bool nextNoise = inNoise || s_noiseDirRx.IsMatch(dn);
                    bool nextPanel = inPanel || s_panelDirRx.IsMatch(dn);
                    WalkProjectFiles(rootProjDir, sub, depth + 1, nextNoise, nextPanel, ref totalFiles, ref totalBytes, ref cacheBytes, ref latestMtime, ref latestFile, htmls, pptxs, a4Pdfs, sgPdfs);
                }
            }
            catch { }
        }

        private static string ExtractHtmlTitle(string fp, string fallback)
        {
            try
            {
                byte[] buf = new byte[6144];
                int n;
                using (var fs = new FileStream(fp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    n = fs.Read(buf, 0, buf.Length);
                }
                string head = Encoding.UTF8.GetString(buf, 0, n);
                Match m = s_titleRx.Match(head);
                if (m.Success)
                {
                    string t = Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim();
                    if (!string.IsNullOrEmpty(t)) return t.Length > 90 ? t.Substring(0, 90) : t;
                }
            }
            catch { }
            return fallback;
        }

        private static void ReadPdfMediaBoxMm(string fp, long sz, out double wMm, out double hMm)
        {
            wMm = 0; hMm = 0;
            try
            {
                int headLen = (int)Math.Min(16384, sz);
                byte[] buf = new byte[headLen];
                using (var fs = new FileStream(fp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    fs.Read(buf, 0, headLen);
                    string s = Encoding.ASCII.GetString(buf);
                    if (TryParseMediaBoxString(s, out wMm, out hMm)) return;

                    if (sz > 16384)
                    {
                        int tailLen = (int)Math.Min(16384, sz - 16384);
                        byte[] tail = new byte[tailLen];
                        fs.Seek(-tailLen, SeekOrigin.End);
                        fs.Read(tail, 0, tailLen);
                        if (TryParseMediaBoxString(Encoding.ASCII.GetString(tail), out wMm, out hMm)) return;
                    }
                }
            }
            catch { }
        }

        private static bool TryParseMediaBoxString(string text, out double wMm, out double hMm)
        {
            wMm = 0; hMm = 0;
            Match m = s_mediaBoxRx.Match(text);
            if (!m.Success) return false;
            double x0, y0, x1, y1;
            if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out x0) &&
                double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out y0) &&
                double.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out x1) &&
                double.TryParse(m.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out y1))
            {
                wMm = Math.Round(Math.Abs(x1 - x0) * 25.4 / 72.0, 1);
                hMm = Math.Round(Math.Abs(y1 - y0) * 25.4 / 72.0, 1);
                return wMm > 0 && hMm > 0;
            }
            return false;
        }

        private static bool IsFullPageA4(double wMm, double hMm, string stem, bool inPanel)
        {
            if (inPanel && Regex.IsMatch(stem, @"_[A-Z]$|panel_[A-Z]")) return false;
            if (wMm > 0 && hMm > 0)
            {
                double s = Math.Min(wMm, hMm);
                double l = Math.Max(wMm, hMm);
                return s >= 165.0 && l >= 225.0;
            }
            if (inPanel) return false;
            return Regex.IsMatch(stem, @"(?:_a4\b|a4拼图|整页|汇总|all_figs)", RegexOptions.IgnoreCase);
        }

        private static void ParseFigureTag(string fn, string rel, out string figGroup, out string figPanel, out int figOrder)
        {
            string stem = Path.GetFileNameWithoutExtension(fn);
            string comb = rel + " " + stem;
            figPanel = "";

            if (Regex.IsMatch(comb, @"(?:graphical[_\s-]*abstract|图形摘要|fig[_\s-]*0\b)", RegexOptions.IgnoreCase))
            {
                figGroup = "GA 图形摘要"; figOrder = 0; return;
            }
            Match mts = Regex.Match(comb, @"(?:table|tab|表)[_\s-]*s(\d+)", RegexOptions.IgnoreCase);
            if (mts.Success) { int n = int.Parse(mts.Groups[1].Value); figGroup = "Table S" + n; figOrder = 200 + n; return; }

            Match mtm = Regex.Match(comb, @"(?:table|tab|表)[_\s-]*(\d+)", RegexOptions.IgnoreCase);
            if (mtm.Success) { int n = int.Parse(mtm.Groups[1].Value); figGroup = "Table " + n; figOrder = 180 + n; return; }

            Match mfs = Regex.Match(comb, @"(?:fig(?:ure)?|图)[_\s-]*s(\d+)", RegexOptions.IgnoreCase);
            if (mfs.Success)
            {
                int n = int.Parse(mfs.Groups[1].Value);
                Match mp = Regex.Match(stem, @"_([A-Z])$");
                if (mp.Success) figPanel = mp.Groups[1].Value;
                figGroup = "Fig S" + n; figOrder = 100 + n; return;
            }

            Match mfm = Regex.Match(comb, @"(?:fig(?:ure)?|图)[_\s-]*(\d+)", RegexOptions.IgnoreCase);
            if (mfm.Success)
            {
                int n = int.Parse(mfm.Groups[1].Value);
                Match mp = Regex.Match(stem, @"(?:fig\d+([A-Z])|_([A-Z])$)", RegexOptions.IgnoreCase);
                if (mp.Success) figPanel = (mp.Groups[1].Success ? mp.Groups[1].Value : mp.Groups[2].Value).ToUpperInvariant();
                figGroup = "Fig " + n; figOrder = n; return;
            }

            figGroup = "专题与汇总图"; figOrder = 90;
        }

        private static string InferPlatformLabel(string pdir)
        {
            string low = pdir.ToLowerInvariant();
            if (low.Contains("claude")) return "Claude";
            if (low.Contains("gemini") || low.Contains("agy")) return "AGY";
            if (low.Contains("grok")) return "Grok";
            if (low.Contains("codex") || low.Contains("gpt")) return "Codex";
            if (low.Contains("cursor")) return "Cursor";
            if (low.Contains("deepseek")) return "DeepSeek";
            if (low.Contains("zcode")) return "ZCode";
            if (low.Contains("alma")) return "Alma";
            return Regex.Replace(pdir, @"(?:输出|[-_]?output)$", "", RegexOptions.IgnoreCase);
        }

        private static double ToUnixTime(DateTime utc)
        {
            return (utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        private static DateTime FromUnixTimeLocal(double sec)
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(sec).ToLocalTime();
        }
    }

    public class StudioWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private string _rootDir;
        private string _hubAppDir;
        private readonly string _appLocalDataDir;
        private string _cachePath;
        private string _metaPath;
        private readonly string _previewOutDir;
        private string _pythonExe;
        private readonly WebView2 _webView;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private static readonly Encoding s_utf8NoBom = new UTF8Encoding(false);

        private WinForms.NotifyIcon _trayIcon;
        private bool _allowExit;
        private bool _shownTrayBalloonOnce;
        private volatile bool _disposed;

        public int ExitCode { get; private set; }

        public StudioWindow(string rootDir, string appLocalDataDir, string previewOutDir, EventWaitHandle wakeEvent)
        {
            _appLocalDataDir = appLocalDataDir;
            _previewOutDir = previewOutDir;
            BindRootDirectory(rootDir, false);

            Title = "模型输出管家";
            Width = 1520;
            Height = 900;
            MinWidth = 1080;
            MinHeight = 680;
            Background = new SolidColorBrush(Color.FromRgb(0xF1, 0xF3, 0xEE));

            if (!string.IsNullOrEmpty(_previewOutDir))
            {
                Width = 1560;
                Height = 920;
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = -12000;
                Top = -12000;
                ShowInTaskbar = false;
                ShowActivated = false;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                InitSystemTrayIcon();
                if (wakeEvent != null)
                {
                    StartSingleInstanceWakeListener(wakeEvent);
                }
            }

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;

            var chrome = new WindowChrome
            {
                CaptionHeight = 0,
                CornerRadius = new CornerRadius(10),
                GlassFrameThickness = new Thickness(1),
                ResizeBorderThickness = new Thickness(6),
                UseAeroCaptionButtons = false
            };
            WindowChrome.SetWindowChrome(this, chrome);

            _webView = new WebView2
            {
                DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 241, 243, 238)
            };
            Content = _webView;

            SourceInitialized += OnSourceInitialized;
            Loaded += OnWindowLoaded;
        }

        private void BindRootDirectory(string newRootDir, bool saveAsDefault)
        {
            _rootDir = newRootDir;
            string candidateHub = Path.Combine(_rootDir, "_hub_app");
            if (Directory.Exists(candidateHub))
            {
                _hubAppDir = candidateHub;
                _cachePath = Path.Combine(_hubAppDir, "scan_cache.json");
                _metaPath = Path.Combine(_hubAppDir, "user_meta.json");
            }
            else
            {
                // Standalone single-EXE mode: keep user's target directory 100% clean, store cache in %LOCALAPPDATA%\ModelOutputHub!
                _hubAppDir = _appLocalDataDir;
                string safeHash = Math.Abs((_rootDir ?? "").ToLowerInvariant().GetHashCode()).ToString();
                _cachePath = Path.Combine(_appLocalDataDir, "scan_cache_" + safeHash + ".json");
                _metaPath = Path.Combine(_appLocalDataDir, "user_meta.json");
            }
            _pythonExe = ResolvePythonExecutable(_hubAppDir);

            if (saveAsDefault)
            {
                try
                {
                    string settingsFile = Path.Combine(_appLocalDataDir, "app_settings.json");
                    File.WriteAllText(settingsFile, _json.Serialize(new Dictionary<string, object> { { "watched_root", _rootDir } }), s_utf8NoBom);
                }
                catch { }
            }
        }

        public static string ResolvePythonExecutable(string hubAppDir)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new List<string>
            {
                Path.Combine(home, @"miniconda3\python.exe"),
                Path.Combine(home, @"anaconda3\python.exe"),
                @"C:\ProgramData\miniconda3\python.exe",
                @"C:\ProgramData\anaconda3\python.exe"
            };

            string pyRoot = Path.Combine(localApp, @"Programs\Python");
            if (Directory.Exists(pyRoot))
            {
                foreach (string d in Directory.GetDirectories(pyRoot, "Python3*").OrderByDescending(x => x))
                {
                    candidates.Add(Path.Combine(d, "python.exe"));
                }
            }

            foreach (string c in candidates)
            {
                if (File.Exists(c)) return c;
            }
            return null;
        }

        private void InitSystemTrayIcon()
        {
            try
            {
                _trayIcon = new WinForms.NotifyIcon
                {
                    Text = "模型输出管家 — 点击显示主界面"
                };

                try
                {
                    string exePath = Assembly.GetExecutingAssembly().Location;
                    _trayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath) ?? System.Drawing.SystemIcons.Application;
                }
                catch
                {
                    _trayIcon.Icon = System.Drawing.SystemIcons.Application;
                }

                var menu = new WinForms.ContextMenuStrip
                {
                    Font = new System.Drawing.Font("Microsoft YaHei UI", 9.5f)
                };

                var itemOpen = new WinForms.ToolStripMenuItem("显示主窗口")
                {
                    Font = new System.Drawing.Font("Microsoft YaHei UI", 9.5f, System.Drawing.FontStyle.Bold)
                };
                itemOpen.Click += (s, e) => Dispatcher.BeginInvoke(new Action(RestoreFromTray));

                var itemSearch = new WinForms.ToolStripMenuItem("全局秒搜课题 (Ctrl+K)");
                itemSearch.Click += (s, e) => Dispatcher.BeginInvoke(new Action(async () =>
                {
                    RestoreFromTray();
                    try
                    {
                        if (_webView.CoreWebView2 != null)
                            await _webView.CoreWebView2.ExecuteScriptAsync("if(window.openCmdPalette)openCmdPalette();");
                    }
                    catch { }
                }));

                var itemPick = new WinForms.ToolStripMenuItem("切换纳管目录...");
                itemPick.Click += (s, e) => Dispatcher.BeginInvoke(new Action(PickAndSwitchRootDir));

                var itemRoot = new WinForms.ToolStripMenuItem("打开当前输出目录");
                itemRoot.Click += (s, e) =>
                {
                    if (Directory.Exists(_rootDir))
                        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _rootDir + "\"") { UseShellExecute = true });
                };

                var itemRefresh = new WinForms.ToolStripMenuItem("立即重新扫描");
                itemRefresh.Click += (s, e) => RunScanBackendAsync(false);

                var itemExit = new WinForms.ToolStripMenuItem("彻底退出模型输出管家");
                itemExit.Click += (s, e) => Dispatcher.BeginInvoke(new Action(ExitApplicationCompletely));

                menu.Items.Add(itemOpen);
                menu.Items.Add(itemSearch);
                menu.Items.Add(itemPick);
                menu.Items.Add(itemRoot);
                menu.Items.Add(itemRefresh);
                menu.Items.Add(new WinForms.ToolStripSeparator());
                menu.Items.Add(itemExit);

                _trayIcon.ContextMenuStrip = menu;
                _trayIcon.MouseClick += (s, e) =>
                {
                    if (e.Button == WinForms.MouseButtons.Left)
                        Dispatcher.BeginInvoke(new Action(RestoreFromTray));
                };
                _trayIcon.DoubleClick += (s, e) => Dispatcher.BeginInvoke(new Action(RestoreFromTray));
                _trayIcon.Visible = true;
            }
            catch { }
        }

        private void StartSingleInstanceWakeListener(EventWaitHandle wakeEvent)
        {
            var th = new Thread(() =>
            {
                while (!_disposed)
                {
                    try
                    {
                        if (wakeEvent.WaitOne(1000))
                        {
                            if (_disposed) break;
                            Dispatcher.BeginInvoke(new Action(RestoreFromTray));
                        }
                    }
                    catch { break; }
                }
            })
            {
                IsBackground = true,
                Name = "ModelOutputHub_WakeListener"
            };
            th.Start();
        }

        public void HideToTray()
        {
            Hide();
            if (_trayIcon != null && !_shownTrayBalloonOnce)
            {
                _shownTrayBalloonOnce = true;
                try
                {
                    _trayIcon.ShowBalloonTip(
                        2200,
                        "模型输出管家已最小化到托盘",
                        "单击右下角托盘图标即可随时唤起；右键托盘图标可彻底退出。",
                        WinForms.ToolTipIcon.None);
                }
                catch { }
            }
        }

        public void RestoreFromTray()
        {
            try
            {
                if (!IsVisible) Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
            }
            catch { }
        }

        public void ExitApplicationCompletely()
        {
            _allowExit = true;
            _disposed = true;
            if (_trayIcon != null)
            {
                try { _trayIcon.Visible = false; _trayIcon.Dispose(); _trayIcon = null; } catch { }
            }
            Close();
            if (Application.Current != null) Application.Current.Shutdown(ExitCode);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_allowExit && string.IsNullOrEmpty(_previewOutDir))
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            _disposed = true;
            if (_trayIcon != null)
            {
                try { _trayIcon.Visible = false; _trayIcon.Dispose(); } catch { }
            }
            base.OnClosing(e);
            if (Application.Current != null && !string.IsNullOrEmpty(_previewOutDir))
            {
                Application.Current.Shutdown(ExitCode);
            }
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int cornerPref = 2;
                DwmSetWindowAttribute(hwnd, 33, ref cornerPref, sizeof(int));
            }
            catch { }
        }

        private async void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Always store WebView2 user profile in LocalApplicationData so read-only / Downloads / Program Files folders never fail!
                string profileDir = Path.Combine(_appLocalDataDir, string.IsNullOrEmpty(_previewOutDir) ? "wv2_profile" : "wv2_preview_profile");
                Directory.CreateDirectory(profileDir);

                // Ensure initial scan exists immediately via pure C# NativeScanner if cache doesn't exist yet!
                if (!File.Exists(_cachePath))
                {
                    PerformScanSync(false);
                }

                var env = await CoreWebView2Environment.CreateAsync(null, profileDir);
                await _webView.EnsureCoreWebView2Async(env);

                var settings = _webView.CoreWebView2.Settings;
                settings.AreDefaultContextMenusEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.IsZoomControlEnabled = false;
                settings.AreBrowserAcceleratorKeysEnabled = false;

                _webView.CoreWebView2.AddWebResourceRequestedFilter("https://hub.local/*", CoreWebView2WebResourceContext.All);
                _webView.CoreWebView2.WebResourceRequested += OnWebResourceRequested;
                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

                if (!string.IsNullOrEmpty(_previewOutDir))
                {
                    await RunPreviewCaptureSequenceAsync(_previewOutDir);
                    _allowExit = true;
                    Close();
                    return;
                }

                _webView.CoreWebView2.Navigate("https://hub.local/app");
            }
            catch (Exception ex)
            {
                ExitCode = 1;
                _allowExit = true;
                if (!string.IsNullOrEmpty(_previewOutDir))
                {
                    File.WriteAllText(Path.Combine(_previewOutDir, "preview_error.txt"), ex.ToString(), s_utf8NoBom);
                    Close();
                }
                else
                {
                    MessageBox.Show("初始化桌面渲染引擎失败：\n" + ex.Message, "模型输出管家", MessageBoxButton.OK, MessageBoxImage.Error);
                    ExitApplicationCompletely();
                }
            }
        }

        private void PickAndSwitchRootDir()
        {
            using (var dlg = new WinForms.FolderBrowserDialog())
            {
                dlg.Description = "请选择你存放大模型（Claude / Gemini / Cursor / Codex 等）输出产出的总目录：";
                if (Directory.Exists(_rootDir)) dlg.SelectedPath = _rootDir;
                if (dlg.ShowDialog() == WinForms.DialogResult.OK && Directory.Exists(dlg.SelectedPath))
                {
                    BindRootDirectory(dlg.SelectedPath, true);
                    RunScanBackendAsync(false, "已切换管理目录并完成扫描");
                }
            }
        }

        private Dictionary<string, object> LoadUserMetaDict()
        {
            if (File.Exists(_metaPath))
            {
                try
                {
                    return _json.Deserialize<Dictionary<string, object>>(File.ReadAllText(_metaPath, Encoding.UTF8))
                           ?? new Dictionary<string, object>();
                }
                catch { }
            }
            return new Dictionary<string, object>();
        }

        private void PerformScanSync(bool runTidyFirst)
        {
            try
            {
                string pyScript = Path.Combine(_hubAppDir, runTidyFirst ? "tidy_outputs.py" : "topic_rules.py");
                if (!string.IsNullOrEmpty(_pythonExe) && File.Exists(_pythonExe) && File.Exists(pyScript))
                {
                    RunProcessSilent(_pythonExe, "\"" + pyScript + "\"", _rootDir);
                    if (File.Exists(_cachePath)) return;
                }

                // Pure C# built-in fallback (zero Python dependency!)
                string json = NativeScanner.ScanDirectoryToJson(_rootDir, LoadUserMetaDict());
                Directory.CreateDirectory(Path.GetDirectoryName(_cachePath));
                File.WriteAllText(_cachePath, json, s_utf8NoBom);
            }
            catch { }
        }

        private void RunScanBackendAsync(bool runTidyFirst, string customToast = null)
        {
            Task.Run(() =>
            {
                PerformScanSync(runTidyFirst);
                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        string toast = customToast ?? (runTidyFirst ? "已完成无损归整并更新课题快捷方式" : "目录扫描已更新");
                        string js = "fetch('https://hub.local/api/state').then(r=>r.json()).then(d=>{ d.toast='" + toast + "'; window.__onHostDataUpdated(d); });";
                        if (_webView.CoreWebView2 != null)
                            await _webView.CoreWebView2.ExecuteScriptAsync(js);
                    }
                    catch { }
                }));
            });
        }

        private void OnWebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                Uri uri = new Uri(e.Request.Uri);
                if (uri.AbsolutePath.Equals("/app", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath == "/")
                {
                    Stream resStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ModelOutputHub.StudioUI");
                    if (resStream != null)
                    {
                        var ms = new MemoryStream();
                        resStream.CopyTo(ms);
                        resStream.Dispose();
                        ms.Position = 0;
                        e.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                            ms, 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
                        return;
                    }
                }
                else if (uri.AbsolutePath.Equals("/api/state", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] cacheBytes = File.Exists(_cachePath)
                        ? File.ReadAllBytes(_cachePath)
                        : s_utf8NoBom.GetBytes(NativeScanner.ScanDirectoryToJson(_rootDir, LoadUserMetaDict()));
                    byte[] metaBytes = File.Exists(_metaPath)
                        ? File.ReadAllBytes(_metaPath)
                        : s_utf8NoBom.GetBytes("{}");

                    var ms = new MemoryStream(cacheBytes.Length + metaBytes.Length + 32);
                    byte[] p1 = s_utf8NoBom.GetBytes("{\"hub\":");
                    byte[] p2 = s_utf8NoBom.GetBytes(",\"meta\":");
                    byte[] p3 = s_utf8NoBom.GetBytes("}");
                    ms.Write(p1, 0, p1.Length);
                    ms.Write(cacheBytes, 0, cacheBytes.Length);
                    ms.Write(p2, 0, p2.Length);
                    ms.Write(metaBytes, 0, metaBytes.Length);
                    ms.Write(p3, 0, p3.Length);
                    ms.Position = 0;

                    e.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                        ms, 200, "OK", "Content-Type: application/json; charset=utf-8\r\nCache-Control: no-store");
                    return;
                }
                else if (uri.AbsolutePath.StartsWith("/fonts/", StringComparison.OrdinalIgnoreCase))
                {
                    string userFontDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Fonts");
                    string sysFontDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                    string targetFont = null;
                    if (uri.AbsolutePath.EndsWith("wenkai-bold.ttf", StringComparison.OrdinalIgnoreCase))
                        targetFont = Path.Combine(userFontDir, "LXGWWenKaiMonoBold-Base.ttf");
                    else if (uri.AbsolutePath.EndsWith("wenkai-xbold.ttf", StringComparison.OrdinalIgnoreCase))
                        targetFont = Path.Combine(userFontDir, "LXGWWenKaiMonoXBold.ttf");
                    else if (uri.AbsolutePath.EndsWith("wenkai-reg.ttf", StringComparison.OrdinalIgnoreCase))
                    {
                        targetFont = Path.Combine(sysFontDir, "LXGWWenKai-Regular.ttf");
                        if (!File.Exists(targetFont)) targetFont = Path.Combine(userFontDir, "LXGWWenKai-Regular.ttf");
                    }

                    if (!string.IsNullOrEmpty(targetFont) && File.Exists(targetFont))
                    {
                        var fms = new MemoryStream(File.ReadAllBytes(targetFont));
                        e.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                            fms, 200, "OK", "Content-Type: font/ttf\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: max-age=86400");
                        return;
                    }
                }
            }
            catch { }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string raw = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(raw)) return;
                var msg = _json.Deserialize<Dictionary<string, object>>(raw);
                if (msg == null || !msg.ContainsKey("action")) return;

                string action = Convert.ToString(msg["action"]);
                switch (action)
                {
                    case "dragWindow":
                        IntPtr hwnd = new WindowInteropHelper(this).Handle;
                        ReleaseCapture();
                        SendMessage(hwnd, 0x00A1, 2, 0);
                        break;

                    case "minWindow":
                        WindowState = WindowState.Minimized;
                        break;

                    case "maxWindow":
                        WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
                        break;

                    case "closeWindow":
                        HideToTray();
                        break;

                    case "exitApp":
                        ExitApplicationCompletely();
                        break;

                    case "pickRootDir":
                        PickAndSwitchRootDir();
                        break;

                    case "openFile":
                        if (msg.ContainsKey("path"))
                        {
                            string fPath = Convert.ToString(msg["path"]);
                            if (File.Exists(fPath) || Directory.Exists(fPath))
                            {
                                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                                string openScript = Path.Combine(home, @"agent-hub\scripts\open_deliverable.py");
                                string ext = (Path.GetExtension(fPath) ?? "").ToLowerInvariant();
                                if (!string.IsNullOrEmpty(_pythonExe) && File.Exists(openScript) && File.Exists(_pythonExe) && (ext == ".html" || ext == ".htm" || ext == ".pdf" || ext == ".pptx"))
                                {
                                    Task.Run(() => RunProcessSilent(_pythonExe, "\"" + openScript + "\" \"" + fPath + "\"", _rootDir));
                                }
                                else
                                {
                                    Process.Start(new ProcessStartInfo(fPath) { UseShellExecute = true });
                                }
                            }
                        }
                        break;

                    case "openFolder":
                        if (msg.ContainsKey("path"))
                        {
                            string dPath = Convert.ToString(msg["path"]);
                            if (Directory.Exists(dPath))
                                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dPath + "\"") { UseShellExecute = true });
                        }
                        break;

                    case "revealPath":
                        if (msg.ContainsKey("path"))
                        {
                            string rPath = Convert.ToString(msg["path"]);
                            if (File.Exists(rPath))
                                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + rPath + "\"") { UseShellExecute = true });
                            else if (Directory.Exists(rPath))
                                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + rPath + "\"") { UseShellExecute = true });
                        }
                        break;

                    case "copyText":
                        if (msg.ContainsKey("text"))
                            Clipboard.SetText(Convert.ToString(msg["text"]));
                        break;

                    case "saveMeta":
                        MergeAndSaveUserMeta(msg, new[] { "starred", "notes", "topic_overrides" });
                        break;

                    case "savePrefs":
                        MergeAndSaveUserMeta(msg, new[] { "ui_paradigm", "ui_scale", "ui_font" });
                        break;

                    case "runRefresh":
                        RunScanBackendAsync(false);
                        break;

                    case "runTidy":
                        RunScanBackendAsync(true);
                        break;
                }
            }
            catch { }
        }

        private void MergeAndSaveUserMeta(Dictionary<string, object> incoming, string[] keys)
        {
            try
            {
                Dictionary<string, object> cur = LoadUserMetaDict();
                foreach (string k in keys)
                {
                    if (incoming.ContainsKey(k)) cur[k] = incoming[k];
                }
                Directory.CreateDirectory(Path.GetDirectoryName(_metaPath));
                File.WriteAllText(_metaPath, _json.Serialize(cur), s_utf8NoBom);
            }
            catch { }
        }

        private static void RunProcessSilent(string exe, string arguments, string rootDir)
        {
            var psi = new ProcessStartInfo(exe, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (!string.IsNullOrEmpty(rootDir))
            {
                psi.EnvironmentVariables["MODEL_OUTPUT_ROOT"] = rootDir;
            }
            using (var p = Process.Start(psi))
            {
                if (p != null) p.WaitForExit(45000);
            }
        }

        private async Task RunPreviewCaptureSequenceAsync(string outDir)
        {
            Directory.CreateDirectory(outDir);
            var shots = new[]
            {
                new { Name = "standalone-verify", Url = "https://hub.local/app?paradigm=takram&view=overview", ExtraJs = "" }
            };

            foreach (var s in shots)
            {
                var tcs = new TaskCompletionSource<bool>();
                EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = null;
                handler = (snd, args) =>
                {
                    _webView.CoreWebView2.NavigationCompleted -= handler;
                    tcs.TrySetResult(true);
                };
                _webView.CoreWebView2.NavigationCompleted += handler;
                _webView.CoreWebView2.Navigate(s.Url);
                await tcs.Task;

                for (int w = 0; w < 30; w++)
                {
                    string rdy = await _webView.CoreWebView2.ExecuteScriptAsync("Boolean(window.__DESIGN_READY)");
                    if (rdy == "true") break;
                    await Task.Delay(100);
                }
                await Task.Delay(350);

                string pngPath = Path.Combine(outDir, s.Name + ".png");
                using (var fs = new FileStream(pngPath, FileMode.Create, FileAccess.Write))
                {
                    await _webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
                }
            }
            ExitCode = 0;
        }
    }
}
