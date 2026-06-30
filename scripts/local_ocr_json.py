#!/usr/bin/env python
from __future__ import annotations

import argparse
import csv
import importlib.util
import json
import os
import re
import shutil
import subprocess
import sys
from html.parser import HTMLParser
from pathlib import Path
from typing import Any

# Windows consoles default to the legacy code page (e.g. cp950), which cannot
# encode all OCR output; force UTF-8 stdio regardless of how we were launched.
for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8")

DEFAULT_OCR_SET_ROOT = Path(__file__).resolve().parent.parent / ".ocr-set"
os.environ.setdefault("PADDLE_PDX_CACHE_HOME", str(DEFAULT_OCR_SET_ROOT / "paddlex-cache"))
os.environ.setdefault("TESSDATA_PREFIX", str(DEFAULT_OCR_SET_ROOT / "tessdata"))
os.environ.setdefault("HF_HOME", str(DEFAULT_OCR_SET_ROOT / "hf-cache"))

MODEL_WEIGHT_PATTERNS = ("*.safetensors", "*.bin", "*.gguf", "*.pdparams", "*.onnx")
GENERIC_MODEL_MIN_WEIGHT_BYTES = 50 * 1024 * 1024
PADDLEOCR_VL_MIN_WEIGHT_BYTES = 1_000_000_000
DOTS_OCR_MIN_WEIGHT_BYTES = 500 * 1024 * 1024
PREPROCESS_PRESETS = (
    "none",
    "upscale",
    "contrast",
    "threshold",
    "denoise",
    "crop-padding",
    "text-line",
    "screenshot",
    "document",
    "table",
    "formula",
    "subtitle",
)
MODEL_CACHE: dict[str, Any] = {}


def main() -> int:
    parser = argparse.ArgumentParser(description="Verbeam local OCR set wrapper.")
    parser.add_argument(
        "--engine",
        required=True,
        choices=[
            "tesseract",
            "rapidocr-ppocrv5",
            "easyocr",
            "paddleocr",
            "pix2text",
            "pp-structure-v3",
            "paddleocr-vl",
            "dots-ocr",
        ],
    )
    parser.add_argument("--image")
    parser.add_argument("--language", default="ja")
    parser.add_argument("--preprocess", default="none", choices=PREPROCESS_PRESETS)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()

    if args.check:
        write_json(check_engine(args.engine))
        return 0

    if not args.image:
        raise SystemExit("--image is required unless --check is used.")

    image = Path(args.image)
    if not image.exists():
        raise SystemExit(f"Image file does not exist: {image}")

    prepared_image = prepare_image(image, args.preprocess)
    try:
        result = run_engine(args.engine, prepared_image, args.language)
    finally:
        if prepared_image != image:
            try:
                prepared_image.unlink()
            except OSError:
                pass

    write_json(result)
    return 0


def prepare_image(image: Path, preset: str) -> Path:
    if preset == "none":
        return image

    try:
        from PIL import Image, ImageFilter, ImageOps  # type: ignore
    except ImportError as exc:
        raise SystemExit("Pillow is required for OCR preprocessing presets. Install pillow in the OCR venv.") from exc

    with Image.open(image) as source:
        resampling = getattr(getattr(Image, "Resampling", Image), "LANCZOS")
        prepared = source.convert("L")

        if preset in {"contrast", "screenshot", "document", "table", "formula", "text-line", "subtitle"}:
            prepared = ImageOps.autocontrast(prepared)

        if preset in {"denoise", "document", "text-line", "subtitle"}:
            prepared = prepared.filter(ImageFilter.MedianFilter(size=3))

        if preset in {"upscale", "screenshot", "table", "formula", "text-line", "subtitle"}:
            prepared = prepared.resize((prepared.width * 2, prepared.height * 2), resampling)

        if preset in {"threshold", "text-line"}:
            prepared = ImageOps.autocontrast(prepared)
            prepared = prepared.point(lambda value: 255 if value >= 160 else 0)

        if preset in {"crop-padding", "text-line"}:
            prepared = ImageOps.expand(prepared, border=12, fill=255)

        output_path = image.with_name(f"{image.stem}.preprocessed-{preset}-{os.getpid()}.png")
        prepared.save(output_path, format="PNG")
        return output_path


def run_engine(engine: str, image: Path, language: str) -> dict[str, Any]:
    if engine == "tesseract":
        return run_tesseract(image, language)
    if engine == "rapidocr-ppocrv5":
        return run_rapidocr_ppocrv5(image)
    if engine == "easyocr":
        return run_easyocr(image, language)
    if engine == "paddleocr":
        return run_paddleocr(image, language)
    if engine == "pix2text":
        return run_pix2text(image)
    if engine == "pp-structure-v3":
        return run_pp_structure_v3(image)
    if engine == "paddleocr-vl":
        return run_paddleocr_vl(image)
    if engine == "dots-ocr":
        return run_dots_ocr(image)
    raise SystemExit(f"Unsupported OCR engine: {engine}")


def check_engine(engine: str) -> dict[str, Any]:
    if engine == "tesseract":
        command = find_tesseract()
        missing = []
        if command is None:
            missing.append("tesseract")
        missing.extend(f"tessdata:{language}" for language in missing_tesseract_languages())
        available = len(missing) == 0
        return {
            "engine": engine,
            "available": available,
            "status": "available" if available else "missing_dependency",
            "missing": missing,
            "note": "tesseract command and CJK language data are available."
            if not missing
            else "Install Tesseract OCR and Japanese/Chinese language data.",
        }

    modules = {
        "easyocr": ["easyocr"],
        "paddleocr": ["paddleocr", "paddle"],
        "pix2text": ["pix2text"],
        "pp-structure-v3": ["paddleocr", "paddle"],
        "paddleocr-vl": ["paddleocr", "paddle"],
        "dots-ocr": ["torch", "transformers", "qwen_vl_utils"],
    }.get(engine, [])
    if engine == "rapidocr-ppocrv5":
        rapidocr_module = find_rapidocr_module()
        if rapidocr_module is None:
            return {
                "engine": engine,
                "available": False,
                "status": "missing_dependency",
                "missing": ["rapidocr or rapidocr_onnxruntime"],
                "note": "Install RapidOCR runtime for the low-latency text-line OCR path.",
            }
        return {
            "engine": engine,
            "available": True,
            "status": "available",
            "missing": [],
            "note": f"RapidOCR runtime is available through Python module '{rapidocr_module}'.",
        }
    missing = [module for module in modules if importlib.util.find_spec(module) is None]
    if missing:
        return {
            "engine": engine,
            "available": False,
            "status": "missing_dependency",
            "missing": missing,
            "note": f"Missing Python packages: {', '.join(missing)}",
        }

    available = True
    status = "available"
    note = "Python package dependencies are available."
    model_cache = None
    if engine == "paddleocr-vl":
        model_cache = paddleocr_vl_cache_health()
        if model_cache["healthy"]:
            note = (
                "PaddleOCR-VL client dependencies and model cache are available: "
                f"{model_cache['path']} ({format_bytes(model_cache['totalBytes'])}, "
                f"{model_cache['weightFiles']} weight file(s))."
            )
        else:
            available = False
            status = "model_missing"
            missing.append("model:PaddleOCR-VL-1.6")
            note = (
                "PaddleOCR-VL client dependencies are installed, but model cache is incomplete "
                f"({model_cache['reason']}). First run may download about 1.9 GB."
            )
    elif engine == "dots-ocr":
        model_cache = hf_model_cache_health("rednote-hilab/dots.ocr", DOTS_OCR_MIN_WEIGHT_BYTES)
        if model_cache["healthy"]:
            note = (
                "dots.ocr client dependencies and model cache are available: "
                f"{model_cache['path']} ({format_bytes(model_cache['totalBytes'])}, "
                f"{model_cache['weightFiles']} weight file(s))."
            )
        else:
            available = False
            status = "model_missing"
            missing.append("model:rednote-hilab/dots.ocr")
            note = (
                "dots.ocr client dependencies are installed, but Hugging Face model cache is incomplete "
                f"({model_cache['reason']}). First run may download large model weights."
            )

    result = {
        "engine": engine,
        "available": available,
        "status": status,
        "missing": missing,
        "note": note,
    }
    if model_cache is not None:
        result["modelCache"] = model_cache
    return result


def run_tesseract(image: Path, language: str) -> dict[str, Any]:
    command = find_tesseract()
    if command is None:
        raise SystemExit("tesseract command was not found.")

    lang = map_tesseract_language(language)
    command_line = [command, str(image), "stdout", "-l", lang, "--psm", "6", "tsv"]
    tessdata_dir = find_tessdata_dir()
    if tessdata_dir is not None:
        command_line.extend(["--tessdata-dir", str(tessdata_dir)])

    process = subprocess.run(
        command_line,
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    if process.returncode != 0:
        raise SystemExit(process.stderr.strip() or f"tesseract failed with exit code {process.returncode}")

    blocks = parse_tesseract_tsv_lines(process.stdout)
    if blocks:
        return blocks_result(blocks, "local:tesseract")

    text = normalize_text(process.stdout)
    return text_result(text, "local:tesseract")


def parse_tesseract_tsv_lines(tsv: str) -> list[dict[str, Any]]:
    rows = csv.DictReader(tsv.splitlines(), delimiter="\t")
    grouped: dict[tuple[str, str, str], dict[str, Any]] = {}
    order: list[tuple[str, str, str]] = []

    for row in rows:
        if row.get("level") != "5":
            continue

        text = normalize_text(row.get("text", ""))
        if not text:
            continue

        confidence = as_float(row.get("conf", 0))
        if confidence < 0:
            confidence = 0

        try:
            left = int(float(row.get("left", "0")))
            top = int(float(row.get("top", "0")))
            width = int(float(row.get("width", "0")))
            height = int(float(row.get("height", "0")))
        except ValueError:
            continue

        if width <= 0 or height <= 0:
            continue

        key = (
            row.get("block_num", "0"),
            row.get("par_num", "0"),
            row.get("line_num", "0"),
        )
        if key not in grouped:
            grouped[key] = {
                "texts": [],
                "confidences": [],
                "left": left,
                "top": top,
                "right": left + width,
                "bottom": top + height,
            }
            order.append(key)

        item = grouped[key]
        item["texts"].append(text)
        item["confidences"].append(confidence)
        item["left"] = min(item["left"], left)
        item["top"] = min(item["top"], top)
        item["right"] = max(item["right"], left + width)
        item["bottom"] = max(item["bottom"], top + height)

    blocks = []
    for key in order:
        item = grouped[key]
        line_text = normalize_text(" ".join(item["texts"]))
        if not line_text:
            continue

        confidences = item["confidences"]
        confidence = sum(confidences) / len(confidences) / 100 if confidences else 1.0
        blocks.append(
            {
                "text": line_text,
                "confidence": max(0.0, min(1.0, confidence)),
                "boundingBox": {
                    "x": item["left"],
                    "y": item["top"],
                    "width": max(1, item["right"] - item["left"]),
                    "height": max(1, item["bottom"] - item["top"]),
                },
            }
        )

    return blocks


def run_rapidocr_ppocrv5(image: Path) -> dict[str, Any]:
    rapidocr_module = find_rapidocr_module()
    params = None
    if rapidocr_module == "rapidocr":
        from rapidocr import RapidOCR  # type: ignore

        try:
            # rapidocr defaults to PP-OCRv4 models, whose `ch` recognizer
            # outputs Simplified lookalikes and drops Traditional-Chinese text
            # next to ASCII. The unified PP-OCRv5 models (the version this
            # engine is named after) handle Simplified/Traditional/Japanese in
            # one model at comparable latency.
            from rapidocr import OCRVersion  # type: ignore

            params = {
                "Det.ocr_version": OCRVersion.PPOCRV5,
                "Rec.ocr_version": OCRVersion.PPOCRV5,
            }
        except ImportError:
            params = None
    elif rapidocr_module == "rapidocr_onnxruntime":
        from rapidocr_onnxruntime import RapidOCR  # type: ignore
    else:
        raise SystemExit("RapidOCR runtime was not found. Install rapidocr or rapidocr_onnxruntime.")

    engine = MODEL_CACHE.get("rapidocr-ppocrv5")
    if engine is None:
        engine = RapidOCR(params=params) if params else RapidOCR()
        MODEL_CACHE["rapidocr-ppocrv5"] = engine

    raw = engine(str(image))
    blocks = extract_rapidocr_blocks(raw)
    return blocks_result(blocks, "local:rapidocr-ppocrv5")


def run_easyocr(image: Path, language: str) -> dict[str, Any]:
    import easyocr  # type: ignore

    languages = map_easyocr_languages(language)
    cache_key = "easyocr:" + ",".join(languages)
    reader = MODEL_CACHE.get(cache_key)
    if reader is None:
        reader = easyocr.Reader(languages, gpu=False, verbose=False)
        MODEL_CACHE[cache_key] = reader
    rows = reader.readtext(str(image), detail=1, paragraph=False)

    blocks = []
    for row in rows:
        points, text, confidence = row
        blocks.append(
            {
                "text": str(text),
                "confidence": float(confidence),
                "boundingBox": bounding_box_from_points(points),
            }
        )

    return blocks_result(blocks, "local:easyocr")


def run_paddleocr(image: Path, language: str) -> dict[str, Any]:
    from paddleocr import PaddleOCR  # type: ignore

    lang = map_paddle_language(language)
    cache_key = f"paddleocr:{lang}"
    ocr = MODEL_CACHE.get(cache_key)
    if ocr is None:
        ocr = PaddleOCR(lang=lang, use_angle_cls=True, show_log=False)
        MODEL_CACHE[cache_key] = ocr
    raw = try_paddle_ocr(ocr, image)
    blocks = extract_paddle_blocks(raw)
    return blocks_result(blocks, "local:paddleocr")


def run_pix2text(image: Path) -> dict[str, Any]:
    from pix2text import Pix2Text  # type: ignore

    p2t = MODEL_CACHE.get("pix2text")
    if p2t is None:
        p2t = Pix2Text.from_config()
        MODEL_CACHE["pix2text"] = p2t
    raw = p2t.recognize(str(image))
    text = normalize_text(extract_text(raw))
    return text_result(text, "local:pix2text")


def run_pp_structure_v3(image: Path) -> dict[str, Any]:
    from paddleocr import PPStructureV3  # type: ignore

    pipeline = MODEL_CACHE.get("pp-structure-v3")
    if pipeline is None:
        pipeline = create_paddle_pipeline(
            PPStructureV3,
            use_doc_orientation_classify=False,
            use_doc_unwarping=False,
            use_textline_orientation=False,
            device="cpu",
        )
        MODEL_CACHE["pp-structure-v3"] = pipeline
    raw = pipeline.predict(str(image))
    text = normalize_text(extract_text(raw))
    return structured_result(text, raw, "local:pp-structure-v3")


def run_paddleocr_vl(image: Path) -> dict[str, Any]:
    from paddleocr import PaddleOCRVL  # type: ignore

    pipeline = MODEL_CACHE.get("paddleocr-vl")
    if pipeline is None:
        pipeline = create_paddle_pipeline(
            PaddleOCRVL,
            use_doc_orientation_classify=False,
            use_doc_unwarping=False,
            device="cpu",
        )
        MODEL_CACHE["paddleocr-vl"] = pipeline
    raw = pipeline.predict(str(image))
    text = normalize_text(extract_text(raw))
    return structured_result(text, raw, "local:paddleocr-vl")


def run_dots_ocr(image: Path) -> dict[str, Any]:
    from transformers import pipeline  # type: ignore

    pipe = MODEL_CACHE.get("dots-ocr")
    if pipe is None:
        pipe = pipeline(
            "image-text-to-text",
            model="rednote-hilab/dots.ocr",
            trust_remote_code=True,
            device=-1,
        )
        MODEL_CACHE["dots-ocr"] = pipe
    messages = [
        {
            "role": "user",
            "content": [
                {"type": "image", "image": str(image)},
                {"type": "text", "text": "Parse this image into markdown. Preserve formulas and tables."},
            ],
        }
    ]
    raw = pipe(messages)
    text = normalize_text(extract_text(raw))
    return structured_result(text, raw, "local:dots-ocr")


def create_paddle_pipeline(factory: Any, **kwargs: Any) -> Any:
    try:
        return factory(**kwargs)
    except TypeError:
        return factory()


def try_paddle_ocr(ocr: Any, image: Path) -> Any:
    if hasattr(ocr, "ocr"):
        try:
            return ocr.ocr(str(image), cls=True)
        except TypeError:
            return ocr.ocr(str(image))

    if hasattr(ocr, "predict"):
        return ocr.predict(str(image))

    raise SystemExit("Unsupported PaddleOCR API surface.")


def extract_paddle_blocks(raw: Any) -> list[dict[str, Any]]:
    blocks: list[dict[str, Any]] = []

    def visit(value: Any) -> None:
        if value is None:
            return

        if isinstance(value, dict):
            for key in ("rec_texts", "texts"):
                if key in value and isinstance(value[key], list):
                    scores = value.get("rec_scores") or value.get("scores") or []
                    boxes = value.get("rec_boxes") or value.get("dt_polys") or value.get("boxes") or []
                    for index, text in enumerate(value[key]):
                        confidence = as_float(scores[index]) if index < len(scores) else 1.0
                        box = bounding_box_from_points(boxes[index]) if index < len(boxes) else None
                        add_block(blocks, text, confidence, box)
                    return
            for child in value.values():
                visit(child)
            return

        if isinstance(value, (list, tuple)):
            if len(value) >= 2 and isinstance(value[1], (list, tuple)) and len(value[1]) >= 2 and isinstance(value[1][0], str):
                add_block(blocks, value[1][0], as_float(value[1][1]), bounding_box_from_points(value[0]))
                return
            for child in value:
                visit(child)
            return

    visit(raw)
    return blocks


def extract_rapidocr_blocks(raw: Any) -> list[dict[str, Any]]:
    value = raw
    if isinstance(value, tuple) and value:
        value = value[0]

    if hasattr(value, "boxes") and hasattr(value, "txts"):
        # numpy arrays raise on truthiness checks, so compare against None instead of using `or`.
        def listify(attr: str) -> list[Any]:
            attr_value = getattr(value, attr, None)
            return [] if attr_value is None else list(attr_value)

        boxes = listify("boxes")
        texts = listify("txts")
        scores = listify("scores")
        return [
            {
                "text": str(text),
                "confidence": as_float(scores[index] if index < len(scores) else 1.0),
                "boundingBox": bounding_box_from_points(boxes[index] if index < len(boxes) else None),
            }
            for index, text in enumerate(texts)
            if str(text).strip()
        ]

    blocks: list[dict[str, Any]] = []
    if isinstance(value, list):
        for item in value:
            if isinstance(item, dict):
                text = item.get("text") or item.get("txt") or item.get("rec_text") or ""
                points = item.get("points") or item.get("box") or item.get("dt_polys")
                confidence = item.get("confidence") or item.get("score") or item.get("rec_score") or 1.0
            elif isinstance(item, (list, tuple)) and len(item) >= 2:
                points = item[0]
                text = item[1]
                confidence = item[2] if len(item) >= 3 else 1.0
            else:
                continue

            text_value = str(text)
            if not text_value.strip():
                continue

            blocks.append(
                {
                    "text": text_value,
                    "confidence": as_float(confidence),
                    "boundingBox": bounding_box_from_points(points),
                }
            )

    return blocks


def blocks_result(blocks: list[dict[str, Any]], engine: str) -> dict[str, Any]:
    text = "\n".join(block["text"] for block in blocks if block.get("text"))
    return {"text": text, "blocks": blocks, "engine": engine, "document": document_from_text_blocks(blocks, engine)}


def text_result(text: str, engine: str) -> dict[str, Any]:
    return {
        "text": text,
        "blocks": [{"text": text, "confidence": 1.0, "boundingBox": None}] if text else [],
        "engine": engine,
        "document": document_from_markdown_or_text(text, engine),
    }


def structured_result(text: str, raw: Any, engine: str) -> dict[str, Any]:
    document = document_from_structured_raw(raw, text, engine)
    blocks = text_blocks_from_document(document)
    if not blocks and text:
        blocks = [{"text": text, "confidence": 1.0, "boundingBox": None}]

    return {
        "text": text,
        "blocks": blocks,
        "engine": engine,
        "structured": True,
        "rawType": type(raw).__name__,
        "document": document,
    }


def document_from_text_blocks(blocks: list[dict[str, Any]], engine: str) -> dict[str, Any]:
    return {
        "version": "ocr-ir-v1",
        "pages": [
            {
                "pageIndex": 0,
                "blocks": [
                    {
                        "id": f"p0-b{index}",
                        "type": "text",
                        "text": normalize_text(str(block.get("text", ""))),
                        "confidence": as_float(block.get("confidence", 1.0)),
                        "boundingBox": block.get("boundingBox"),
                        "polygon": polygon_from_box(block.get("boundingBox")),
                        "readingOrder": index,
                        "engine": engine,
                        "shouldTranslate": True,
                        "children": [],
                    }
                    for index, block in enumerate(blocks)
                    if normalize_text(str(block.get("text", "")))
                ],
            }
        ],
    }


def document_from_structured_raw(raw: Any, text: str, engine: str) -> dict[str, Any]:
    blocks = extract_structured_blocks(raw, engine)
    if not blocks:
        return document_from_markdown_or_text(text or extract_text(raw), engine)

    return {
        "version": "ocr-ir-v1",
        "pages": [{"pageIndex": 0, "blocks": normalize_reading_order(blocks, engine)}],
    }


def document_from_markdown_or_text(text: str, engine: str) -> dict[str, Any]:
    return {
        "version": "ocr-ir-v1",
        "pages": [{"pageIndex": 0, "blocks": markdown_blocks(text, engine)}],
    }


def extract_structured_blocks(raw: Any, engine: str) -> list[dict[str, Any]]:
    blocks: list[dict[str, Any]] = []
    for item in iter_parsing_items(raw):
        block = structured_block_from_item(item, engine, len(blocks))
        if block is not None:
            blocks.append(block)

    return normalize_reading_order(blocks, engine)


def iter_parsing_items(value: Any, depth: int = 0) -> list[Any]:
    if depth > 8 or value is None or isinstance(value, (str, bytes, int, float, bool)):
        return []

    if isinstance(value, dict):
        parsing_res_list = value.get("parsing_res_list")
        if isinstance(parsing_res_list, list):
            return parsing_res_list

        items: list[Any] = []
        for key in (
            "layoutParsingResults",
            "layout_parsing_results",
            "pages",
            "results",
            "blocks",
            "res",
            "json",
            "markdown",
        ):
            if key in value:
                items.extend(iter_parsing_items(value[key], depth + 1))
        return items

    if isinstance(value, (list, tuple)):
        items = []
        for item in value:
            nested = iter_parsing_items(item, depth + 1)
            if nested:
                items.extend(nested)
            elif is_potential_block(item):
                items.append(item)
        return items

    for attribute in (
        "parsing_res_list",
        "layoutParsingResults",
        "layout_parsing_results",
        "pages",
        "results",
        "blocks",
        "json",
        "res",
        "markdown",
    ):
        if hasattr(value, attribute):
            try:
                nested = iter_parsing_items(getattr(value, attribute), depth + 1)
            except Exception:
                nested = []
            if nested:
                return nested

    return []


def is_potential_block(value: Any) -> bool:
    if isinstance(value, dict):
        return any(key in value for key in ("block_content", "content", "text", "markdown", "latex", "html"))
    return any(hasattr(value, key) for key in ("block_content", "content", "text", "markdown", "latex", "html"))


def structured_block_from_item(item: Any, engine: str, index: int) -> dict[str, Any] | None:
    label = str(first_value(item, "block_label", "label", "type", "block_type", "category") or "")
    content = normalize_text(str(first_value(item, "block_content", "content", "text", "markdown", "md", "html", "latex") or ""))
    if not content:
        content = normalize_text(extract_text(item))
    if not content:
        return None

    box = bounding_box_from_points(
        first_value(item, "block_bbox", "bbox", "box", "boundingBox", "bounding_box", "poly", "polygon", "points")
    )
    block_type = infer_block_type(label, content)
    block = base_document_block(index, block_type, content, engine, box)
    if block_type == "formula":
        block["shouldTranslate"] = False
        block["formula"] = {"latex": strip_formula_delimiters(content), "sourceText": content, "shouldTranslate": False}
    elif block_type == "table":
        block["shouldTranslate"] = False
        block["table"] = table_from_content(content)
    elif block_type in ("code", "figure"):
        block["shouldTranslate"] = False

    return block


def markdown_blocks(text: str, engine: str) -> list[dict[str, Any]]:
    normalized = normalize_text(text)
    if not normalized:
        return []

    lines = normalized.split("\n")
    blocks: list[dict[str, Any]] = []
    index = 0
    while index < len(lines):
        line = lines[index]
        if is_markdown_table_start(lines, index):
            table_lines = [line]
            index += 1
            while index < len(lines) and "|" in lines[index]:
                table_lines.append(lines[index])
                index += 1
            content = "\n".join(table_lines)
            block = base_document_block(len(blocks), "table", content, engine, None)
            block["shouldTranslate"] = False
            block["table"] = table_from_markdown(content)
            blocks.append(block)
            continue

        block_type = infer_block_type("", line)
        block = base_document_block(len(blocks), block_type, line, engine, None)
        if block_type == "formula":
            block["shouldTranslate"] = False
            block["formula"] = {"latex": strip_formula_delimiters(line), "sourceText": line, "shouldTranslate": False}
        elif block_type in ("code", "figure"):
            block["shouldTranslate"] = False
        blocks.append(block)
        index += 1

    return blocks


def base_document_block(
    index: int,
    block_type: str,
    text: str,
    engine: str,
    box: dict[str, int] | None,
) -> dict[str, Any]:
    return {
        "id": f"p0-b{index}",
        "type": block_type,
        "text": text,
        "confidence": 1.0,
        "boundingBox": box,
        "polygon": polygon_from_box(box),
        "readingOrder": index,
        "engine": engine,
        "shouldTranslate": block_type not in {"formula", "code", "figure"},
        "children": [],
    }


def normalize_reading_order(blocks: list[dict[str, Any]], engine: str) -> list[dict[str, Any]]:
    values = []
    for index, block in enumerate(blocks):
        block.setdefault("id", f"p0-b{index}")
        block.setdefault("type", "unknown")
        block.setdefault("text", "")
        block.setdefault("confidence", 1.0)
        block.setdefault("boundingBox", None)
        block.setdefault("polygon", polygon_from_box(block.get("boundingBox")))
        block["readingOrder"] = index
        block.setdefault("engine", engine)
        block.setdefault("shouldTranslate", block.get("type") not in {"formula", "code", "figure"})
        block.setdefault("children", [])
        values.append(block)
    return values


def infer_block_type(label: str, content: str) -> str:
    value = f"{label} {content[:200]}".lower()
    if "table" in value or content.lstrip().lower().startswith("<table") or looks_like_markdown_table(content):
        return "table"
    if "formula" in value or "equation" in value or "latex" in value or looks_like_formula(content):
        return "formula"
    if "title" in value or "heading" in value:
        return "title"
    if "code" in value:
        return "code"
    if "figure" in value or "chart" in value or "image" in value:
        return "figure"
    return "text"


def looks_like_formula(text: str) -> bool:
    stripped = text.strip()
    if stripped.startswith("$$") and stripped.endswith("$$"):
        return True
    if stripped.startswith("$") and stripped.endswith("$") and len(stripped) > 2:
        return True
    return bool(re.search(r"\\(frac|sum|int|sqrt|begin|alpha|beta|gamma|theta|pi|times|cdot)\b", stripped))


def strip_formula_delimiters(text: str) -> str:
    stripped = text.strip()
    if stripped.startswith("$$") and stripped.endswith("$$") and len(stripped) >= 4:
        return stripped[2:-2].strip()
    if stripped.startswith("$") and stripped.endswith("$") and len(stripped) >= 2:
        return stripped[1:-1].strip()
    return stripped


def looks_like_markdown_table(text: str) -> bool:
    lines = [line.strip() for line in text.splitlines() if line.strip()]
    return any(is_markdown_table_start(lines, index) for index in range(max(0, len(lines) - 1)))


def is_markdown_table_start(lines: list[str], index: int) -> bool:
    if index + 1 >= len(lines):
        return False
    return "|" in lines[index] and bool(re.match(r"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?\s*$", lines[index + 1]))


def table_from_content(content: str) -> dict[str, Any]:
    stripped = content.strip()
    if stripped.lower().startswith("<table") or "<table" in stripped.lower():
        table = table_from_html(stripped)
        if table["cells"]:
            return table
    if looks_like_markdown_table(stripped):
        return table_from_markdown(stripped)

    return table_from_rows([[stripped]])


def table_from_markdown(content: str) -> dict[str, Any]:
    rows: list[list[str]] = []
    for line in content.splitlines():
        stripped = line.strip()
        if not stripped or "|" not in stripped:
            continue
        if re.match(r"^\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?$", stripped):
            continue
        cells = [cell.strip() for cell in stripped.strip("|").split("|")]
        rows.append(cells)
    return table_from_rows(rows)


def table_from_html(content: str) -> dict[str, Any]:
    parser = HtmlTableParser()
    parser.feed(content)
    return table_from_rows(parser.rows, spans=parser.spans)


def table_from_rows(rows: list[list[str]], spans: list[list[tuple[int, int]]] | None = None) -> dict[str, Any]:
    row_count = len(rows)
    column_count = max((len(row) for row in rows), default=0)
    cells = []
    for row_index, row in enumerate(rows):
        for column_index, text in enumerate(row):
            row_span = 1
            column_span = 1
            if spans and row_index < len(spans) and column_index < len(spans[row_index]):
                row_span, column_span = spans[row_index][column_index]
            cells.append(
                {
                    "id": f"r{row_index}-c{column_index}",
                    "rowIndex": row_index,
                    "columnIndex": column_index,
                    "rowSpan": row_span,
                    "columnSpan": column_span,
                    "text": normalize_text(text),
                    "boundingBox": None,
                    "polygon": [],
                    "confidence": 1.0,
                    "shouldTranslate": should_translate_table_cell(text),
                }
            )

    return {"rowCount": row_count, "columnCount": column_count, "cells": cells}


def should_translate_table_cell(text: str) -> bool:
    value = text.strip()
    if not value:
        return False
    if looks_like_formula(value):
        return False
    if re.fullmatch(r"[\d\s.,:%+\-*/=()［］\[\]{}<>¥$€£]+", value):
        return False
    return True


class HtmlTableParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__()
        self.rows: list[list[str]] = []
        self.spans: list[list[tuple[int, int]]] = []
        self._current_row: list[str] | None = None
        self._current_spans: list[tuple[int, int]] | None = None
        self._current_cell: list[str] | None = None
        self._current_span = (1, 1)

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        if tag == "tr":
            self._current_row = []
            self._current_spans = []
        elif tag in {"td", "th"} and self._current_row is not None:
            attr_map = {key.lower(): value for key, value in attrs}
            self._current_cell = []
            self._current_span = (
                max(1, int_or_default(attr_map.get("rowspan"), 1)),
                max(1, int_or_default(attr_map.get("colspan"), 1)),
            )

    def handle_data(self, data: str) -> None:
        if self._current_cell is not None:
            self._current_cell.append(data)

    def handle_endtag(self, tag: str) -> None:
        if tag in {"td", "th"} and self._current_cell is not None and self._current_row is not None:
            self._current_row.append(normalize_text("".join(self._current_cell)))
            if self._current_spans is not None:
                self._current_spans.append(self._current_span)
            self._current_cell = None
            self._current_span = (1, 1)
        elif tag == "tr" and self._current_row is not None:
            self.rows.append(self._current_row)
            self.spans.append(self._current_spans or [])
            self._current_row = None
            self._current_spans = None


def int_or_default(value: str | None, fallback: int) -> int:
    try:
        return int(value or fallback)
    except (TypeError, ValueError):
        return fallback


def text_blocks_from_document(document: dict[str, Any]) -> list[dict[str, Any]]:
    blocks = []
    for page in document.get("pages", []):
        for block in page.get("blocks", []):
            text = normalize_text(str(block.get("text", "")))
            if text:
                blocks.append(
                    {
                        "text": text,
                        "confidence": as_float(block.get("confidence", 1.0)),
                        "boundingBox": block.get("boundingBox"),
                    }
                )
    return blocks


def polygon_from_box(box: Any) -> list[dict[str, float]]:
    if not isinstance(box, dict):
        return []
    x = as_float(box.get("x"))
    y = as_float(box.get("y"))
    width = as_float(box.get("width"))
    height = as_float(box.get("height"))
    return [
        {"x": x, "y": y},
        {"x": x + width, "y": y},
        {"x": x + width, "y": y + height},
        {"x": x, "y": y + height},
    ]


def first_value(item: Any, *names: str) -> Any:
    if isinstance(item, dict):
        for name in names:
            if name in item:
                return item[name]
        return None

    for name in names:
        if hasattr(item, name):
            try:
                return getattr(item, name)
            except Exception:
                pass
    return None


def add_block(blocks: list[dict[str, Any]], text: Any, confidence: float, box: Any) -> None:
    value = normalize_text(str(text))
    if value:
        blocks.append({"text": value, "confidence": confidence, "boundingBox": box})


def extract_text(value: Any) -> str:
    if value is None:
        return ""
    if isinstance(value, str):
        return value
    if isinstance(value, dict):
        parsed_blocks = extract_parsing_blocks_text(value)
        if parsed_blocks:
            return parsed_blocks
        for key in (
            "text",
            "markdown_texts",
            "markdown_text",
            "markdown",
            "md",
            "latex",
            "html",
            "content",
            "block_content",
            "generated_text",
            "output_text",
            "page_content",
        ):
            if key in value:
                return extract_text(value[key])
        return "\n".join(extract_text(item) for item in value.values())
    if isinstance(value, (list, tuple)):
        return "\n".join(extract_text(item) for item in value)
    for attribute in ("content", "block_content", "markdown", "text", "json", "res"):
        if hasattr(value, attribute):
            try:
                return extract_text(getattr(value, attribute))
            except Exception:
                pass
    return str(value)


def extract_parsing_blocks_text(value: dict[str, Any]) -> str:
    parsing_res_list = value.get("parsing_res_list")
    if not isinstance(parsing_res_list, list):
        return ""

    parts = []
    for item in parsing_res_list:
        if isinstance(item, dict):
            text = item.get("block_content") or item.get("content")
        else:
            text = getattr(item, "content", None)
        if text:
            parts.append(str(text))

    return normalize_text("\n".join(parts))


def bounding_box_from_points(points: Any) -> dict[str, int] | None:
    if points is None:
        return None

    try:
        if hasattr(points, "tolist"):
            points = points.tolist()

        if isinstance(points, dict):
            if all(key in points for key in ("x", "y", "width", "height")):
                return {
                    "x": int(round(as_float(points.get("x")))),
                    "y": int(round(as_float(points.get("y")))),
                    "width": max(1, int(round(as_float(points.get("width"))))),
                    "height": max(1, int(round(as_float(points.get("height"))))),
                }
            if all(key in points for key in ("left", "top", "right", "bottom")):
                left = as_float(points.get("left"))
                top = as_float(points.get("top"))
                right = as_float(points.get("right"))
                bottom = as_float(points.get("bottom"))
                return {
                    "x": int(round(left)),
                    "y": int(round(top)),
                    "width": max(1, int(round(right - left))),
                    "height": max(1, int(round(bottom - top))),
                }

        if isinstance(points, (list, tuple)) and len(points) == 4 and all(
            isinstance(value, (int, float)) for value in points
        ):
            left, top, right, bottom = (float(value) for value in points)
            return {
                "x": int(round(left)),
                "y": int(round(top)),
                "width": max(1, int(round(right - left))),
                "height": max(1, int(round(bottom - top))),
            }

        normalized = []
        for point in points:
            if hasattr(point, "tolist"):
                point = point.tolist()
            if isinstance(point, dict):
                normalized.append((float(point.get("x", 0)), float(point.get("y", 0))))
            elif isinstance(point, (list, tuple)) and len(point) >= 2:
                normalized.append((float(point[0]), float(point[1])))
        if not normalized:
            return None
        xs = [point[0] for point in normalized]
        ys = [point[1] for point in normalized]
        left = min(xs)
        top = min(ys)
        right = max(xs)
        bottom = max(ys)
        return {
            "x": int(round(left)),
            "y": int(round(top)),
            "width": max(1, int(round(right - left))),
            "height": max(1, int(round(bottom - top))),
        }
    except (TypeError, ValueError):
        return None


def normalize_text(value: str) -> str:
    return "\n".join(line.strip() for line in value.replace("\r\n", "\n").replace("\r", "\n").split("\n") if line.strip())


def as_float(value: Any) -> float:
    try:
        return float(value)
    except (TypeError, ValueError):
        return 1.0


def find_tesseract() -> str | None:
    command = shutil.which("tesseract")
    if command:
        return command
    common = Path("C:/Program Files/Tesseract-OCR/tesseract.exe")
    return str(common) if common.exists() else None


def find_rapidocr_module() -> str | None:
    for module in ("rapidocr", "rapidocr_onnxruntime"):
        if importlib.util.find_spec(module) is not None:
            return module
    return None


def paddleocr_vl_cache_dir() -> Path | None:
    health = paddleocr_vl_cache_health()
    return Path(health["path"]) if health["healthy"] else None


def paddleocr_vl_cache_health() -> dict[str, Any]:
    candidates = [
        DEFAULT_OCR_SET_ROOT / "paddlex-cache" / "official_models" / "PaddleOCR-VL-1.6",
        DEFAULT_OCR_SET_ROOT / "paddlex-cache" / "official_models" / "PaddleOCR-VL",
    ]
    return model_cache_health(candidates, PADDLEOCR_VL_MIN_WEIGHT_BYTES, ["model.safetensors"])


def hf_model_cache_dir(model_id: str) -> Path | None:
    health = hf_model_cache_health(model_id, GENERIC_MODEL_MIN_WEIGHT_BYTES)
    return Path(health["path"]) if health["healthy"] else None


def hf_model_cache_health(model_id: str, min_total_bytes: int) -> dict[str, Any]:
    cache_name = "models--" + model_id.replace("/", "--")
    candidates = [
        Path(os.environ.get("HF_HOME", "")) / "hub" / cache_name,
        Path.home() / ".cache" / "huggingface" / "hub" / cache_name,
    ]
    return model_cache_health(candidates, min_total_bytes, [])


def model_cache_health(candidates: list[Path], min_total_bytes: int, expected_files: list[str]) -> dict[str, Any]:
    evaluated = [evaluate_model_cache(candidate, min_total_bytes, expected_files) for candidate in candidates if candidate.exists()]
    if not evaluated:
        return {
            "healthy": False,
            "path": "",
            "totalBytes": 0,
            "weightFiles": 0,
            "largestWeightBytes": 0,
            "minTotalBytes": min_total_bytes,
            "reason": "model directory was not found",
        }

    healthy = next((item for item in evaluated if item["healthy"]), None)
    if healthy is not None:
        return healthy

    return max(evaluated, key=lambda item: item["totalBytes"])


def evaluate_model_cache(path: Path, min_total_bytes: int, expected_files: list[str]) -> dict[str, Any]:
    missing_expected = [name for name in expected_files if not (path / name).exists()]
    weight_files = list_model_weight_files(path)
    total_bytes = 0
    largest_bytes = 0
    for file_path in weight_files:
        try:
            size = file_path.stat().st_size
        except OSError:
            continue
        total_bytes += size
        largest_bytes = max(largest_bytes, size)

    reasons = []
    if missing_expected:
        reasons.append("missing expected file(s): " + ", ".join(missing_expected))
    if not weight_files:
        reasons.append("no model weight files found")
    elif total_bytes < min_total_bytes:
        reasons.append(f"model weights are too small: {format_bytes(total_bytes)} < {format_bytes(min_total_bytes)}")

    return {
        "healthy": not reasons,
        "path": str(path),
        "totalBytes": total_bytes,
        "weightFiles": len(weight_files),
        "largestWeightBytes": largest_bytes,
        "minTotalBytes": min_total_bytes,
        "reason": "; ".join(reasons) if reasons else "ok",
    }


def has_model_weight_file(path: Path) -> bool:
    return model_weight_total_bytes(path) >= GENERIC_MODEL_MIN_WEIGHT_BYTES

def model_weight_total_bytes(path: Path) -> int:
    total = 0
    for file_path in list_model_weight_files(path):
        try:
            total += file_path.stat().st_size
        except OSError:
            continue
    return total


def list_model_weight_files(path: Path) -> list[Path]:
    if not path.exists():
        return []

    files: list[Path] = []
    for pattern in MODEL_WEIGHT_PATTERNS:
        files.extend(path.rglob(pattern))
    return sorted(set(files), key=lambda item: str(item).lower())


def format_bytes(value: int) -> str:
    size = float(value)
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if size < 1024 or unit == "TB":
            return f"{size:.1f} {unit}" if unit != "B" else f"{int(size)} B"
        size /= 1024
    return f"{value} B"


def required_tesseract_languages() -> list[str]:
    return ["eng", "jpn", "chi_tra", "chi_sim"]


def missing_tesseract_languages() -> list[str]:
    tessdata_dir = find_tessdata_dir()
    if tessdata_dir is None:
        return required_tesseract_languages()

    return [
        language
        for language in required_tesseract_languages()
        if not (tessdata_dir / f"{language}.traineddata").exists()
    ]


def find_tessdata_dir() -> Path | None:
    candidates = []
    env_value = os.environ.get("TESSDATA_PREFIX")
    if env_value:
        candidates.append(Path(env_value))
    candidates.append(Path(__file__).resolve().parent.parent / ".ocr-set" / "tessdata")
    candidates.append(Path("C:/Program Files/Tesseract-OCR/tessdata"))

    for candidate in candidates:
        if candidate.exists():
            return candidate
    return None


def map_tesseract_language(language: str) -> str:
    value = language.lower()
    if value.startswith("ja") or value.startswith("jp"):
        return "jpn"
    if value.startswith("zh-tw") or value.startswith("zh-hant"):
        return "chi_tra"
    if value.startswith("zh"):
        return "chi_sim"
    if value.startswith("ko"):
        return "kor"
    return "eng" if value.startswith("en") else language


def map_easyocr_languages(language: str) -> list[str]:
    value = language.lower()
    if value.startswith("ja") or value.startswith("jp"):
        return ["ja", "en"]
    if value.startswith("zh-tw") or value.startswith("zh-hant"):
        return ["ch_tra", "en"]
    if value.startswith("zh"):
        return ["ch_sim", "en"]
    if value.startswith("ko"):
        return ["ko", "en"]
    return ["en"]


def map_paddle_language(language: str) -> str:
    value = language.lower()
    if value.startswith("ja") or value.startswith("jp"):
        return "japan"
    if value.startswith("zh"):
        return "ch"
    if value.startswith("ko"):
        return "korean"
    return "en"


def write_json(value: dict[str, Any]) -> None:
    sys.stdout.write(json.dumps(value, ensure_ascii=False, separators=(",", ":")))


if __name__ == "__main__":
    raise SystemExit(main())
