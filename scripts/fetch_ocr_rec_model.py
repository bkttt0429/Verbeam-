"""Fetch PP-OCR per-language recognition models + dicts for rapidocr-net.

Reads app/scripts/ocr_rec_catalog.json and downloads the requested languages'
rec ONNX + dict into the wired model dir (default app/src/Verbeam.Api/ocr-models/,
which the Api csproj globs into the build/dist), verifying the model SHA256.
Falls back to the local rapidocr venv copy when present (no network), and tries
both the ModelScope and HuggingFace mirrors per file so it works anywhere.

Examples:
  python fetch_ocr_rec_model.py --list
  python fetch_ocr_rec_model.py --recommended
  python fetch_ocr_rec_model.py --lang ko,eslav,th
  python fetch_ocr_rec_model.py --all --mirror hf
"""
import argparse
import hashlib
import json
import shutil
import sys
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent  # app/
DEFAULT_OUT = ROOT / "src" / "Verbeam.Api" / "ocr-models"
# Single source of truth, co-located with the models so RapidOcrNetProvider reads the same file at runtime.
CATALOG = DEFAULT_OUT / "ocr_rec_catalog.json"
VENV_MODELS = ROOT / ".ocr-set" / "venv" / "Lib" / "site-packages" / "rapidocr" / "models"


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def download(url: str, dest: Path) -> None:
    dest.parent.mkdir(parents=True, exist_ok=True)
    tmp = dest.with_suffix(dest.suffix + ".part")
    req = urllib.request.Request(url, headers={"User-Agent": "verbeam-ocr-fetch"})
    with urllib.request.urlopen(req, timeout=120) as resp, open(tmp, "wb") as out:
        shutil.copyfileobj(resp, out)
    tmp.replace(dest)


def fetch_url(rel_path: str, dest: Path, bases: list) -> None:
    """Download rel_path from the first base URL that succeeds (mirror fallback)."""
    last = None
    for base in bases:
        try:
            download(f"{base}/{rel_path}", dest)
            return
        except Exception as exc:  # network / HTTP error -> try the next mirror
            host = base.split("//")[-1].split("/")[0]
            print(f"  mirror {host} failed ({exc}); trying next...")
            last = exc
    raise SystemExit(f"all mirrors failed for {rel_path}: {last}")


def fetch_one(entry: dict, catalog: dict, out_dir: Path, mirror: str) -> None:
    ms, hf = catalog["baseUrl"], catalog["hfMirrorBaseUrl"]
    bases = [hf, ms] if mirror == "hf" else [ms, hf]
    key = entry["key"]
    model_dest = out_dir / entry["model"]
    dict_dest = out_dir / entry["dict"]

    # model
    if model_dest.exists() and entry.get("sha256") and sha256(model_dest) == entry["sha256"]:
        print(f"[{key}] model already present + verified: {model_dest.name}")
    else:
        venv_copy = VENV_MODELS / entry["model"]
        if venv_copy.exists() and entry.get("sha256") and sha256(venv_copy) == entry["sha256"]:
            out_dir.mkdir(parents=True, exist_ok=True)
            shutil.copy2(venv_copy, model_dest)
            print(f"[{key}] model copied from venv: {model_dest.name}")
        else:
            print(f"[{key}] downloading model: {entry['modelPath']}")
            fetch_url(entry["modelPath"], model_dest, bases)
            got = sha256(model_dest)
            if entry.get("sha256") and got != entry["sha256"]:
                raise SystemExit(f"[{key}] SHA256 MISMATCH: got {got}, want {entry['sha256']}")
            print(f"[{key}] model OK ({model_dest.stat().st_size // 1024} KB, sha verified)")

    # dict (no SHA in catalog; prefer venv copy, else download)
    if dict_dest.exists():
        print(f"[{key}] dict already present: {dict_dest.name}")
    else:
        venv_dict = VENV_MODELS / entry["dict"]
        if venv_dict.exists():
            shutil.copy2(venv_dict, dict_dest)
            print(f"[{key}] dict copied from venv: {dict_dest.name}")
        else:
            print(f"[{key}] downloading dict: {entry['dictPath']}")
            fetch_url(entry["dictPath"], dict_dest, bases)
            print(f"[{key}] dict OK: {dict_dest.name}")


def main() -> int:
    catalog = json.loads(CATALOG.read_text(encoding="utf-8"))
    by_key = {e["key"]: e for e in catalog["recognizers"]}

    ap = argparse.ArgumentParser(description="Fetch PP-OCR per-language rec models for rapidocr-net.")
    ap.add_argument("--lang", help="comma-separated keys (e.g. ko,eslav,th); see --list")
    ap.add_argument("--recommended", action="store_true", help="fetch catalog.recommendedSet")
    ap.add_argument("--all", action="store_true", help="fetch every language")
    ap.add_argument("--out", default=str(DEFAULT_OUT), help=f"target dir (default {DEFAULT_OUT})")
    ap.add_argument("--mirror", choices=["modelscope", "hf"], default="modelscope",
                    help="primary mirror; the other is the fallback")
    ap.add_argument("--list", action="store_true", help="print the catalog and exit")
    args = ap.parse_args()

    if args.list or not (args.lang or args.recommended or args.all):
        print(f"Source: {catalog['source']} {catalog['version']}")
        print(f"Shared det (language-agnostic): {catalog['sharedDetModel']['file']}")
        print(f"Recommended set: {', '.join(catalog['recommendedSet'])}\n")
        print(f"{'key':<14}{'version':<10}{'present':<9}languages")
        for e in catalog["recognizers"]:
            print(f"{e['key']:<14}{e['version']:<10}{('yes' if e['present'] else 'no'):<9}{', '.join(e['languages'])}")
        if args.list:
            return 0
        print("\nNothing fetched. Pass --recommended, --all, or --lang <keys>.")
        return 0

    if args.all:
        keys = [e["key"] for e in catalog["recognizers"]]
    elif args.recommended:
        keys = list(catalog["recommendedSet"])
    else:
        keys = [k.strip() for k in args.lang.split(",") if k.strip()]

    out_dir = Path(args.out)
    unknown = [k for k in keys if k not in by_key]
    if unknown:
        raise SystemExit(f"Unknown language keys: {unknown}. Known: {list(by_key)}")

    print(f"Target dir: {out_dir}\nPrimary mirror: {args.mirror}\nFetching: {', '.join(keys)}\n")
    for k in keys:
        fetch_one(by_key[k], catalog, out_dir, args.mirror)
    print("\nDone. Wire a rec via Ocr:RapidOcrNet (RecModelPath/KeysPath or the per-language map).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
