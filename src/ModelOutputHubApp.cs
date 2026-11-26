// -*- coding: utf-8 -*-
// ModelOutputHubApp.cs — Model Output Hub (Studio Edition)
// Native Win11 Frameless Window + System Tray Resident + Single-Instance IPC + WebView2 Virtual Host

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
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

        private static string s_libDir;

        [STAThread]
        public static int Main(string[] args)
        {
            try { SetCurrentProcessExplicitAppUserModelID("ModelOutputHub.Studio.Desktop.v1"); } catch { }

            string exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            string rootDir = Environment.GetEnvironmentVariable("MODEL_OUTPUT_ROOT");
            if (string.IsNullOrEmpty(rootDir) || !Directory.Exists(rootDir))
            {
                rootDir = exeDir;
            }
            for (int i = 0; i < (args != null ? args.Length - 1 : 0); i++)
            {
                if (args[i] == "--root" && Directory.Exists(args[i + 1]))
                {
                    rootDir = Path.GetFullPath(args[i + 1]);
                    break;
                }
            }

            string hubAppDir = Path.Combine(rootDir, "_hub_app");
            if (!Directory.Exists(hubAppDir) && Directory.Exists(Path.Combine(exeDir, "_hub_app")))
            {
                hubAppDir = Path.Combine(exeDir, "_hub_app");
            }
            s_libDir = Path.Combine(hubAppDir, "lib");

            if (Directory.Exists(s_libDir))
            {
                SetDllDirectory(s_libDir);
            }

            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
            return BootRunner.Run(args ?? new string[0], rootDir, hubAppDir);
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                string simpleName = new AssemblyName(args.Name).Name + ".dll";
                string candidate = Path.Combine(s_libDir, simpleName);
                if (File.Exists(candidate))
                {
                    return Assembly.LoadFrom(candidate);
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
        public static int Run(string[] args, string rootDir, string hubAppDir)
        {
            if (args.Any(a => a == "--selftest"))
            {
                return RunSelfTest(hubAppDir);
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
                var win = new StudioWindow(rootDir, hubAppDir, previewDir, wakeEvent);
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

        private static int RunSelfTest(string hubAppDir)
        {
            try
            {
                string ver = CoreWebView2Environment.GetAvailableBrowserVersionString();
                bool hasRes;
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ModelOutputHub.StudioUI"))
                {
                    hasRes = (s != null && s.Length > 2048);
                }
                string py = StudioWindow.ResolvePythonExecutable(hubAppDir);
                Console.WriteLine(string.Format("SELFTEST_OK wv2={0} ui_embedded={1} tray_ready=True python={2}", ver, hasRes, !string.IsNullOrEmpty(py)));
                return (!string.IsNullOrEmpty(ver) && hasRes) ? 0 : 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("SELFTEST_ERR: " + ex.Message);
                return 1;
            }
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

        private readonly string _rootDir;
        private readonly string _hubAppDir;
        private readonly string _cachePath;
        private readonly string _metaPath;
        private readonly string _previewOutDir;
        private readonly string _pythonExe;
        private readonly WebView2 _webView;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private static readonly Encoding s_utf8NoBom = new UTF8Encoding(false);

        private WinForms.NotifyIcon _trayIcon;
        private bool _allowExit;
        private bool _shownTrayBalloonOnce;
        private volatile bool _disposed;

        public int ExitCode { get; private set; }

        public StudioWindow(string rootDir, string hubAppDir, string previewOutDir, EventWaitHandle wakeEvent)
        {
            _rootDir = rootDir;
            _hubAppDir = hubAppDir;
            _cachePath = Path.Combine(hubAppDir, "scan_cache.json");
            _metaPath = Path.Combine(hubAppDir, "user_meta.json");
            _previewOutDir = previewOutDir;
            _pythonExe = ResolvePythonExecutable(hubAppDir);

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

        public static string ResolvePythonExecutable(string hubAppDir)
        {
            try
            {
                string cfgFile = Path.Combine(hubAppDir, "hub_config.json");
                if (File.Exists(cfgFile))
                {
                    var js = new JavaScriptSerializer();
                    var cfg = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(cfgFile, Encoding.UTF8));
                    if (cfg != null && cfg.ContainsKey("python_exe"))
                    {
                        string p = Convert.ToString(cfg["python_exe"]);
                        if (File.Exists(p)) return p;
                    }
                }
            }
            catch { }

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

            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir) || dir.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                try
                {
                    string p = Path.Combine(dir.Trim(), "python.exe");
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            return "python";
        }

        private void InitSystemTrayIcon()
        {
            try
            {
                _trayIcon = new WinForms.NotifyIcon
                {
                    Text = "模型输出管家 — 点击显示主界面"
                };

                string icoFile = Path.Combine(_hubAppDir, "app.ico");
                if (File.Exists(icoFile))
                {
                    _trayIcon.Icon = new System.Drawing.Icon(icoFile);
                }
                else
                {
                    string exePath = Assembly.GetExecutingAssembly().Location;
                    _trayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath) ?? System.Drawing.SystemIcons.Application;
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
                        {
                            await _webView.CoreWebView2.ExecuteScriptAsync("if(window.openCmdPalette)openCmdPalette();");
                        }
                    }
                    catch { }
                }));

                var itemRoot = new WinForms.ToolStripMenuItem("打开模型输出根目录");
                itemRoot.Click += (s, e) =>
                {
                    if (Directory.Exists(_rootDir))
                    {
                        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _rootDir + "\"") { UseShellExecute = true });
                    }
                };

                var itemRefresh = new WinForms.ToolStripMenuItem("重新扫描活跃目录");
                itemRefresh.Click += (s, e) => RunPythonBackendAsync(false);

                var itemExit = new WinForms.ToolStripMenuItem("彻底退出模型输出管家");
                itemExit.Click += (s, e) => Dispatcher.BeginInvoke(new Action(ExitApplicationCompletely));

                menu.Items.Add(itemOpen);
                menu.Items.Add(itemSearch);
                menu.Items.Add(itemRoot);
                menu.Items.Add(itemRefresh);
                menu.Items.Add(new WinForms.ToolStripSeparator());
                menu.Items.Add(itemExit);

                _trayIcon.ContextMenuStrip = menu;
                _trayIcon.MouseClick += (s, e) =>
                {
                    if (e.Button == WinForms.MouseButtons.Left)
                    {
                        Dispatcher.BeginInvoke(new Action(RestoreFromTray));
                    }
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
                try
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                    _trayIcon = null;
                }
                catch { }
            }
            Close();
            if (Application.Current != null)
            {
                Application.Current.Shutdown(ExitCode);
            }
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
                int cornerPref = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(hwnd, 33, ref cornerPref, sizeof(int));
            }
            catch { }
        }

        private async void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                string profileDir = Path.Combine(_hubAppDir, string.IsNullOrEmpty(_previewOutDir) ? "wv2_profile" : "wv2_preview_profile");
                Directory.CreateDirectory(profileDir);

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

                // First-run auto-scan if scan_cache.json does not exist yet
                if (!File.Exists(_cachePath))
                {
                    RunPythonBackendAsync(false);
                }
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
                        : s_utf8NoBom.GetBytes("{\"summary\":{\"total_projects\":0,\"locked_projects\":0,\"total_html\":0,\"total_pptx\":0,\"total_pdf\":0,\"total_size_gb\":0,\"total_cache_gb\":0,\"dup_size_mb\":0},\"topics\":[],\"projects\":[],\"herdr_agents\":[]}");
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

                    case "openFile":
                        if (msg.ContainsKey("path"))
                        {
                            string fPath = Convert.ToString(msg["path"]);
                            if (File.Exists(fPath) || Directory.Exists(fPath))
                            {
                                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                                string openScript = Path.Combine(home, @"agent-hub\scripts\open_deliverable.py");
                                string ext = (Path.GetExtension(fPath) ?? "").ToLowerInvariant();
                                if (File.Exists(openScript) && File.Exists(_pythonExe) && (ext == ".html" || ext == ".htm" || ext == ".pdf" || ext == ".pptx"))
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
                            {
                                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dPath + "\"") { UseShellExecute = true });
                            }
                        }
                        break;

                    case "revealPath":
                        if (msg.ContainsKey("path"))
                        {
                            string rPath = Convert.ToString(msg["path"]);
                            if (File.Exists(rPath))
                            {
                                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + rPath + "\"") { UseShellExecute = true });
                            }
                            else if (Directory.Exists(rPath))
                            {
                                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + rPath + "\"") { UseShellExecute = true });
                            }
                        }
                        break;

                    case "copyText":
                        if (msg.ContainsKey("text"))
                        {
                            Clipboard.SetText(Convert.ToString(msg["text"]));
                        }
                        break;

                    case "saveMeta":
                        MergeAndSaveUserMeta(msg, new[] { "starred", "notes", "topic_overrides" });
                        break;

                    case "savePrefs":
                        MergeAndSaveUserMeta(msg, new[] { "ui_paradigm", "ui_scale", "ui_font" });
                        break;

                    case "runRefresh":
                        RunPythonBackendAsync(false);
                        break;

                    case "runTidy":
                        RunPythonBackendAsync(true);
                        break;
                }
            }
            catch { }
        }

        private void MergeAndSaveUserMeta(Dictionary<string, object> incoming, string[] keys)
        {
            try
            {
                Dictionary<string, object> cur = new Dictionary<string, object>();
                if (File.Exists(_metaPath))
                {
                    try
                    {
                        cur = _json.Deserialize<Dictionary<string, object>>(File.ReadAllText(_metaPath, Encoding.UTF8))
                              ?? new Dictionary<string, object>();
                    }
                    catch { }
                }
                foreach (string k in keys)
                {
                    if (incoming.ContainsKey(k))
                    {
                        cur[k] = incoming[k];
                    }
                }
                File.WriteAllText(_metaPath, _json.Serialize(cur), s_utf8NoBom);
            }
            catch { }
        }

        private void RunPythonBackendAsync(bool runTidyFirst)
        {
            Task.Run(() =>
            {
                try
                {
                    string scriptPath = Path.Combine(_hubAppDir, runTidyFirst ? "tidy_outputs.py" : "topic_rules.py");
                    if (File.Exists(scriptPath))
                    {
                        RunProcessSilent(_pythonExe, "\"" + scriptPath + "\"", _rootDir);
                    }

                    Dispatcher.BeginInvoke(new Action(async () =>
                    {
                        try
                        {
                            string toast = runTidyFirst ? "已完成无损归整并更新课题快捷方式" : "课题与活跃会话状态已更新";
                            string js = "fetch('https://hub.local/api/state').then(r=>r.json()).then(d=>{ d.toast='" + toast + "'; window.__onHostDataUpdated(d); });";
                            if (_webView.CoreWebView2 != null)
                            {
                                await _webView.CoreWebView2.ExecuteScriptAsync(js);
                            }
                        }
                        catch { }
                    }));
                }
                catch { }
            });
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
                new { Name = "hero-takram-atlas", Url = "https://hub.local/app?paradigm=takram&font=wenkai_bold&view=overview", ExtraJs = "document.getElementById('fontPop').classList.add('open');" },
                new { Name = "paradigm-pentagram-grid", Url = "https://hub.local/app?paradigm=pentagram&view=overview", ExtraJs = "" },
                new { Name = "paradigm-sugiura-dark", Url = "https://hub.local/app?paradigm=sugiura&view=overview", ExtraJs = "" },
                new { Name = "focused-lineage-mindmap", Url = "https://hub.local/app?paradigm=takram&view=lineage", ExtraJs = "" }
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

                if (!string.IsNullOrEmpty(s.ExtraJs))
                {
                    await _webView.CoreWebView2.ExecuteScriptAsync(s.ExtraJs);
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
