#!/usr/bin/env python
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Render PDF pages for Verbeam document OCR.")
    parser.add_argument("pdf", help="Input PDF path.")
    parser.add_argument("--count", action="store_true", help="Only print page count JSON.")
    parser.add_argument("--text", action="store_true", help="Print embedded text blocks for every page as JSON.")
    parser.add_argument("--page", type=int, help="Zero-based page index to render.")
    parser.add_argument("--output", help="Output image path for --page, or output PDF for --export.")
    parser.add_argument("--dpi", type=int, default=180)
    parser.add_argument("--quality", type=int, default=90)
    parser.add_argument("--export", action="store_true",
                        help="Overlay-export: mask original text and draw translations from --spec onto the PDF.")
    parser.add_argument("--spec", help="JSON spec of per-page translated blocks (for --export).")
    parser.add_argument("--font", help="CJK font file used to draw translated text (for --export).")
    return parser.parse_args()


_ALIGN = {"left": 0, "center": 1, "right": 2, "justify": 3}


def _insert_fitted_text(page, scratch_page, rect, text, fontname, font_kwargs, align, overflow, requested):
    """Draw text into rect once, shrinking the font to fit when overflow == 'shrink'.
    Fit is probed on a scratch page so the real page is drawn exactly once (insert_textbox
    renders even on overflow, so measuring on the live page would double-ink)."""
    min_size = 4.0
    size = requested if requested and requested > 0 else max(min_size, min(rect.height * 0.78, 24.0))
    if overflow == "shrink":
        while size > min_size:
            remaining = scratch_page.insert_textbox(
                rect, text, fontname=fontname, fontsize=size, align=align, **font_kwargs)
            if remaining >= 0:
                break
            size = max(min_size, size - 0.5)
    page.insert_textbox(rect, text, fontname=fontname, fontsize=size,
                        color=(0, 0, 0), align=align, **font_kwargs)


def export_overlay(document, spec_path: str, output_path: Path, font_path: str | None) -> dict:
    import fitz  # type: ignore

    spec = json.loads(Path(spec_path).read_text(encoding="utf-8"))
    variant = (spec.get("variant") or "mono").lower()
    fontname = "F0"
    font_kwargs = {"fontfile": font_path} if font_path else {}
    source_path = document.name
    scratch = fitz.open()
    drawn = 0
    try:
        for page_spec in spec.get("pages", []):
            index = int(page_spec.get("pageIndex", 0))
            if index < 0 or index >= document.page_count:
                continue
            page = document.load_page(index)
            blocks = [b for b in page_spec.get("blocks", []) if (b.get("text") or "").strip()]
            if not blocks:
                continue

            # 1) mask the original content under every translated block, then apply once.
            for block in blocks:
                page.add_redact_annot(_rect(fitz, block), fill=(1, 1, 1))
            page.apply_redactions()

            # 2) draw each translation fitted into its (possibly user-moved) box.
            scratch_page = scratch.new_page(width=page.rect.width, height=page.rect.height)
            for block in blocks:
                _insert_fitted_text(
                    page,
                    scratch_page,
                    _rect(fitz, block),
                    block["text"],
                    fontname,
                    font_kwargs,
                    _ALIGN.get((block.get("align") or "left").lower(), 0),
                    (block.get("overflow") or "shrink").lower(),
                    float(block.get("fontSize") or 0))
                drawn += 1

        # Subset the embedded CJK font to only the glyphs actually drawn: turns a ~13 MB
        # full-font embed into a few hundred KB. (PyMuPDF + fontTools.)
        try:
            document.subset_fonts()
        except Exception:
            pass

        output_path.parent.mkdir(parents=True, exist_ok=True)
        if variant == "dual":
            # Bilingual: interleave each original page with its translated page.
            original = fitz.open(source_path)
            combined = fitz.open()
            try:
                for index in range(document.page_count):
                    combined.insert_pdf(original, from_page=index, to_page=index)
                    combined.insert_pdf(document, from_page=index, to_page=index)
                combined.save(str(output_path), garbage=4, deflate=True)
            finally:
                original.close()
                combined.close()
        else:
            document.save(str(output_path), garbage=4, deflate=True)
    finally:
        scratch.close()
    return {"output": str(output_path), "blocks": drawn, "variant": variant}


def _rect(fitz, block):
    x = float(block["x"])
    y = float(block["y"])
    return fitz.Rect(x, y, x + float(block["w"]), y + float(block["h"]))


def extract_text_pages(document) -> dict:
    pages = []
    for page_index in range(document.page_count):
        page = document.load_page(page_index)
        blocks = []
        char_count = 0
        for block in page.get_text("blocks"):
            # block: (x0, y0, x1, y1, text, block_no, block_type); type 1 = image block
            if len(block) >= 7 and block[6] != 0:
                continue
            text = (block[4] or "").strip()
            if not text:
                continue
            char_count += sum(1 for character in text if not character.isspace())
            blocks.append({
                "text": text,
                "bbox": [round(float(value), 2) for value in block[:4]],
            })
        pages.append({
            "pageIndex": page_index,
            "charCount": char_count,
            "width": int(round(page.rect.width)),
            "height": int(round(page.rect.height)),
            "blocks": blocks,
        })
    return {"pageCount": document.page_count, "pages": pages}


def main() -> int:
    args = parse_args()
    pdf_path = Path(args.pdf)
    if not pdf_path.exists():
        raise SystemExit(f"PDF file does not exist: {pdf_path}")

    try:
        import fitz  # type: ignore
    except Exception as exc:
        raise SystemExit("PyMuPDF is required for PDF document OCR. Install it in the OCR Python environment with: pip install PyMuPDF") from exc

    document = fitz.open(str(pdf_path))
    try:
        if args.count:
            print(json.dumps({"pageCount": document.page_count}, ensure_ascii=False))
            return 0

        if args.text:
            print(json.dumps(extract_text_pages(document), ensure_ascii=False))
            return 0

        if args.export:
            if not args.spec or not args.output:
                raise SystemExit("--spec and --output are required for --export.")
            result = export_overlay(document, args.spec, Path(args.output), args.font)
            print(json.dumps(result, ensure_ascii=False))
            return 0

        if args.page is None or not args.output:
            raise SystemExit("--page and --output are required unless --count or --text is used.")
        if args.page < 0 or args.page >= document.page_count:
            raise SystemExit(f"Page index is out of range: {args.page}")

        output_path = Path(args.output)
        output_path.parent.mkdir(parents=True, exist_ok=True)
        page = document.load_page(args.page)
        matrix = fitz.Matrix(args.dpi / 72, args.dpi / 72)
        pixmap = page.get_pixmap(matrix=matrix, alpha=False)
        if output_path.suffix.lower() in {".jpg", ".jpeg"}:
            pixmap.save(str(output_path), jpg_quality=max(1, min(100, args.quality)))
        else:
            pixmap.save(str(output_path))
        # imageWidth/Height: rendered pixel size (bbox space for re-OCR pages).
        # pageWidth/Height: logical PDF points (72 dpi) used to normalize text-layer bboxes.
        print(json.dumps({
            "page": args.page,
            "output": str(output_path),
            "dpi": int(args.dpi),
            "imageWidth": int(pixmap.width),
            "imageHeight": int(pixmap.height),
            "pageWidth": round(float(page.rect.width), 2),
            "pageHeight": round(float(page.rect.height), 2),
        }, ensure_ascii=False))
        return 0
    finally:
        document.close()


if __name__ == "__main__":
    raise SystemExit(main())
