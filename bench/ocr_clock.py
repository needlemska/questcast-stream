"""Читает время с кадра, снятого из окна получателя.

Режимы:
  1) два окна на одном снимке экрана:
       ocr_clock.py shot.png --src x,y,w,h --dst x,y,w,h
  2) кадр окна плеера, где видео вписано с сохранением пропорций:
       ocr_clock.py frame.png --video 1280x720 --clock 30,30,640,130

Печатает JSON: {"ok": true, "src": "HH:MM:SS.mmm", "delta_ms": 41}
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import tempfile
from pathlib import Path

from PIL import Image, ImageOps

TIME_RE = re.compile(r"(\d{2})\D?(\d{2})\D?(\d{2})\D?(\d{3})")


def read_clock(image: Image.Image, box: tuple[int, int, int, int], tesseract: str) -> str | None:
    x, y, w, h = (int(v) for v in box)
    part = image.crop((max(0, x), max(0, y), max(0, x) + w, max(0, y) + h)).convert("L")
    part = ImageOps.invert(part)
    part = part.resize((part.width * 2, part.height * 2), Image.LANCZOS)
    with tempfile.TemporaryDirectory() as td:
        png = Path(td) / "crop.png"
        part.save(png)
        out = subprocess.run(
            [tesseract, str(png), "stdout", "--psm", "7",
             "-c", "tessedit_char_whitelist=0123456789:."],
            capture_output=True, text=True, timeout=30,
        )
    text = (out.stdout or "").replace(" ", "").strip()
    m = TIME_RE.search(text)
    if not m:
        return None
    hh, mm, ss, ms = m.groups()
    if not (0 <= int(hh) < 24 and 0 <= int(mm) < 60 and 0 <= int(ss) < 60):
        return None
    return f"{hh}:{mm}:{ss}.{ms}"


def to_ms(stamp: str) -> int:
    hh, mm, rest = stamp.split(":")
    ss, ms = rest.split(".")
    return ((int(hh) * 60 + int(mm)) * 60 + int(ss)) * 1000 + int(ms)


def video_rect(image: Image.Image, video_w: int, video_h: int, clock_box: tuple[int, int, int, int]):
    """Где часы оказались в кадре при вписывании видео в окно с сохранением пропорций."""
    scale = min(image.width / video_w, image.height / video_h)
    x0 = (image.width - video_w * scale) / 2
    y0 = (image.height - video_h * scale) / 2
    cx, cy, cw, ch = clock_box
    return (
        int(x0 + cx * scale),
        int(y0 + cy * scale),
        max(8, int(cw * scale)),
        max(8, int(ch * scale)),
    )


def parse_pair(text: str) -> tuple[int, int]:
    a, b = text.lower().split("x")
    return int(a), int(b)


def auto_candidates(image: Image.Image, max_bands: int = 6) -> list[tuple[int, int, int, int]]:
    """Кандидаты на строку цифр: горизонтальные полосы с плотным ярким текстом.

    Позиция окна, DPI и масштаб плеера перестают иметь значение, а стенд не
    ломается, когда окно переехало. Кандидаты отдаются от самых высоких полос:
    строка цифр заметно выше строки заголовка вкладки.
    """
    gray = image.convert("L")
    w, h = gray.size
    px = gray.load()
    limit_x, limit_y = int(w * 0.75), int(h * 0.5)

    rows: list[int] = []
    for y in range(limit_y):
        bright = 0
        for x in range(0, limit_x, 2):
            if px[x, y] > 170:
                bright += 1
        rows.append(bright)

    if not rows or max(rows) < 8:
        return []
    threshold = max(6, int(max(rows) * 0.25))
    dense = [y for y, n in enumerate(rows) if n >= threshold]

    bands: list[tuple[int, int]] = []
    start = prev = dense[0]
    for y in dense[1:]:
        if y - prev > 4:
            bands.append((start, prev))
            start = y
        prev = y
    bands.append((start, prev))

    boxes: list[tuple[int, int, int, int]] = []
    for y0, y1 in sorted(bands, key=lambda b: b[1] - b[0], reverse=True)[:max_bands]:
        if y1 - y0 < 10:
            continue
        xs = [x for y in range(y0, y1 + 1) for x in range(0, limit_x) if px[x, y] > 170]
        if not xs:
            continue
        x0, x1 = min(xs), max(xs)
        if x1 - x0 < 60:
            continue
        pad = 12
        boxes.append((max(0, x0 - pad), max(0, y0 - pad), x1 - x0 + 2 * pad, y1 - y0 + 2 * pad))
    return boxes


def parse_box(text: str) -> tuple[int, int, int, int]:
    parts = [int(v) for v in text.split(",")]
    if len(parts) != 4:
        raise argparse.ArgumentTypeError("box must be x,y,w,h")
    return tuple(parts)  # type: ignore[return-value]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("image")
    ap.add_argument("--src", type=parse_box, help="x,y,w,h области источника")
    ap.add_argument("--dst", type=parse_box, help="x,y,w,h области получателя на снимке")
    ap.add_argument("--video", type=parse_pair, help="WxH закодированного видео")
    ap.add_argument("--clock", type=parse_box, help="x,y,w,h часов внутри видео")
    ap.add_argument("--auto", action="store_true", help="найти часы на кадре самому")
    ap.add_argument("--tesseract", default=r"C:\Program Files\Tesseract-OCR\tesseract.exe")
    args = ap.parse_args()

    image = Image.open(args.image).convert("RGB")

    if args.src and args.dst:
        a = read_clock(image, args.src, args.tesseract)
        b = read_clock(image, args.dst, args.tesseract)
        if not a or not b:
            print(json.dumps({"ok": False, "src": a, "dst": b}))
            return 1
        delta = to_ms(b) - to_ms(a)
        if delta < -30_000:
            delta += 86_400_000
        print(json.dumps({"ok": True, "src": a, "dst": b, "delta_ms": delta}))
        return 0

    if args.auto:
        candidates = auto_candidates(image)
        for box in candidates:
            t = read_clock(image, box, args.tesseract)
            if t:
                print(json.dumps({"ok": True, "drawn": t, "drawn_ms": to_ms(t), "box": box}))
                return 0
        print(json.dumps({"ok": False, "drawn": None, "candidates": len(candidates)}))
        return 1

    if args.video and args.clock:
        box = video_rect(image, args.video[0], args.video[1], args.clock)
        t = read_clock(image, box, args.tesseract)
        if not t:
            print(json.dumps({"ok": False, "drawn": None, "box": box}))
            return 1
        print(json.dumps({"ok": True, "drawn": t, "drawn_ms": to_ms(t), "box": box}))
        return 0

    ap.error("нужно либо --src/--dst, либо --video/--clock")
    return 2


if __name__ == "__main__":
    sys.exit(main())
