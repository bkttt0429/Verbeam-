#!/usr/bin/env python
from __future__ import annotations

import argparse
import importlib.util
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Any

DEFAULT_OCR_SET_ROOT = Path(__file__).resolve().parent.parent / ".ocr-set"
os.environ.setdefault("PADDLE_PDX_CACHE_HOME", str(DEFAULT_OCR_SET_ROOT / "paddlex-cache"))
os.environ.setdefault("TESSDATA_PREFIX", str(DEFAULT_OCR_SET_ROOT / "tessdata"))


def main() -> int:
    parser = argparse.ArgumentParser(description="YomiBridge local OCR set wrapper.")
    parser.add_argument(
        "--engine",
        required=True,
        choices=[
            "tesseract",
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

    if args.engine == "tesseract":
        result = run_tesseract(image, args.language)
    elif args.engine == "easyocr":
        result = run_easyocr(image, args.language)
    elif args.engine == "paddleocr":
        result = run_paddleocr(image, args.language)
    elif args.engine == "pix2text":
        result = run_pix2text(image)
    elif args.engine == "pp-structure-v3":
        result = run_pp_structure_v3(image)
    elif args.engine == "paddleocr-vl":
        result = run_paddleocr_vl(image)
    elif args.engine == "dots-ocr":
        result = run_dots_ocr(image)
    else:
        raise SystemExit(f"Unsupported OCR engine: {args.engine}")

    write_json(result)
    return 0


def check_engine(engine: str) -> dict[str, Any]:
    if engine == "tesseract":
        command = find_tesseract()
        missing = []
        if command is None:
            missing.append("tesseract")
        missing.extend(f"tessdata:{language}" for language in missing_tesseract_languages())
        return {
            "engine": engine,
            "available": len(missing) == 0,
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
    }[engine]
    missing = [module for module in modules if importlib.util.find_spec(module) is None]
    note = "Python package dependencies are available."
    if engine == "paddleocr-vl" and not missing:
        model_dir = paddleocr_vl_cache_dir()
        if model_dir is not None:
            note = f"PaddleOCR-VL client dependencies and model cache are available: {model_dir}"
        else:
            note = "PaddleOCR-VL client dependencies are available. First run may download large model files and can be slow."
    elif engine == "dots-ocr" and not missing:
        note = "dots.ocr client dependencies are available. First run downloads model weights unless already cached."

    return {
        "engine": engine,
        "available": len(missing) == 0,
        "missing": missing,
        "note": note if not missing else f"Missing Python packages: {', '.join(missing)}",
    }


def run_tesseract(image: Path, language: str) -> dict[str, Any]:
    command = find_tesseract()
    if command is None:
        raise SystemExit("tesseract command was not found.")

    lang = map_tesseract_language(language)
    command_line = [command, str(image), "stdout", "-l", lang, "--psm", "6"]
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

    text = normalize_text(process.stdout)
    return text_result(text, "local:tesseract")


def run_easyocr(image: Path, language: str) -> dict[str, Any]:
    import easyocr  # type: ignore

    languages = map_easyocr_languages(language)
    reader = easyocr.Reader(languages, gpu=False, verbose=False)
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
    ocr = PaddleOCR(lang=lang, use_angle_cls=True, show_log=False)
    raw = try_paddle_ocr(ocr, image)
    blocks = extract_paddle_blocks(raw)
    return blocks_result(blocks, "local:paddleocr")


def run_pix2text(image: Path) -> dict[str, Any]:
    from pix2text import Pix2Text  # type: ignore

    p2t = Pix2Text.from_config()
    raw = p2t.recognize(str(image))
    text = normalize_text(extract_text(raw))
    return text_result(text, "local:pix2text")


def run_pp_structure_v3(image: Path) -> dict[str, Any]:
    from paddleocr import PPStructureV3  # type: ignore

    pipeline = create_paddle_pipeline(
        PPStructureV3,
        use_doc_orientation_classify=False,
        use_doc_unwarping=False,
        use_textline_orientation=False,
        device="cpu",
    )
    raw = pipeline.predict(str(image))
    text = normalize_text(extract_text(raw))
    return structured_result(text, raw, "local:pp-structure-v3")


def run_paddleocr_vl(image: Path) -> dict[str, Any]:
    from paddleocr import PaddleOCRVL  # type: ignore

    pipeline = create_paddle_pipeline(
        PaddleOCRVL,
        use_doc_orientation_classify=False,
        use_doc_unwarping=False,
        device="cpu",
    )
    raw = pipeline.predict(str(image))
    text = normalize_text(extract_text(raw))
    return structured_result(text, raw, "local:paddleocr-vl")


def run_dots_ocr(image: Path) -> dict[str, Any]:
    from transformers import pipeline  # type: ignore

    pipe = pipeline(
        "image-text-to-text",
        model="rednote-hilab/dots.ocr",
        trust_remote_code=True,
        device=-1,
    )
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


def blocks_result(blocks: list[dict[str, Any]], engine: str) -> dict[str, Any]:
    text = "\n".join(block["text"] for block in blocks if block.get("text"))
    return {"text": text, "blocks": blocks, "engine": engine}


def text_result(text: str, engine: str) -> dict[str, Any]:
    return {
        "text": text,
        "blocks": [{"text": text, "confidence": 1.0, "boundingBox": None}] if text else [],
        "engine": engine,
    }


def structured_result(text: str, raw: Any, engine: str) -> dict[str, Any]:
    return {
        "text": text,
        "blocks": [{"text": text, "confidence": 1.0, "boundingBox": None}] if text else [],
        "engine": engine,
        "structured": True,
        "rawType": type(raw).__name__,
    }


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
        for key in ("text", "markdown", "md", "latex", "generated_text", "output_text"):
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
        normalized = []
        for point in points:
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
            "width": int(round(right - left)),
            "height": int(round(bottom - top)),
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


def paddleocr_vl_cache_dir() -> Path | None:
    candidates = [
        DEFAULT_OCR_SET_ROOT / "paddlex-cache" / "official_models" / "PaddleOCR-VL-1.6",
        DEFAULT_OCR_SET_ROOT / "paddlex-cache" / "official_models" / "PaddleOCR-VL",
    ]
    for candidate in candidates:
        if (candidate / "model.safetensors").exists():
            return candidate
    return None


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
