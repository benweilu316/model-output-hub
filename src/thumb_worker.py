# -*- coding: utf-8 -*-
"""
thumb_worker.py — Zero-orphan anonymous-pipe PDF thumbnail worker via PyMuPDF (fitz).
Reads `<pdf_path>\t<out_png_path>\n` lines from stdin and writes `OK\n` or `ERR\n` to stdout.
Automatically terminates the instant parent `.exe` closes stdin (even on forced kill).
"""
import os
import sys

sys.stdin.reconfigure(encoding="utf-8", errors="replace")
sys.stdout.reconfigure(encoding="utf-8", errors="replace")

try:
    import fitz
except ImportError:
    sys.exit(2)


def render_pdf_thumb(pdf_path: str, out_png: str, target_w: int = 420) -> bool:
    doc = None
    try:
        doc = fitz.open(pdf_path)
        if len(doc) < 1:
            return False
        page = doc[0]
        rect = page.rect
        w = max(1.0, float(rect.width))
        scale = min(2.2, max(0.35, target_w / w))
        pix = page.get_pixmap(matrix=fitz.Matrix(scale, scale), alpha=False)
        os.makedirs(os.path.dirname(out_png), exist_ok=True)
        tmp_png = out_png + ".tmp"
        pix.save(tmp_png)
        os.replace(tmp_png, out_png)
        return True
    except Exception:
        return False
    finally:
        if doc is not None:
            try:
                doc.close()
            except Exception:
                pass


def main() -> None:
    for line in sys.stdin:
        raw = line.rstrip("\r\n")
        if not raw:
            continue
        if raw == "QUIT":
            break
        parts = raw.split("\t")
        if len(parts) < 2:
            sys.stdout.write("ERR\n")
            sys.stdout.flush()
            continue
        ok = render_pdf_thumb(parts[0], parts[1])
        sys.stdout.write("OK\n" if ok else "ERR\n")
        sys.stdout.flush()


if __name__ == "__main__":
    main()
