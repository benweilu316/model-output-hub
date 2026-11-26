#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
build.py — Zero-VisualStudio Portable Builder for 模型输出管家 (Model Output Hub)
================================================================================
Compiles `模型输出管家.exe` using the built-in Windows .NET Framework x64 compiler (`csc.exe`)
and automatically fetches the official Microsoft.Web.WebView2 NuGet package if needed.

Output:
  dist/ModelOutputHub/
    ├── 模型输出管家.exe
    ├── README.md
    └── _hub_app/
          ├── topic_rules.py
          ├── tidy_outputs.py
          ├── custom_topics.example.json
          ├── app.ico
          └── lib/ (Microsoft.Web.WebView2.Core.dll, Microsoft.Web.WebView2.Wpf.dll, WebView2Loader.dll)
"""
import io
import os
import pathlib
import shutil
import subprocess
import sys
import urllib.request
import zipfile

sys.stdout.reconfigure(encoding="utf-8")
REPO_ROOT = pathlib.Path(__file__).resolve().parent
SRC_DIR = REPO_ROOT / "src"
DIST_APP = REPO_ROOT / "dist" / "ModelOutputHub"
HUB_DIR = DIST_APP / "_hub_app"
LIB_DIR = HUB_DIR / "lib"

NUGET_WV2_URL = "https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2/1.0.2903.40"


def ensure_webview2_libs(local_fallback_dir: pathlib.Path = None) -> None:
    LIB_DIR.mkdir(parents=True, exist_ok=True)
    needed = ("Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.Wpf.dll", "WebView2Loader.dll")
    if all((LIB_DIR / f).is_file() for f in needed):
        return

    if local_fallback_dir and all((local_fallback_dir / f).is_file() for f in needed):
        for f in needed:
            shutil.copy2(local_fallback_dir / f, LIB_DIR / f)
        print("[OK] Copied WebView2 runtime libraries from local cache.")
        return

    print("[Download] Fetching official Microsoft.Web.WebView2 package from NuGet...")
    req = urllib.request.Request(NUGET_WV2_URL, headers={"User-Agent": "ModelOutputHub-Builder/1.0"})
    raw = urllib.request.urlopen(req, timeout=30).read()
    with zipfile.ZipFile(io.BytesIO(raw)) as zf:
        mapping = {
            "lib/net462/Microsoft.Web.WebView2.Core.dll": "Microsoft.Web.WebView2.Core.dll",
            "lib/net462/Microsoft.Web.WebView2.Wpf.dll": "Microsoft.Web.WebView2.Wpf.dll",
            "runtimes/win-x64/native/WebView2Loader.dll": "WebView2Loader.dll",
        }
        for zpath, out_name in mapping.items():
            with zf.open(zpath) as src_s:
                (LIB_DIR / out_name).write_bytes(src_s.read())
    print("[OK] Extracted official WebView2 SDK DLLs.")


def build_portable_package(local_lib_fallback: pathlib.Path = None) -> pathlib.Path:
    if DIST_APP.exists():
        shutil.rmtree(DIST_APP)
    HUB_DIR.mkdir(parents=True, exist_ok=True)

    ensure_webview2_libs(local_lib_fallback)

    for fn in ("topic_rules.py", "tidy_outputs.py", "custom_topics.example.json", "app.ico"):
        shutil.copy2(SRC_DIR / fn, HUB_DIR / fn)
    if (REPO_ROOT / "README.md").is_file():
        shutil.copy2(REPO_ROOT / "README.md", DIST_APP / "README.md")

    csc = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    wpf = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF"
    if not os.path.isfile(csc):
        raise RuntimeError(f"Built-in C# compiler not found at {csc}")

    exe_out = DIST_APP / "模型输出管家.exe"
    cmd = [
        csc, "/nologo", "/target:winexe", "/platform:x64", "/optimize+", "/utf8output",
        f"/win32icon:{SRC_DIR / 'app.ico'}",
        f"/resource:{SRC_DIR / 'studio_ui.dat'},ModelOutputHub.StudioUI",
        f"/resource:{LIB_DIR / 'Microsoft.Web.WebView2.Core.dll'},ModelOutputHub.Wv2Core",
        f"/resource:{LIB_DIR / 'Microsoft.Web.WebView2.Wpf.dll'},ModelOutputHub.Wv2Wpf",
        f"/resource:{LIB_DIR / 'WebView2Loader.dll'},ModelOutputHub.Wv2Loader",
        f"/out:{exe_out}",
        f"/r:{LIB_DIR / 'Microsoft.Web.WebView2.Core.dll'}",
        f"/r:{LIB_DIR / 'Microsoft.Web.WebView2.Wpf.dll'}",
        f"/r:{wpf}\\PresentationFramework.dll",
        f"/r:{wpf}\\PresentationCore.dll",
        f"/r:{wpf}\\WindowsBase.dll",
        "/r:System.Xaml.dll",
        "/r:System.Drawing.dll",
        "/r:System.Windows.Forms.dll",
        "/r:System.Web.Extensions.dll",
        str(SRC_DIR / "ModelOutputHubApp.cs"),
    ]
    r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if r.returncode != 0:
        print(r.stdout, r.stderr, file=sys.stderr)
        raise RuntimeError("Compilation failed.")

    r_self = subprocess.run([str(exe_out), "--selftest"], capture_output=True, text=True, encoding="utf-8", errors="replace")
    print("[SelfTest]", r_self.stdout.strip())
    if r_self.returncode != 0:
        raise RuntimeError(f"SelfTest failed: {r_self.stderr}")

    zip_path = REPO_ROOT / "dist" / "ModelOutputHub-v1.0.0-win-x64.zip"
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
        for fp in sorted(DIST_APP.rglob("*")):
            if fp.is_file():
                zf.write(fp, fp.relative_to(DIST_APP))
    print(f"[Done] Portable bundle created: {zip_path} ({zip_path.stat().st_size / 1024:.1f} KB)")
    return zip_path


if __name__ == "__main__":
    fb = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else None
    build_portable_package(fb)
