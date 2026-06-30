#!/usr/bin/env python
from __future__ import annotations

import contextlib
import json
import sys
import traceback
from pathlib import Path
from typing import Any

import local_ocr_json

# importing local_ocr_json already reconfigures stdout/stderr to UTF-8;
# the worker also reads request lines, so stdin needs the same treatment.
if hasattr(sys.stdin, "reconfigure"):
    sys.stdin.reconfigure(encoding="utf-8")


def main() -> int:
    for line in sys.stdin:
        if not line.strip():
            continue

        response = handle_line(line)
        sys.stdout.write(json.dumps(response, ensure_ascii=False) + "\n")
        sys.stdout.flush()

    return 0


def handle_line(line: str) -> dict[str, Any]:
    request_id = ""
    try:
        line = line.lstrip("\ufeff")
        request = json.loads(line)
        request_id = str(request.get("id", ""))
        engine = str(request["engine"])
        image = Path(str(request["image"]))
        language = str(request.get("language") or "ja")
        preprocess = str(request.get("preprocess") or "none")

        if not image.exists():
            raise FileNotFoundError(f"Image file does not exist: {image}")

        prepared_image = local_ocr_json.prepare_image(image, preprocess)
        try:
            with contextlib.redirect_stdout(sys.stderr):
                result = local_ocr_json.run_engine(engine, prepared_image, language)
        finally:
            if prepared_image != image:
                try:
                    prepared_image.unlink()
                except OSError:
                    pass

        return {"id": request_id, "ok": True, "result": result}
    except Exception as exc:
        print(traceback.format_exc(), file=sys.stderr, flush=True)
        return {"id": request_id, "ok": False, "error": str(exc)}


if __name__ == "__main__":
    raise SystemExit(main())
