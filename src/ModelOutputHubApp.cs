// -*- coding: utf-8 -*-
// ModelOutputHubApp.cs — Model Output Hub (v2.0 Instant File Finder + Live QuickLook + Visual Thumbnails)
// 1. True Single-File Zero-Dependency .exe (100% immune to Windows MOTW Zone.Identifier & ZIP-preview launch)
// 2. Everything-grade Live Disk Search (/api/live-search) + Real Folder Browser (/api/folder-list)
// 3. Instant Built-in QuickLook Preview (/rawfile, /api/preview-info) + Fast Cached Thumbnails (/api/thumb via fitz pipe or Win32 Shell)
// 4. Built-in Pure C# NativeScanner (9 deliverable categories) + Auto-Refresh on Window Activate + System Tray Resident

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
                try { SetCurrentProcessExplicitAppUserModelID("ModelOutputHub.Studio.Desktop.v2_0"); } catch { }

                s_appLocalDataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ModelOutputHub");
                string nativeDir = Path.Combine(s_appLocalDataDir, @"runtime\win-x64");
                Directory.CreateDirectory(nativeDir);

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
                        return;
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
                                Assembly asm = Assembly.Load(ms.ToArray());
                                s_loadedAssemblies[shortName] = asm;
                                return asm;
                            }
                        }
                    }
                }

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

    internal static class ShellThumbnailHelper
    {
        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage([In, MarshalAs(UnmanagedType.Struct)] SIZE size, [In] int flags, [Out] out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
            public SIZE(int x, int y) { cx = x; cy = y; }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            [In, MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [Out, MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        public static bool TrySaveShellThumbnailPng(string filePath, string outPngPath, int maxPx = 420)
        {
            IntPtr hBitmap = IntPtr.Zero;
            try
            {
                Guid iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
                IShellItemImageFactory factory;
                int hr = SHCreateItemFromParsingName(filePath, IntPtr.Zero, iid, out factory);
                if (hr != 0 || factory == null) return false;

                // SIIGBF_BIGGERSIZEOK (0x01) | SIIGBF_THUMBNAILONLY (0x08)
                hr = factory.GetImage(new SIZE(maxPx, maxPx), 0x01 | 0x08, out hBitmap);
                if (hr != 0 || hBitmap == IntPtr.Zero)
                {
                    // Fallback allowing icon/preview render (0x00)
                    hr = factory.GetImage(new SIZE(maxPx, maxPx), 0x00, out hBitmap);
                }
                if (hr != 0 || hBitmap == IntPtr.Zero) return false;

                using (Bitmap bmp = Image.FromHbitmap(hBitmap))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outPngPath));
                    string tmp = outPngPath + ".tmp";
                    bmp.Save(tmp, ImageFormat.Png);
                    if (File.Exists(outPngPath)) File.Delete(outPngPath);
                    File.Move(tmp, outPngPath);
                    return true;
                }
            }
            catch
            {
                return false;
            }
            finally
            {
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            }
        }

        public static bool TrySaveGdiImageThumbnailPng(string imgPath, string outPngPath, int maxWidth = 420)
        {
            try
            {
                using (var fs = new FileStream(imgPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (Image src = Image.FromStream(fs, false, false))
                {
                    int w = src.Width;
                    int h = src.Height;
                    if (w <= 0 || h <= 0) return false;
                    int nw = w > maxWidth ? maxWidth : w;
                    int nh = Math.Max(1, (int)Math.Round((double)h * nw / w));
                    using (var dst = new Bitmap(nw, nh))
                    using (Graphics g = Graphics.FromImage(dst))
                    {
                        g.Clear(System.Drawing.Color.White);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.DrawImage(src, 0, 0, nw, nh);
                        Directory.CreateDirectory(Path.GetDirectoryName(outPngPath));
                        string tmp = outPngPath + ".tmp";
                        dst.Save(tmp, ImageFormat.Png);
                        if (File.Exists(outPngPath)) File.Delete(outPngPath);
                        File.Move(tmp, outPngPath);
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }
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
                if (args[i] == "--capture-previews")
                    previewDir = args[i + 1];
            }

            Mutex singleMutex = null;
            EventWaitHandle wakeEvent = null;

            if (string.IsNullOrEmpty(previewDir))
            {
                bool createdNew;
                singleMutex = new Mutex(true, MutexName, out createdNew);
                wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName);

                if (!createdNew)
                {
                    try { wakeEvent.Set(); } catch { }
                    return 0;
                }
            }

            try
            {
                string ver = CoreWebView2Environment.GetAvailableBrowserVersionString();
                if (string.IsNullOrEmpty(ver))
                    throw new InvalidOperationException("未检测到 Microsoft Edge WebView2 Runtime。");
            }
            catch (Exception ex)
            {
                WinForms.MessageBox.Show(
                    "您的电脑缺少 Microsoft Edge WebView2 运行时组件，无法渲染界面：\n" + ex.Message +
                    "\n\n请安装系统自带 Edge 更新或从微软官网下载 Evergreen WebView2 Runtime。",
                    "模型输出管家 — 环境提示",
                    WinForms.MessageBoxButtons.OK,
                    WinForms.MessageBoxIcon.Warning);
                return 2;
            }

            var app = new Application();
            var win = new StudioWindow(rootDir, appLocalDataDir, previewDir, wakeEvent);
            app.Run(win);

            if (singleMutex != null)
            {
                try { singleMutex.ReleaseMutex(); singleMutex.Dispose(); } catch { }
            }
            return win.ExitCode;
        }

        private static string ResolveInitialRootDir(string[] args, string exeDir, string appLocalDataDir)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--root" && Directory.Exists(args[i + 1]))
                    return Path.GetFullPath(args[i + 1]);
            }

            if (LooksLikeOutputHubRoot(exeDir))
                return exeDir;

            string settingsFile = Path.Combine(appLocalDataDir, "app_settings.json");
            if (File.Exists(settingsFile))
            {
                try
                {
                    var js = new JavaScriptSerializer();
                    var cfg = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(settingsFile, Encoding.UTF8));
                    if (cfg != null && cfg.ContainsKey("watched_root"))
                    {
                        string saved = Convert.ToString(cfg["watched_root"]);
                        if (!string.IsNullOrEmpty(saved) && Directory.Exists(saved))
                            return saved;
                    }
                }
                catch { }
            }

            return exeDir;
        }

        private static bool LooksLikeOutputHubRoot(string dir)
        {
            if (Directory.Exists(Path.Combine(dir, "_hub_app"))) return true;
            try
            {
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    string name = Path.GetFileName(sub);
                    if (name.EndsWith("输出", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith("_output", StringComparison.OrdinalIgnoreCase))
                        return true;
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

    internal static class NativeScanner
    {
        public static readonly Regex NoiseDirRx = new Regex(
            @"^(?:_backups?|backups?|_snapshots?|snapshots?|\.archive.*|_archive.*|_draft_fail|__pycache__|\.git|node_modules|render.*|inspect.*|panel_pngs.*|offslide_imgs|lint_pages|checker_pngs|_slides_.*|_workroom|_probe.*|qa.*|logs|.*备份.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex s_panelDirRx = new Regex(
            @"^(?:panels?|pdf_panels?|single_panels?|单面板.*|.*单图.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex s_sliceRx = new Regex(
            @"(?:_notag|_noletter|_raw_tmp|_NA)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex s_noiseImgRx = new Regex(
            @"(?:^page_?\d+|^slide_?\d+|^p\d+_s\d+|_thumb\b|^diff_|^mask_|^render_|_raw_tmp)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly HashSet<string> s_plumbingDocs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "state.md", "ledger.md", "donotretry.md", "manifest.txt", "prompt.txt",
            "cmakelists.txt", "requirements.txt", "license.txt", "package-lock.json"
        };

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
                    if (o != null) starredSet.Add(Convert.ToString(o));
            }
            var notesMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (userMeta != null && userMeta.ContainsKey("notes") && userMeta["notes"] is Dictionary<string, object>)
            {
                foreach (var kv in (Dictionary<string, object>)userMeta["notes"])
                    notesMap[kv.Key] = Convert.ToString(kv.Value ?? "");
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
                        platDirs.Add(d);
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
                        var rootLoose = ScanRootLooseProject(rootDir, starredSet, notesMap, nowUnix);
                        if (rootLoose != null) projects.Add(rootLoose);

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

                Func<string, int> sumKey = k => tProjs.Sum(x => {
                    var c = (Dictionary<string, int>)x["counts"];
                    return c.ContainsKey(k) ? c[k] : 0;
                });

                int a4Cnt = sumKey("a4_pdf");
                int sgCnt = sumKey("single_pdf");

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
                    { "html_count", sumKey("html") },
                    { "pptx_count", sumKey("pptx") },
                    { "a4_pdf_count", a4Cnt },
                    { "single_pdf_count", sgCnt },
                    { "pdf_count", a4Cnt + sgCnt },
                    { "image_count", sumKey("image") },
                    { "doc_count", sumKey("doc") },
                    { "table_count", sumKey("table") },
                    { "archive_count", sumKey("archive") },
                    { "script_count", sumKey("script") },
                    { "size_mb", Math.Round(tProjs.Sum(x => Convert.ToDouble(x["size_mb"])), 1) },
                    { "cache_mb", Math.Round(tProjs.Sum(x => Convert.ToDouble(x["cache_mb"])), 1) },
                    { "branches", brList }
                });
            }

            Func<string, int> totalKey = k => projects.Sum(x => {
                var c = (Dictionary<string, int>)x["counts"];
                return c.ContainsKey(k) ? c[k] : 0;
            });

            int sumA4 = totalKey("a4_pdf");
            int sumSg = totalKey("single_pdf");

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
                        { "total_html", totalKey("html") },
                        { "total_pptx", totalKey("pptx") },
                        { "total_a4_pdf", sumA4 },
                        { "total_single_pdf", sumSg },
                        { "total_pdf", sumA4 + sumSg },
                        { "total_image", totalKey("image") },
                        { "total_doc", totalKey("doc") },
                        { "total_table", totalKey("table") },
                        { "total_archive", totalKey("archive") },
                        { "total_script", totalKey("script") },
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
            var images = new List<Dictionary<string, object>>();
            var docs = new List<Dictionary<string, object>>();
            var tables = new List<Dictionary<string, object>>();
            var archives = new List<Dictionary<string, object>>();
            var scripts = new List<Dictionary<string, object>>();

            WalkProjectFiles(
                pdir, pdir, 0, false, false,
                ref totalFiles, ref totalBytes, ref cacheBytes, ref latestMtime, ref latestFile,
                htmls, pptxs, a4Pdfs, sgPdfs, images, docs, tables, archives, scripts);

            Comparison<Dictionary<string, object>> byMtimeDesc = (a, b) => Convert.ToDouble(b["mtime"]).CompareTo(Convert.ToDouble(a["mtime"]));
            htmls.Sort(byMtimeDesc);
            pptxs.Sort(byMtimeDesc);
            a4Pdfs.Sort(byMtimeDesc);
            sgPdfs.Sort(byMtimeDesc);
            images.Sort(byMtimeDesc);
            docs.Sort(byMtimeDesc);
            tables.Sort(byMtimeDesc);
            archives.Sort(byMtimeDesc);
            scripts.Sort(byMtimeDesc);

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
                { "image", images.Count },
                { "doc", docs.Count },
                { "table", tables.Count },
                { "archive", archives.Count },
                { "script", scripts.Count }
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
                { "htmls", htmls.Take(30).ToList() },
                { "pptxs", pptxs.Take(20).ToList() },
                { "a4_pdfs", a4Pdfs.Take(80).ToList() },
                { "single_pdfs", sgPdfs.Take(120).ToList() },
                { "pdfs", a4Pdfs.Concat(sgPdfs).Take(30).ToList() },
                { "images", images.Take(40).ToList() },
                { "docs", docs.Take(40).ToList() },
                { "tables", tables.Take(25).ToList() },
                { "archives", archives.Take(20).ToList() },
                { "scripts", scripts.Take(25).ToList() },
                { "counts", counts },
                { "_schema_ver", 3 }
            };
        }


        private static Dictionary<string, object> ScanRootLooseProject(
            string rootDir, HashSet<string> starredSet, Dictionary<string, string> notesMap, double nowUnix)
        {
            int totalFiles = 0;
            long totalBytes = 0;
            long cacheBytes = 0;
            double latestMtime = 0;
            string latestFile = "";

            var htmls = new List<Dictionary<string, object>>();
            var pptxs = new List<Dictionary<string, object>>();
            var a4Pdfs = new List<Dictionary<string, object>>();
            var sgPdfs = new List<Dictionary<string, object>>();
            var images = new List<Dictionary<string, object>>();
            var docs = new List<Dictionary<string, object>>();
            var tables = new List<Dictionary<string, object>>();
            var archives = new List<Dictionary<string, object>>();
            var scripts = new List<Dictionary<string, object>>();

            WalkProjectFiles(
                rootDir, rootDir, 99, false, false,
                ref totalFiles, ref totalBytes, ref cacheBytes, ref latestMtime, ref latestFile,
                htmls, pptxs, a4Pdfs, sgPdfs, images, docs, tables, archives, scripts, true);

            int delivTotal = htmls.Count + pptxs.Count + a4Pdfs.Count + sgPdfs.Count + images.Count + docs.Count + tables.Count + archives.Count + scripts.Count;
            if (delivTotal == 0) return null;

            DateTime dtLocal = latestMtime > 0 ? FromUnixTimeLocal(latestMtime) : DateTime.Now;
            var counts = new Dictionary<string, int>
            {
                { "html", htmls.Count }, { "pptx", pptxs.Count }, { "a4_pdf", a4Pdfs.Count },
                { "single_pdf", sgPdfs.Count }, { "pdf", a4Pdfs.Count + sgPdfs.Count },
                { "image", images.Count }, { "doc", docs.Count }, { "table", tables.Count },
                { "archive", archives.Count }, { "script", scripts.Count }
            };

            return new Dictionary<string, object>
            {
                { "key", "_root_files_" },
                { "name", "当前目录直存文件" },
                { "parent_group", "" },
                { "path", rootDir },
                { "platform_dir", "本地目录" },
                { "platform", "本地" },
                { "platform_color", "#1E513F" },
                { "topic_id", "08_misc_projects" },
                { "sub_branch", "00_根目录直存文件" },
                { "total_files", totalFiles },
                { "size_mb", Math.Round(totalBytes / 1048576.0, 2) },
                { "cache_mb", 0.0 },
                { "mtime", latestMtime },
                { "date", dtLocal.ToString("yyyy-MM-dd") },
                { "datetime", dtLocal.ToString("yyyy-MM-dd HH:mm") },
                { "latest_file", latestFile },
                { "locked", false },
                { "lock_reasons", new string[0] },
                { "starred", starredSet.Contains("_root_files_") },
                { "note", notesMap.ContainsKey("_root_files_") ? notesMap["_root_files_"] : "" },
                { "htmls", htmls.Take(30).ToList() },
                { "pptxs", pptxs.Take(20).ToList() },
                { "a4_pdfs", a4Pdfs.Take(80).ToList() },
                { "single_pdfs", sgPdfs.Take(120).ToList() },
                { "pdfs", a4Pdfs.Concat(sgPdfs).Take(30).ToList() },
                { "images", images.Take(40).ToList() },
                { "docs", docs.Take(40).ToList() },
                { "tables", tables.Take(25).ToList() },
                { "archives", archives.Take(20).ToList() },
                { "scripts", scripts.Take(25).ToList() },
                { "counts", counts },
                { "_schema_ver", 3 }
            };
        }

        private static void WalkProjectFiles(
            string rootProjDir, string curDir, int depth, bool inNoise, bool inPanel,
            ref int totalFiles, ref long totalBytes, ref long cacheBytes, ref double latestMtime, ref string latestFile,
            List<Dictionary<string, object>> htmls, List<Dictionary<string, object>> pptxs,
            List<Dictionary<string, object>> a4Pdfs, List<Dictionary<string, object>> sgPdfs,
            List<Dictionary<string, object>> images, List<Dictionary<string, object>> docs,
            List<Dictionary<string, object>> tables, List<Dictionary<string, object>> archives,
            List<Dictionary<string, object>> scripts, bool shallowRootOnly = false)
        {
            if (!shallowRootOnly && depth > 5) return;
            int effDepth = shallowRootOnly ? 0 : depth;
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
                    if (inNoise || ext == ".b64" || ext == ".bak" || ext == ".pyc" || ext == ".log")
                        cacheBytes += sz;

                    string relStr = fp.Substring(rootProjDir.Length).TrimStart('\\', '/').Replace('\\', '/');
                    if (mt > latestMtime && ext != ".pyc")
                    {
                        latestMtime = mt;
                        latestFile = relStr;
                    }

                    if (inNoise || effDepth > 5 || sz == 0) continue;
                    if (shallowRootOnly && (fn.Equals("模型输出管家.exe", StringComparison.OrdinalIgnoreCase) || fn.Equals("ModelOutputHub.exe", StringComparison.OrdinalIgnoreCase) || fn.StartsWith("使用说明", StringComparison.OrdinalIgnoreCase))) continue;

                    string stem = Path.GetFileNameWithoutExtension(fn);
                    string dtStr = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
                    var baseObj = new Dictionary<string, object>
                    {
                        { "name", fn },
                        { "path", fp },
                        { "rel", relStr },
                        { "size_mb", Math.Round(sz / 1048576.0, 2) },
                        { "mtime", mt },
                        { "date", dtStr }
                    };

                    if ((ext == ".html" || ext == ".htm") && effDepth <= 4)
                    {
                        baseObj["title"] = ExtractHtmlTitle(fp, stem);
                        htmls.Add(baseObj);
                    }
                    else if ((ext == ".pptx" || ext == ".ppt") && effDepth <= 4)
                    {
                        baseObj["title"] = fn;
                        pptxs.Add(baseObj);
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

                        baseObj["dim_mm"] = dimLabel;
                        baseObj["fig_group"] = figGroup;
                        baseObj["fig_panel"] = figPanel;
                        baseObj["fig_order"] = figOrder;
                        baseObj["kind"] = isA4 ? "a4" : "single";
                        if (isA4) a4Pdfs.Add(baseObj); else sgPdfs.Add(baseObj);
                    }
                    else if ((ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".svg" || ext == ".webp") && effDepth <= 4 && !inPanel)
                    {
                        if (!s_noiseImgRx.IsMatch(stem))
                        {
                            string figGroup, figPanel;
                            int figOrder;
                            ParseFigureTag(fn, relStr, out figGroup, out figPanel, out figOrder);
                            baseObj["fig_group"] = figGroup;
                            baseObj["fig_panel"] = figPanel;
                            baseObj["fig_order"] = figOrder;
                            images.Add(baseObj);
                        }
                    }
                    else if ((ext == ".docx" || ext == ".doc" || ext == ".md" || ext == ".txt" || ext == ".rtf") && effDepth <= 4)
                    {
                        if (!s_plumbingDocs.Contains(fn) && !fn.EndsWith("_lint.txt", StringComparison.OrdinalIgnoreCase))
                            docs.Add(baseObj);
                    }
                    else if ((ext == ".xlsx" || ext == ".xls" || ext == ".csv" || ext == ".tsv") && effDepth <= 4)
                    {
                        if (!fn.EndsWith("_positions.csv", StringComparison.OrdinalIgnoreCase) &&
                            !fn.EndsWith("_manifest.tsv", StringComparison.OrdinalIgnoreCase))
                            tables.Add(baseObj);
                    }
                    else if ((ext == ".zip" || ext == ".7z" || ext == ".rar" || ext == ".tgz" || fn.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) && effDepth <= 4)
                    {
                        archives.Add(baseObj);
                    }
                    else if ((ext == ".r" || ext == ".py" || ext == ".sh" || ext == ".ipynb") && effDepth <= 4)
                    {
                        scripts.Add(baseObj);
                    }
                }

                if (!shallowRootOnly) foreach (string sub in Directory.GetDirectories(curDir))
                {
                    FileAttributes attr;
                    try { attr = File.GetAttributes(sub); } catch { continue; }
                    if ((attr & FileAttributes.ReparsePoint) != 0) continue;

                    string dn = Path.GetFileName(sub);
                    bool nextNoise = inNoise || NoiseDirRx.IsMatch(dn);
                    bool nextPanel = inPanel || s_panelDirRx.IsMatch(dn);
                    WalkProjectFiles(
                        rootProjDir, sub, depth + 1, nextNoise, nextPanel,
                        ref totalFiles, ref totalBytes, ref cacheBytes, ref latestMtime, ref latestFile,
                        htmls, pptxs, a4Pdfs, sgPdfs, images, docs, tables, archives, scripts);
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
                    n = fs.Read(buf, 0, buf.Length);
                string head = Encoding.UTF8.GetString(buf, 0, n);
                Match m = s_titleRx.Match(head);
                if (m.Success)
                {
                    string t = Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim();
                    if (!string.IsNullOrEmpty(t)) return t.Length > 95 ? t.Substring(0, 95) : t;
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

        public static void ParseFigureTag(string fn, string rel, out string figGroup, out string figPanel, out int figOrder)
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

        public static string InferPlatformLabel(string pdir)
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

        public static double ToUnixTime(DateTime utc)
        {
            return (utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        public static DateTime FromUnixTimeLocal(double sec)
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(sec).ToLocalTime();
        }
    }


    internal static class LlmGateway
    {
        private static readonly Encoding s_utf8 = new UTF8Encoding(false);
        private static readonly JavaScriptSerializer s_js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        static LlmGateway()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; }
            catch { try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { } }
        }

        private static string GetConfigPath(string appLocalDataDir)
        {
            return Path.Combine(appLocalDataDir, "ai_config.json");
        }

        public static Dictionary<string, string> LoadConfigRaw(string appLocalDataDir)
        {
            var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "provider", "deepseek" },
                { "base_url", "https://api.deepseek.com/v1" },
                { "model", "deepseek-chat" },
                { "api_key", "" }
            };
            string fp = GetConfigPath(appLocalDataDir);
            if (!File.Exists(fp)) return res;

            try
            {
                var d = s_js.Deserialize<Dictionary<string, object>>(File.ReadAllText(fp, Encoding.UTF8));
                if (d == null) return res;
                if (d.ContainsKey("provider")) res["provider"] = Convert.ToString(d["provider"] ?? "deepseek");
                if (d.ContainsKey("base_url")) res["base_url"] = Convert.ToString(d["base_url"] ?? "");
                if (d.ContainsKey("model")) res["model"] = Convert.ToString(d["model"] ?? "");
                if (d.ContainsKey("api_key_dpapi"))
                {
                    string encB64 = Convert.ToString(d["api_key_dpapi"] ?? "");
                    if (!string.IsNullOrEmpty(encB64))
                    {
                        byte[] raw = ProtectedData.Unprotect(Convert.FromBase64String(encB64), null, DataProtectionScope.CurrentUser);
                        res["api_key"] = Encoding.UTF8.GetString(raw);
                    }
                }
            }
            catch { }
            return res;
        }

        public static string GetPublicConfigJson(string appLocalDataDir)
        {
            var cfg = LoadConfigRaw(appLocalDataDir);
            string k = cfg["api_key"] ?? "";
            bool isOllama = cfg["provider"].Equals("ollama", StringComparison.OrdinalIgnoreCase) ||
                            cfg["base_url"].IndexOf("127.0.0.1:11434", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            cfg["base_url"].IndexOf("localhost:11434", StringComparison.OrdinalIgnoreCase) >= 0;
            bool configured = (!string.IsNullOrEmpty(k) || isOllama) && !string.IsNullOrEmpty(cfg["base_url"]) && !string.IsNullOrEmpty(cfg["model"]);
            string masked = "";
            if (k.Length > 8)
                masked = k.Substring(0, 4) + "••••••••" + k.Substring(k.Length - 4);
            else if (k.Length > 0)
                masked = "••••••••";

            return s_js.Serialize(new Dictionary<string, object>
            {
                { "configured", configured },
                { "provider", cfg["provider"] },
                { "base_url", cfg["base_url"] },
                { "model", cfg["model"] },
                { "has_key", k.Length > 0 },
                { "masked_key", masked }
            });
        }

        public static void SaveConfig(string appLocalDataDir, string provider, string baseUrl, string model, string newApiKey, bool clearKey)
        {
            var cur = LoadConfigRaw(appLocalDataDir);
            string finalKey = cur["api_key"] ?? "";
            if (clearKey)
                finalKey = "";
            else if (!string.IsNullOrEmpty(newApiKey) && !newApiKey.Contains("••••"))
                finalKey = newApiKey.Trim();

            string encB64 = "";
            if (!string.IsNullOrEmpty(finalKey))
            {
                byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(finalKey), null, DataProtectionScope.CurrentUser);
                encB64 = Convert.ToBase64String(enc);
            }

            var saveObj = new Dictionary<string, object>
            {
                { "provider", (provider ?? "custom").Trim() },
                { "base_url", (baseUrl ?? "").Trim() },
                { "model", (model ?? "").Trim() },
                { "api_key_dpapi", encB64 }
            };
            Directory.CreateDirectory(appLocalDataDir);
            File.WriteAllText(GetConfigPath(appLocalDataDir), s_js.Serialize(saveObj), s_utf8);
        }

        public static string CallLlmChat(string provider, string baseUrl, string apiKey, string model, string systemPrompt, string userPrompt, int maxTokens, out string errorMsg)
        {
            errorMsg = null;
            baseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
            model = (model ?? "").Trim();
            apiKey = (apiKey ?? "").Trim();

            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(model))
            {
                errorMsg = "请先填写完整的 API Base URL 与模型名称（Model）。";
                return null;
            }

            bool isAnthropic = (provider ?? "").Equals("anthropic", StringComparison.OrdinalIgnoreCase) ||
                               baseUrl.IndexOf("api.anthropic.com", StringComparison.OrdinalIgnoreCase) >= 0;

            string endpoint;
            string payloadJson;

            if (isAnthropic)
            {
                if (baseUrl.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
                    endpoint = baseUrl;
                else if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                    endpoint = baseUrl + "/messages";
                else
                    endpoint = baseUrl + "/v1/messages";

                payloadJson = s_js.Serialize(new Dictionary<string, object>
                {
                    { "model", model },
                    { "max_tokens", maxTokens },
                    { "system", systemPrompt ?? "" },
                    { "messages", new object[] { new Dictionary<string, object> { { "role", "user" }, { "content", userPrompt } } } }
                });
            }
            else
            {
                if (baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                    endpoint = baseUrl;
                else
                    endpoint = baseUrl + "/chat/completions";

                var msgs = new List<object>();
                if (!string.IsNullOrEmpty(systemPrompt))
                    msgs.Add(new Dictionary<string, object> { { "role", "system" }, { "content", systemPrompt } });
                msgs.Add(new Dictionary<string, object> { { "role", "user" }, { "content", userPrompt } });

                payloadJson = s_js.Serialize(new Dictionary<string, object>
                {
                    { "model", model },
                    { "temperature", 0.2 },
                    { "max_tokens", maxTokens },
                    { "messages", msgs }
                });
            }

            string rawResp = SendPostWithProxyFallback(endpoint, payloadJson, apiKey, isAnthropic, out errorMsg);
            if (rawResp == null) return null;

            try
            {
                var doc = s_js.Deserialize<Dictionary<string, object>>(rawResp);
                if (doc == null) { errorMsg = "无法解析接口返回 JSON"; return null; }

                if (isAnthropic && doc.ContainsKey("content") && doc["content"] is System.Collections.IEnumerable)
                {
                    var sb = new StringBuilder();
                    foreach (object block in (System.Collections.IEnumerable)doc["content"])
                    {
                        var bd = block as Dictionary<string, object>;
                        if (bd != null && bd.ContainsKey("text")) sb.Append(Convert.ToString(bd["text"]));
                    }
                    return sb.ToString().Trim();
                }

                if (doc.ContainsKey("choices") && doc["choices"] is System.Collections.IEnumerable)
                {
                    foreach (object ch in (System.Collections.IEnumerable)doc["choices"])
                    {
                        var cd = ch as Dictionary<string, object>;
                        if (cd != null && cd.ContainsKey("message") && cd["message"] is Dictionary<string, object>)
                        {
                            var md = (Dictionary<string, object>)cd["message"];
                            if (md.ContainsKey("content"))
                                return Convert.ToString(md["content"] ?? "").Trim();
                        }
                    }
                }
                errorMsg = "接口返回格式不含 message.content：" + (rawResp.Length > 200 ? rawResp.Substring(0, 200) : rawResp);
                return null;
            }
            catch (Exception ex)
            {
                errorMsg = "解析模型响应出错：" + ex.Message;
                return null;
            }
        }

        private static string SendPostWithProxyFallback(string url, string bodyJson, string apiKey, bool isAnthropic, out string errorMsg)
        {
            errorMsg = null;
            byte[] bodyBytes = s_utf8.GetBytes(bodyJson);

            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.Method = "POST";
                    req.ContentType = "application/json; charset=utf-8";
                    req.Accept = "application/json";
                    req.UserAgent = "ModelOutputHub/2.0";
                    req.Timeout = 40000;
                    req.ReadWriteTimeout = 40000;
                    if (attempt == 1) req.Proxy = null;

                    if (isAnthropic)
                    {
                        if (!string.IsNullOrEmpty(apiKey)) req.Headers["x-api-key"] = apiKey;
                        req.Headers["anthropic-version"] = "2023-06-01";
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(apiKey)) req.Headers["Authorization"] = "Bearer " + apiKey;
                    }

                    using (Stream rs = req.GetRequestStream())
                        rs.Write(bodyBytes, 0, bodyBytes.Length);

                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        return sr.ReadToEnd();
                }
                catch (WebException wex)
                {
                    if (wex.Response != null)
                    {
                        try
                        {
                            var hresp = (HttpWebResponse)wex.Response;
                            using (var sr = new StreamReader(hresp.GetResponseStream(), Encoding.UTF8))
                            {
                                string errBody = sr.ReadToEnd();
                                if (errBody.Length > 260) errBody = errBody.Substring(0, 260) + "...";
                                errorMsg = string.Format("HTTP {0}: {1}", (int)hresp.StatusCode, errBody);
                                return null;
                            }
                        }
                        catch { }
                    }
                    if (attempt == 0) continue; // Retry once bypassing dead system proxy!
                    errorMsg = "网络连接失败（离线或地址不可达）：" + wex.Message;
                    return null;
                }
                catch (Exception ex)
                {
                    errorMsg = "请求异常：" + ex.Message;
                    return null;
                }
            }
            return null;
        }

        public static string ExtractCleanFileExcerpt(string fp, int maxChars = 9000)
        {
            if (string.IsNullOrEmpty(fp) || !File.Exists(fp)) return "";
            try
            {
                var fi = new FileInfo(fp);
                string ext = (fi.Extension ?? "").ToLowerInvariant();
                if (ext == ".html" || ext == ".htm")
                {
                    int readBytes = (int)Math.Min(3 * 1024 * 1024, fi.Length);
                    byte[] buf = new byte[readBytes];
                    using (var fs = new FileStream(fp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        fs.Read(buf, 0, readBytes);
                    string html = Encoding.UTF8.GetString(buf);
                    html = Regex.Replace(html, @"<script[\s\S]*?</script>", " ", RegexOptions.IgnoreCase);
                    html = Regex.Replace(html, @"<style[\s\S]*?</style>", " ", RegexOptions.IgnoreCase);
                    html = Regex.Replace(html, @"<svg[\s\S]*?</svg>", " ", RegexOptions.IgnoreCase);
                    html = Regex.Replace(html, @"data:image/[^""'\)\s]+", "", RegexOptions.IgnoreCase);
                    html = Regex.Replace(html, @"<[^>]+>", " ");
                    html = System.Net.WebUtility.HtmlDecode(html);
                    html = Regex.Replace(html, @"\s+", " ").Trim();
                    return html.Length > maxChars ? html.Substring(0, maxChars) + "..." : html;
                }
                if (ext == ".txt" || ext == ".md" || ext == ".r" || ext == ".py" || ext == ".sh" || ext == ".csv" || ext == ".tsv" || ext == ".json")
                {
                    int readBytes = (int)Math.Min(65536, fi.Length);
                    byte[] buf = new byte[readBytes];
                    using (var fs = new FileStream(fp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        fs.Read(buf, 0, readBytes);
                    string s;
                    try { s = new UTF8Encoding(false, true).GetString(buf); }
                    catch { try { s = Encoding.GetEncoding(936).GetString(buf); } catch { s = Encoding.UTF8.GetString(buf); } }
                    return s.Length > maxChars ? s.Substring(0, maxChars) + "..." : s;
                }
                return string.Format("[二进制文件：{0}，大小：{1:F2} MB，最后修改：{2:yyyy-MM-dd HH:mm}]", fi.Name, fi.Length / 1048576.0, fi.LastWriteTime);
            }
            catch { return ""; }
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
        private readonly string _thumbsDir;
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
        private volatile bool _isScanning;
        private DateTime _lastScanUtc = DateTime.MinValue;

        // Warm zero-orphan Python fitz thumbnail worker
        private readonly object _thumbLock = new object();
        private Process _thumbWorkerProc;
        private List<string[]> _synonymGroups = new List<string[]>();

        public int ExitCode { get; private set; }

        public StudioWindow(string rootDir, string appLocalDataDir, string previewOutDir, EventWaitHandle wakeEvent)
        {
            _appLocalDataDir = appLocalDataDir;
            _thumbsDir = Path.Combine(appLocalDataDir, "thumbs");
            Directory.CreateDirectory(_thumbsDir);
            _previewOutDir = previewOutDir;
            BindRootDirectory(rootDir, false);

            Title = "模型输出管家";
            Width = 1560;
            Height = 920;
            MinWidth = 1080;
            MinHeight = 680;
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF1, 0xF3, 0xEE));

            if (!string.IsNullOrEmpty(_previewOutDir))
            {
                Width = 1580;
                Height = 940;
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
                    StartSingleInstanceWakeListener(wakeEvent);
                Activated += OnWindowActivatedAutoRefresh;
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

        private void OnWindowActivatedAutoRefresh(object sender, EventArgs e)
        {
            if (_isScanning || !string.IsNullOrEmpty(_previewOutDir)) return;
            if ((DateTime.UtcNow - _lastScanUtc).TotalSeconds > 25)
            {
                RunScanBackendAsync(false, "");
            }
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
                _hubAppDir = _appLocalDataDir;
                string safeHash = Math.Abs((_rootDir ?? "").ToLowerInvariant().GetHashCode()).ToString();
                _cachePath = Path.Combine(_appLocalDataDir, "scan_cache_" + safeHash + ".json");
                _metaPath = Path.Combine(_appLocalDataDir, "user_meta.json");
            }
            _pythonExe = ResolvePythonExecutable(_hubAppDir);
            BuildSynonymGroups();

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

        private void BuildSynonymGroups()
        {
            var groups = new List<string[]>
            {
                new[] { "图注", "备注", "legend", "legends", "notes" },
                new[] { "稿件", "正文", "论文", "文章", "manuscript", "paper" },
                new[] { "压缩包", "打包", "便携包", ".zip", ".7z", ".rar", ".tgz" },
                new[] { "表格", "数据表", "统计表", ".xlsx", ".xls", ".csv", ".tsv", "table" },
                new[] { "图片", "卡通", "机制图", "流程图", "cartoon", "biorender", "workflow", ".png", ".svg" },
                new[] { "报告", "网页", "审阅", "report", ".html" },
                new[] { "演示", "汇报", "幻灯片", "ppt", ".pptx", "slide" },
                new[] { "组图", "拼图", "整页", "a4", "fig" },
                new[] { "代码", "脚本", ".py", ".r", ".sh", "script" },
                new[] { "文档", "说明", ".docx", ".md", ".txt", "readme" }
            };

            // Dynamically load user's local topic keyword groups if private_profile.json / custom_topics.json exists!
            foreach (string fn in new[] { "private_profile.json", "custom_topics.json" })
            {
                string fp = Path.Combine(_hubAppDir, fn);
                if (!File.Exists(fp)) continue;
                try
                {
                    var cfg = _json.Deserialize<Dictionary<string, object>>(File.ReadAllText(fp, Encoding.UTF8));
                    if (cfg != null && cfg.ContainsKey("rules") && cfg["rules"] is System.Collections.IEnumerable)
                    {
                        foreach (object rObj in (System.Collections.IEnumerable)cfg["rules"])
                        {
                            var rDict = rObj as Dictionary<string, object>;
                            if (rDict != null && rDict.ContainsKey("any") && rDict["any"] is System.Collections.IEnumerable)
                            {
                                var kws = new List<string>();
                                foreach (object kw in (System.Collections.IEnumerable)rDict["any"])
                                {
                                    string s = Convert.ToString(kw ?? "").Trim().ToLowerInvariant();
                                    if (s.Length >= 2) kws.Add(s);
                                }
                                if (kws.Count >= 2) groups.Add(kws.ToArray());
                            }
                        }
                    }
                }
                catch { }
            }
            _synonymGroups = groups;
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
                    candidates.Add(Path.Combine(d, "python.exe"));
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
                    Text = "模型输出管家 — 点击直达找文件工作台"
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

                var itemOpen = new WinForms.ToolStripMenuItem("打开主窗口（秒搜找文件）");
                itemOpen.Font = new System.Drawing.Font("Microsoft YaHei UI", 9.5f, System.Drawing.FontStyle.Bold);
                itemOpen.Click += (s, e) => Dispatcher.BeginInvoke(new Action(RestoreFromTray));

                var itemPick = new WinForms.ToolStripMenuItem("切换输出总目录...");
                itemPick.Click += (s, e) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    RestoreFromTray();
                    PickAndSwitchRootDir();
                }));

                var itemRoot = new WinForms.ToolStripMenuItem("打开当前输出文件夹");
                itemRoot.Click += (s, e) =>
                {
                    if (Directory.Exists(_rootDir))
                        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _rootDir + "\"") { UseShellExecute = true });
                };

                var itemRefresh = new WinForms.ToolStripMenuItem("立即重新扫描");
                itemRefresh.Click += (s, e) => Dispatcher.BeginInvoke(new Action(() => RunScanBackendAsync(false)));

                var itemExit = new WinForms.ToolStripMenuItem("彻底退出模型输出管家");
                itemExit.Click += (s, e) => Dispatcher.BeginInvoke(new Action(ExitApplicationCompletely));

                menu.Items.Add(itemOpen);
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
                        if (wakeEvent.WaitOne(500))
                            Dispatcher.BeginInvoke(new Action(RestoreFromTray));
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
                        "单击右下角托盘图标即可随时唤起秒搜文件；右键可彻底退出。",
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
                if (_webView != null && _webView.CoreWebView2 != null)
                {
                    _webView.CoreWebView2.ExecuteScriptAsync("if(window.__focusMainSearch)window.__focusMainSearch();");
                }
            }
            catch { }
        }

        private void StopThumbWorker()
        {
            lock (_thumbLock)
            {
                if (_thumbWorkerProc != null)
                {
                    try
                    {
                        if (!_thumbWorkerProc.HasExited)
                        {
                            _thumbWorkerProc.StandardInput.Close();
                            if (!_thumbWorkerProc.WaitForExit(300))
                                _thumbWorkerProc.Kill();
                        }
                        _thumbWorkerProc.Dispose();
                    }
                    catch { }
                    _thumbWorkerProc = null;
                }
            }
        }

        public void ExitApplicationCompletely()
        {
            _allowExit = true;
            _disposed = true;
            StopThumbWorker();
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
            StopThumbWorker();
            if (_trayIcon != null)
            {
                try { _trayIcon.Visible = false; _trayIcon.Dispose(); } catch { }
            }
            base.OnClosing(e);
            if (Application.Current != null && !string.IsNullOrEmpty(_previewOutDir))
                Application.Current.Shutdown(ExitCode);
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
                string profileDir = Path.Combine(_appLocalDataDir, string.IsNullOrEmpty(_previewOutDir) ? "wv2_profile" : "wv2_preview_profile");
                Directory.CreateDirectory(profileDir);

                bool needFreshScan = !File.Exists(_cachePath);
                if (!needFreshScan)
                {
                    try
                    {
                        string head = File.ReadAllText(_cachePath, Encoding.UTF8);
                        if (!head.Contains("\"_schema_ver\": 3") && !head.Contains("\"_schema_ver\":3"))
                            needFreshScan = true;
                    }
                    catch { needFreshScan = true; }
                }
                if (needFreshScan)
                    PerformScanSync(false);
                else
                    _lastScanUtc = DateTime.UtcNow;

                var env = await CoreWebView2Environment.CreateAsync(null, profileDir);
                await _webView.EnsureCoreWebView2Async(env);

                var settings = _webView.CoreWebView2.Settings;
                settings.AreDefaultContextMenusEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.IsZoomControlEnabled = true;
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
                // Also kick off a silent incremental background refresh on cold boot so newest files are guaranteed fresh!
                if (!needFreshScan)
                    RunScanBackendAsync(false, "");
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
            _isScanning = true;
            try
            {
                string pyScript = Path.Combine(_hubAppDir, runTidyFirst ? "tidy_outputs.py" : "topic_rules.py");
                if (!string.IsNullOrEmpty(_pythonExe) && File.Exists(_pythonExe) && File.Exists(pyScript))
                {
                    RunProcessSilent(_pythonExe, "\"" + pyScript + "\"", _rootDir);
                    if (File.Exists(_cachePath))
                    {
                        _lastScanUtc = DateTime.UtcNow;
                        return;
                    }
                }

                string json = NativeScanner.ScanDirectoryToJson(_rootDir, LoadUserMetaDict());
                Directory.CreateDirectory(Path.GetDirectoryName(_cachePath));
                File.WriteAllText(_cachePath, json, s_utf8NoBom);
                _lastScanUtc = DateTime.UtcNow;
            }
            catch { }
            finally
            {
                _isScanning = false;
            }
        }

        private void RunScanBackendAsync(bool runTidyFirst, string customToast = null)
        {
            if (_isScanning) return;
            Task.Run(() =>
            {
                PerformScanSync(runTidyFirst);
                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        string toast = customToast == null
                            ? (runTidyFirst ? "已完成无损归整并更新课题快捷方式" : "目录扫描已更新")
                            : customToast;
                        string jsToast = toast.Replace("\\", "\\\\").Replace("'", "\\'");
                        string js = "fetch('https://hub.local/api/state').then(r=>r.json()).then(d=>{ d.toast='" + jsToast + "'; window.__onHostDataUpdated(d); });";
                        if (_webView.CoreWebView2 != null)
                            await _webView.CoreWebView2.ExecuteScriptAsync(js);
                    }
                    catch { }
                }));
            });
        }

        private static Dictionary<string, string> ParseQueryParams(string query)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return dict;
            string q = query.TrimStart('?');
            foreach (string part in q.Split('&'))
            {
                if (string.IsNullOrEmpty(part)) continue;
                int eq = part.IndexOf('=');
                if (eq > 0)
                {
                    string k = Uri.UnescapeDataString(part.Substring(0, eq).Replace('+', ' '));
                    string v = Uri.UnescapeDataString(part.Substring(eq + 1).Replace('+', ' '));
                    dict[k] = v;
                }
            }
            return dict;
        }

        private bool TryRenderPdfViaFitzWorker(string pdfPath, string outPngPath)
        {
            string workerPy = Path.Combine(_hubAppDir, "thumb_worker.py");
            if (string.IsNullOrEmpty(_pythonExe) || !File.Exists(_pythonExe) || !File.Exists(workerPy))
                return false;

            lock (_thumbLock)
            {
                try
                {
                    if (_thumbWorkerProc == null || _thumbWorkerProc.HasExited)
                    {
                        var psi = new ProcessStartInfo(_pythonExe, "-u \"" + workerPy + "\"")
                        {
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            RedirectStandardInput = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            StandardOutputEncoding = Encoding.UTF8
                        };
                        _thumbWorkerProc = Process.Start(psi);
                    }
                    if (_thumbWorkerProc == null) return false;

                    byte[] lineBytes = s_utf8NoBom.GetBytes(pdfPath + "\t" + outPngPath + "\n");
                    _thumbWorkerProc.StandardInput.BaseStream.Write(lineBytes, 0, lineBytes.Length);
                    _thumbWorkerProc.StandardInput.BaseStream.Flush();

                    string resp = _thumbWorkerProc.StandardOutput.ReadLine();
                    return resp != null && resp.StartsWith("OK") && File.Exists(outPngPath);
                }
                catch
                {
                    try { if (_thumbWorkerProc != null) _thumbWorkerProc.Kill(); } catch { }
                    _thumbWorkerProc = null;
                    return false;
                }
            }
        }

        private string GetOrCreateThumbnailPng(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;
            FileInfo fi;
            try { fi = new FileInfo(filePath); } catch { return null; }

            string sig = filePath.ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            string hashHex;
            using (var sha = SHA1.Create())
            {
                byte[] hb = sha.ComputeHash(Encoding.UTF8.GetBytes(sig));
                var sb = new StringBuilder(40);
                for (int i = 0; i < 16; i++) sb.Append(hb[i].ToString("x2"));
                hashHex = sb.ToString();
            }

            string outPng = Path.Combine(_thumbsDir, hashHex + ".png");
            if (File.Exists(outPng) && new FileInfo(outPng).Length > 64)
                return outPng;

            string ext = (fi.Extension ?? "").ToLowerInvariant();
            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp")
            {
                if (fi.Length <= 380 * 1024 && ext == ".png")
                    return filePath;
                if (ShellThumbnailHelper.TrySaveGdiImageThumbnailPng(filePath, outPng, 420))
                    return outPng;
                return filePath;
            }

            if (ext == ".pdf")
            {
                if (TryRenderPdfViaFitzWorker(filePath, outPng))
                    return outPng;
                if (ShellThumbnailHelper.TrySaveShellThumbnailPng(filePath, outPng, 420))
                    return outPng;
                return null;
            }

            if (ext == ".pptx" || ext == ".ppt" || ext == ".docx" || ext == ".xlsx")
            {
                if (ShellThumbnailHelper.TrySaveShellThumbnailPng(filePath, outPng, 420))
                    return outPng;
            }
            return null;
        }

        private static string ReadTextPreviewSmart(string fp, int maxBytes, out bool truncated)
        {
            truncated = false;
            byte[] buf;
            using (var fs = new FileStream(fp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long len = fs.Length;
                int toRead = (int)Math.Min(maxBytes, len);
                truncated = len > maxBytes;
                buf = new byte[toRead];
                int n = fs.Read(buf, 0, toRead);
                if (n < toRead) Array.Resize(ref buf, n);
            }
            if (buf.Length >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF)
                return Encoding.UTF8.GetString(buf, 3, buf.Length - 3);

            try
            {
                var strictUtf8 = new UTF8Encoding(false, true);
                return strictUtf8.GetString(buf);
            }
            catch
            {
                try { return Encoding.GetEncoding(936).GetString(buf); }
                catch { return Encoding.UTF8.GetString(buf); }
            }
        }

        private string BuildPreviewInfoJson(string fp)
        {
            if (string.IsNullOrEmpty(fp) || !File.Exists(fp))
                return "{\"ok\":false,\"error\":\"文件不存在\"}";

            var fi = new FileInfo(fp);
            string ext = (fi.Extension ?? "").ToLowerInvariant();
            var res = new Dictionary<string, object>
            {
                { "ok", true },
                { "name", fi.Name },
                { "path", fp },
                { "dir", fi.DirectoryName ?? "" },
                { "ext", ext },
                { "size_mb", Math.Round(fi.Length / 1048576.0, 3) },
                { "size_kb", Math.Round(fi.Length / 1024.0, 1) },
                { "date", fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss") }
            };

            // Sibling deliverable files in the same folder (huge UX helper!)
            var siblings = new List<Dictionary<string, object>>();
            try
            {
                if (!string.IsNullOrEmpty(fi.DirectoryName) && Directory.Exists(fi.DirectoryName))
                {
                    foreach (var sfi in new DirectoryInfo(fi.DirectoryName).GetFiles()
                        .Where(x => !x.Name.StartsWith("~$") && !x.FullName.Equals(fp, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(x => x.LastWriteTimeUtc)
                        .Take(12))
                    {
                        siblings.Add(new Dictionary<string, object>
                        {
                            { "name", sfi.Name },
                            { "path", sfi.FullName },
                            { "ext", (sfi.Extension ?? "").ToLowerInvariant() },
                            { "size_mb", Math.Round(sfi.Length / 1048576.0, 2) },
                            { "date", sfi.LastWriteTime.ToString("MM-dd HH:mm") }
                        });
                    }
                }
            }
            catch { }
            res["siblings"] = siblings;

            if (ext == ".csv" || ext == ".tsv")
            {
                bool trunc;
                string txt = ReadTextPreviewSmart(fp, 49152, out trunc);
                char sep = ext == ".tsv" ? '\t' : ',';
                var rows = new List<string[]>();
                using (var sr = new StringReader(txt))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null && rows.Count < 35)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        string[] cols = line.Split(sep);
                        if (cols.Length > 14) cols = cols.Take(14).ToArray();
                        rows.Add(cols);
                    }
                }
                res["mode"] = "table";
                res["rows"] = rows;
                res["truncated"] = trunc;
            }
            else if (ext == ".txt" || ext == ".md" || ext == ".r" || ext == ".py" || ext == ".sh" ||
                     ext == ".json" || ext == ".yaml" || ext == ".yml" || ext == ".log" || ext == ".sql")
            {
                bool trunc;
                string txt = ReadTextPreviewSmart(fp, 49152, out trunc);
                res["mode"] = "text";
                res["content"] = txt;
                res["truncated"] = trunc;
            }
            else if (ext == ".zip")
            {
                res["mode"] = "zip";
                var entries = new List<Dictionary<string, object>>();
                int totalEntries = 0;
                try
                {
                    using (var fs = new FileStream(fp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var za = new ZipArchive(fs, ZipArchiveMode.Read))
                    {
                        totalEntries = za.Entries.Count;
                        foreach (var e in za.Entries.Take(80))
                        {
                            if (string.IsNullOrEmpty(e.Name) && e.FullName.EndsWith("/")) continue;
                            entries.Add(new Dictionary<string, object>
                            {
                                { "name", e.FullName },
                                { "kb", Math.Round(e.Length / 1024.0, 1) }
                            });
                        }
                    }
                }
                catch { }
                res["zip_total"] = totalEntries;
                res["zip_entries"] = entries;
            }
            else
            {
                string th = GetOrCreateThumbnailPng(fp);
                res["has_thumb"] = !string.IsNullOrEmpty(th);
                res["mode"] = "binary";
            }

            return _json.Serialize(res);
        }

        private List<string[]> ExpandQueryTokens(string rawQuery)
        {
            var tokenAlts = new List<string[]>();
            if (string.IsNullOrWhiteSpace(rawQuery)) return tokenAlts;

            foreach (string rawTok in Regex.Split(rawQuery.Trim().ToLowerInvariant(), @"\s+"))
            {
                if (string.IsNullOrEmpty(rawTok)) continue;
                var alts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rawTok };
                foreach (var grp in _synonymGroups)
                {
                    if (grp.Any(w => w.Equals(rawTok, StringComparison.OrdinalIgnoreCase) || (rawTok.Length >= 2 && w.Contains(rawTok))))
                    {
                        foreach (string w in grp) alts.Add(w);
                    }
                }
                tokenAlts.Add(alts.ToArray());
            }
            return tokenAlts;
        }

        private string RunLiveDiskSearchJson(string rawQuery)
        {
            var sw = Stopwatch.StartNew();
            var tokenAlts = ExpandQueryTokens(rawQuery);
            if (tokenAlts.Count == 0 || !Directory.Exists(_rootDir))
                return "{\"items\":[],\"ms\":0}";

            var hits = new List<Dictionary<string, object>>();
            var stack = new Stack<string>();
            stack.Push(_rootDir);

            int scanned = 0;
            while (stack.Count > 0 && scanned < 95000)
            {
                string cur = stack.Pop();
                try
                {
                    foreach (string fp in Directory.GetFiles(cur))
                    {
                        scanned++;
                        string fn = Path.GetFileName(fp);
                        if (fn.StartsWith("~$") || fn.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase)) continue;

                        string rel = fp.StartsWith(_rootDir, StringComparison.OrdinalIgnoreCase)
                            ? fp.Substring(_rootDir.Length).TrimStart('\\', '/').Replace('\\', '/')
                            : fn;
                        string hay = rel.ToLowerInvariant();

                        bool allMatch = true;
                        for (int i = 0; i < tokenAlts.Count; i++)
                        {
                            string[] alts = tokenAlts[i];
                            bool anyAlt = false;
                            for (int j = 0; j < alts.Length; j++)
                            {
                                if (hay.Contains(alts[j])) { anyAlt = true; break; }
                            }
                            if (!anyAlt) { allMatch = false; break; }
                        }
                        if (!allMatch) continue;

                        FileInfo fi;
                        try { fi = new FileInfo(fp); } catch { continue; }
                        if (fi.Length == 0) continue;

                        string ext = (fi.Extension ?? "").ToLowerInvariant();
                        double mt = NativeScanner.ToUnixTime(fi.LastWriteTimeUtc);
                        string fnLow = fn.ToLowerInvariant();
                        int score = 0;
                        foreach (var alts in tokenAlts)
                        {
                            if (alts.Any(a => fnLow.Contains(a))) score += 20;
                        }
                        if (ext == ".html" || ext == ".pptx" || ext == ".pdf" || ext == ".zip" || ext == ".docx" || ext == ".xlsx" || ext == ".md" || ext == ".txt" || ext == ".png")
                            score += 10;

                        string[] parts = rel.Split('/');
                        string platDir = parts.Length > 1 ? parts[0] : "本地目录";
                        string projFolder = parts.Length > 2 ? parts[1] : Path.GetFileName(Path.GetDirectoryName(fp) ?? "");

                        hits.Add(new Dictionary<string, object>
                        {
                            { "name", fn },
                            { "path", fp },
                            { "rel", rel },
                            { "dir", fi.DirectoryName ?? "" },
                            { "proj_name", projFolder },
                            { "platform", NativeScanner.InferPlatformLabel(platDir) },
                            { "ext", ext },
                            { "size_mb", Math.Round(fi.Length / 1048576.0, 2) },
                            { "mtime", mt },
                            { "date", fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm") },
                            { "_score", score }
                        });
                    }

                    foreach (string sub in Directory.GetDirectories(cur))
                    {
                        string dn = Path.GetFileName(sub);
                        if (dn.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                            dn.Equals("__pycache__", StringComparison.OrdinalIgnoreCase) ||
                            dn.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                            dn.Equals("wv2_profile", StringComparison.OrdinalIgnoreCase) ||
                            dn.Equals("00_按课题总览", StringComparison.OrdinalIgnoreCase))
                            continue;
                        FileAttributes attr;
                        try { attr = File.GetAttributes(sub); } catch { continue; }
                        if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                        stack.Push(sub);
                    }
                }
                catch { }
            }

            var topHits = hits
                .OrderByDescending(x => Convert.ToInt32(x["_score"]))
                .ThenByDescending(x => Convert.ToDouble(x["mtime"]))
                .Take(120)
                .ToList();

            return _json.Serialize(new Dictionary<string, object>
            {
                { "items", topHits },
                { "total_hits", hits.Count },
                { "scanned", scanned },
                { "ms", (int)sw.ElapsedMilliseconds }
            });
        }

        private string BuildFolderListJson(string targetDir)
        {
            string dir = string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir) ? _rootDir : Path.GetFullPath(targetDir);
            if (!Directory.Exists(dir))
                return "{\"ok\":false,\"dirs\":[],\"files\":[]}";

            var dirs = new List<Dictionary<string, object>>();
            var files = new List<Dictionary<string, object>>();

            try
            {
                foreach (var di in new DirectoryInfo(dir).GetDirectories().OrderByDescending(d => d.LastWriteTimeUtc))
                {
                    if (di.Name.Equals("wv2_profile", StringComparison.OrdinalIgnoreCase) || di.Name.StartsWith(".")) continue;
                    dirs.Add(new Dictionary<string, object>
                    {
                        { "name", di.Name },
                        { "path", di.FullName },
                        { "mtime", NativeScanner.ToUnixTime(di.LastWriteTimeUtc) },
                        { "date", di.LastWriteTime.ToString("yyyy-MM-dd HH:mm") }
                    });
                }

                foreach (var fi in new DirectoryInfo(dir).GetFiles().Where(f => !f.Name.StartsWith("~$")).OrderByDescending(f => f.LastWriteTimeUtc))
                {
                    string ext = (fi.Extension ?? "").ToLowerInvariant();
                    string figGroup, figPanel;
                    int figOrder;
                    NativeScanner.ParseFigureTag(fi.Name, "", out figGroup, out figPanel, out figOrder);
                    files.Add(new Dictionary<string, object>
                    {
                        { "name", fi.Name },
                        { "path", fi.FullName },
                        { "rel", fi.Name },
                        { "ext", ext },
                        { "size_mb", Math.Round(fi.Length / 1048576.0, 2) },
                        { "mtime", NativeScanner.ToUnixTime(fi.LastWriteTimeUtc) },
                        { "date", fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm") },
                        { "fig_group", figGroup },
                        { "fig_panel", figPanel }
                    });
                }
            }
            catch { }

            string parentDir = "";
            try
            {
                var p = Directory.GetParent(dir);
                if (p != null && dir.Length > _rootDir.Length)
                    parentDir = p.FullName;
            }
            catch { }

            return _json.Serialize(new Dictionary<string, object>
            {
                { "ok", true },
                { "path", dir },
                { "parent", parentDir },
                { "is_root", dir.TrimEnd('\\', '/').Equals(_rootDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) },
                { "dirs", dirs },
                { "files", files }
            });
        }

        private static string GuessMimeType(string ext)
        {
            switch ((ext ?? "").ToLowerInvariant())
            {
                case ".pdf": return "application/pdf";
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".webp": return "image/webp";
                case ".gif": return "image/gif";
                case ".svg": return "image/svg+xml";
                case ".html":
                case ".htm": return "text/html; charset=utf-8";
                default: return "text/plain; charset=utf-8";
            }
        }

        private void OnWebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                Uri uri = new Uri(e.Request.Uri);
                string pathOnly = uri.AbsolutePath;

                if (pathOnly.Equals("/app", StringComparison.OrdinalIgnoreCase) || pathOnly == "/")
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
                else if (pathOnly.Equals("/api/state", StringComparison.OrdinalIgnoreCase))
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
                else if (pathOnly.Equals("/rawfile", StringComparison.OrdinalIgnoreCase))
                {
                    var qp = ParseQueryParams(uri.Query);
                    string fp;
                    if (qp.TryGetValue("path", out fp) && File.Exists(fp))
                    {
                        string ext = Path.GetExtension(fp);
                        var fs = new FileStream(fp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        e.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                            fs, 200, "OK", "Content-Type: " + GuessMimeType(ext) + "\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: no-cache");
                        return;
                    }
                    e.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(), 404, "Not Found", "Content-Type: text/plain");
                    return;
                }
                else if (pathOnly.Equals("/api/thumb", StringComparison.OrdinalIgnoreCase))
                {
                    var qp = ParseQueryParams(uri.Query);
                    string fp;
                    if (qp.TryGetValue("path", out fp))
                    {
                        string thumbPng = GetOrCreateThumbnailPng(fp);
                        if (!string.IsNullOrEmpty(thumbPng) && File.Exists(thumbPng))
                        {
                            var ms = new MemoryStream(File.ReadAllBytes(thumbPng));
                            e.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                                ms, 200, "OK", "Content-Type: image/png\r\nCache-Control: max-age=3600");
                            return;
                        }
                    }
                    e.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(), 404, "No Thumb", "Content-Type: text/plain");
                    return;
                }
                else if (pathOnly.Equals("/api/preview-info", StringComparison.OrdinalIgnoreCase))
                {
                    var qp = ParseQueryParams(uri.Query);
                    string fp;
                    qp.TryGetValue("path", out fp);
                    byte[] body = s_utf8NoBom.GetBytes(BuildPreviewInfoJson(fp));
                    e.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                        new MemoryStream(body), 200, "OK", "Content-Type: application/json; charset=utf-8\r\nCache-Control: no-store");
                    return;
                }
                else if (pathOnly.Equals("/api/live-search", StringComparison.OrdinalIgnoreCase))
                {
                    var qp = ParseQueryParams(uri.Query);
                    string qVal;
                    qp.TryGetValue("q", out qVal);
                    byte[] body = s_utf8NoBom.GetBytes(RunLiveDiskSearchJson(qVal));
                    e.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                        new MemoryStream(body), 200, "OK", "Content-Type: application/json; charset=utf-8\r\nCache-Control: no-store");
                    return;
                }
                else if (pathOnly.Equals("/api/folder-list", StringComparison.OrdinalIgnoreCase))
                {
                    var qp = ParseQueryParams(uri.Query);
                    string dVal;
                    qp.TryGetValue("path", out dVal);
                    byte[] body = s_utf8NoBom.GetBytes(BuildFolderListJson(dVal));
                    e.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                        new MemoryStream(body), 200, "OK", "Content-Type: application/json; charset=utf-8\r\nCache-Control: no-store");
                    return;
                }
                else if (pathOnly.StartsWith("/fonts/", StringComparison.OrdinalIgnoreCase))
                {
                    string userFontDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Fonts");
                    string sysFontDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                    string targetFont = null;
                    if (pathOnly.EndsWith("wenkai-bold.ttf", StringComparison.OrdinalIgnoreCase))
                        targetFont = Path.Combine(userFontDir, "LXGWWenKaiMonoBold-Base.ttf");
                    else if (pathOnly.EndsWith("wenkai-xbold.ttf", StringComparison.OrdinalIgnoreCase))
                        targetFont = Path.Combine(userFontDir, "LXGWWenKaiMonoXBold.ttf");
                    else if (pathOnly.EndsWith("wenkai-reg.ttf", StringComparison.OrdinalIgnoreCase))
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
                    e.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                        new MemoryStream(), 404, "Not Found", "Content-Type: text/plain");
                    return;
                }
                else if (pathOnly.Equals("/api/ai-config", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] body = s_utf8NoBom.GetBytes(LlmGateway.GetPublicConfigJson(_appLocalDataDir));
                    e.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                        new MemoryStream(body), 200, "OK", "Content-Type: application/json; charset=utf-8\r\nCache-Control: no-store");
                    return;
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
                        MergeAndSaveUserMeta(msg, new[] { "starred", "starred_files", "notes", "topic_overrides" });
                        break;

                    case "savePrefs":
                        MergeAndSaveUserMeta(msg, new[] { "ui_paradigm", "ui_scale", "ui_font", "finder_layout", "finder_dedup" });
                        break;

                    case "runRefresh":
                        RunScanBackendAsync(false);
                        break;

                    case "runTidy":
                        RunScanBackendAsync(true);
                        break;

                    case "saveAiConfig":
                        LlmGateway.SaveConfig(
                            _appLocalDataDir,
                            msg.ContainsKey("provider") ? Convert.ToString(msg["provider"]) : "custom",
                            msg.ContainsKey("base_url") ? Convert.ToString(msg["base_url"]) : "",
                            msg.ContainsKey("model") ? Convert.ToString(msg["model"]) : "",
                            msg.ContainsKey("api_key") ? Convert.ToString(msg["api_key"]) : "",
                            msg.ContainsKey("clear_key") && Convert.ToBoolean(msg["clear_key"]));
                        SendJsonCallbackToJs("__onAiConfigSaved", LlmGateway.GetPublicConfigJson(_appLocalDataDir));
                        break;

                    case "testAiConnection":
                        HandleTestAiConnectionAsync(msg);
                        break;

                    case "askAiAssistant":
                        HandleAskAiAssistantAsync(msg);
                        break;
                }
            }
            catch { }
        }


        private void SendJsonCallbackToJs(string fnName, string rawJsonArg)
        {
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    if (_webView != null && _webView.CoreWebView2 != null)
                        await _webView.CoreWebView2.ExecuteScriptAsync("if(window." + fnName + ")window." + fnName + "(" + rawJsonArg + ");");
                }
                catch { }
            }));
        }

        private void HandleTestAiConnectionAsync(Dictionary<string, object> msg)
        {
            Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                var saved = LlmGateway.LoadConfigRaw(_appLocalDataDir);
                string prov = msg.ContainsKey("provider") ? Convert.ToString(msg["provider"]) : saved["provider"];
                string burl = msg.ContainsKey("base_url") ? Convert.ToString(msg["base_url"]) : saved["base_url"];
                string mod  = msg.ContainsKey("model") ? Convert.ToString(msg["model"]) : saved["model"];
                string keyIn = msg.ContainsKey("api_key") ? Convert.ToString(msg["api_key"]) : "";
                string useKey = (!string.IsNullOrEmpty(keyIn) && !keyIn.Contains("••••")) ? keyIn.Trim() : saved["api_key"];

                string err;
                string reply = LlmGateway.CallLlmChat(prov, burl, useKey, mod, "Reply concisely.", "请只回复 OK 两个字母。", 24, out err);
                sw.Stop();
                string payload = _json.Serialize(new Dictionary<string, object>
                {
                    { "ok", reply != null },
                    { "ms", (int)sw.ElapsedMilliseconds },
                    { "reply", reply ?? "" },
                    { "error", err ?? "" }
                });
                SendJsonCallbackToJs("__onAiTestResult", payload);
            });
        }

        private void HandleAskAiAssistantAsync(Dictionary<string, object> msg)
        {
            Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                var cfg = LlmGateway.LoadConfigRaw(_appLocalDataDir);
                string mode = msg.ContainsKey("mode") ? Convert.ToString(msg["mode"]) : "find";
                string userPrompt = msg.ContainsKey("prompt") ? Convert.ToString(msg["prompt"]) : "";
                string curFile = msg.ContainsKey("current_file") ? Convert.ToString(msg["current_file"]) : "";
                string candsText = msg.ContainsKey("candidates_text") ? Convert.ToString(msg["candidates_text"]) : "";

                string sysPrompt =
                    "你是内置在桌面软件「模型输出管家」中的本地文件查找与科研文档助手。请始终用清晰精炼的中文回答。\n" +
                    "当用户让你找文件、或者你的回答涉及候选清单中的具体文件时，除了用中文简要说明每个推荐文件是什么，还必须在回答最后单独另起一行写出命中的候选编号，格式严格为：\n" +
                    "MATCHED_IDS: F0, F2, F5\n" +
                    "（如果没有涉及特定候选编号则不要写这一行；严禁编造候选清单以外的路径。）";

                var fullUser = new StringBuilder();
                if (!string.IsNullOrEmpty(curFile) && (mode == "summarize" || userPrompt.Contains("当前") || userPrompt.Contains("这份") || userPrompt.Contains("这个文件")))
                {
                    string excerpt = LlmGateway.ExtractCleanFileExcerpt(curFile, 9000);
                    if (!string.IsNullOrEmpty(excerpt))
                    {
                        fullUser.AppendLine("【当前右侧正在预览的文件】：" + curFile);
                        fullUser.AppendLine("【文件正文摘录】：");
                        fullUser.AppendLine(excerpt);
                        fullUser.AppendLine();
                    }
                }
                if (!string.IsNullOrEmpty(candsText))
                {
                    fullUser.AppendLine("【本地候选文件索引（编号 | 类型 | 课题 | 目录 | 文件名/标题 | 更新时间）】：");
                    fullUser.AppendLine(candsText);
                    fullUser.AppendLine();
                }
                fullUser.AppendLine("【用户需求】：" + userPrompt);

                string err;
                string answer = LlmGateway.CallLlmChat(cfg["provider"], cfg["base_url"], cfg["api_key"], cfg["model"], sysPrompt, fullUser.ToString(), 1100, out err);
                sw.Stop();

                var matchedIds = new List<string>();
                string cleanAnswer = answer ?? "";
                if (!string.IsNullOrEmpty(cleanAnswer))
                {
                    Match m = Regex.Match(cleanAnswer, @"MATCHED_IDS\s*:\s*([^\r\n]+)", RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        foreach (Match idm in Regex.Matches(m.Groups[1].Value, @"F\d+", RegexOptions.IgnoreCase))
                            matchedIds.Add(idm.Value.ToUpperInvariant());
                        cleanAnswer = cleanAnswer.Remove(m.Index, m.Length).Trim();
                    }
                }

                string payload = _json.Serialize(new Dictionary<string, object>
                {
                    { "ok", answer != null },
                    { "ms", (int)sw.ElapsedMilliseconds },
                    { "model", cfg["model"] },
                    { "answer", cleanAnswer },
                    { "matched_ids", matchedIds },
                    { "error", err ?? "" }
                });
                SendJsonCallbackToJs("__onAiReply", payload);
            });
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
                psi.EnvironmentVariables["MODEL_OUTPUT_ROOT"] = rootDir;
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
                new { Name = "v2-finder-home", Url = "https://hub.local/app?paradigm=takram&view=finder" },
                new { Name = "v2-finder-thumbs", Url = "https://hub.local/app?paradigm=takram&view=finder&ftype=a4&layout=grid" }
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
                await Task.Delay(450);

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
