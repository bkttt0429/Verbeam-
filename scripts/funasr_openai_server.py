import argparse
import os
import tempfile
import time
from pathlib import Path
from typing import Any

from fastapi import FastAPI, File, Form, UploadFile
from fastapi.responses import JSONResponse, PlainTextResponse


app = FastAPI(title="Verbeam FunASR Server")
asr_model: Any | None = None
server_config: dict[str, Any] = {}


def resolve_model_reference(model_name: str) -> str:
    explicit_path = Path(model_name)
    if explicit_path.exists():
        return str(explicit_path)

    normalized = model_name.replace("\\", "/").lower()
    if normalized not in {"sensevoice", "sensevoicesmall", "iic/sensevoicesmall"}:
        return model_name

    candidates: list[Path] = []
    modelscope_cache = os.environ.get("MODELSCOPE_CACHE")
    if modelscope_cache:
        candidates.append(Path(modelscope_cache) / "models" / "iic" / "SenseVoiceSmall")

    funasr_model_cache = os.environ.get("FUNASR_MODEL_CACHE")
    if funasr_model_cache:
        candidates.append(Path(funasr_model_cache) / "modelscope" / "models" / "iic" / "SenseVoiceSmall")

    for candidate in candidates:
        if (candidate / "config.yaml").exists() and (candidate / "model.pt").exists():
            return str(candidate)

    return "iic/SenseVoiceSmall" if normalized in {"sensevoice", "sensevoicesmall"} else model_name


def create_model(model_name: str, device: str) -> Any:
    from funasr import AutoModel

    model_reference = resolve_model_reference(model_name)
    server_config["model_reference"] = model_reference

    return AutoModel(
        model=model_reference,
        vad_model="fsmn-vad",
        vad_kwargs={"max_single_segment_time": 30000},
        device=device,
        disable_update=True,
    )


def load_model_with_retries(model_name: str, device: str, retries: int, retry_delay: int) -> Any:
    last_error: Exception | None = None
    for attempt in range(1, max(retries, 1) + 1):
        try:
            print(f"Loading ASR model attempt {attempt}/{max(retries, 1)}: {model_name}", flush=True)
            return create_model(model_name, device)
        except Exception as exc:
            last_error = exc
            print(f"ASR model load failed on attempt {attempt}: {exc}", flush=True)
            if attempt < max(retries, 1):
                time.sleep(max(retry_delay, 1))

    raise RuntimeError(f"ASR model load failed after {max(retries, 1)} attempts.") from last_error


def clean_text(value: str) -> str:
    try:
        from funasr.utils.postprocess_utils import rich_transcription_postprocess

        return rich_transcription_postprocess(value)
    except Exception:
        return value.strip()


def normalize_time(value: Any) -> float:
    try:
        number = float(value)
    except (TypeError, ValueError):
        return 0.0

    return number / 1000.0 if number > 1000 else number


def segment_from_sentence(item: dict[str, Any], index: int, language: str | None) -> dict[str, Any] | None:
    text = clean_text(str(item.get("text") or item.get("sentence") or "").strip())
    if not text:
        return None

    start = item.get("start") or item.get("start_time") or item.get("startTime") or 0
    end = item.get("end") or item.get("end_time") or item.get("endTime") or start
    speaker = item.get("speaker") or item.get("spk") or item.get("speaker_id")
    return {
        "id": index,
        "start": normalize_time(start),
        "end": normalize_time(end),
        "text": text,
        "confidence": float(item.get("confidence") or item.get("score") or 1.0),
        "speaker": None if speaker is None else str(speaker),
        "language": language,
    }


def normalize_result(raw_result: Any, language: str | None) -> dict[str, Any]:
    items = raw_result if isinstance(raw_result, list) else [raw_result]
    segments: list[dict[str, Any]] = []
    text_parts: list[str] = []

    for result_item in items:
        if not isinstance(result_item, dict):
            continue

        sentence_info = result_item.get("sentence_info")
        if isinstance(sentence_info, list):
            for sentence in sentence_info:
                if isinstance(sentence, dict):
                    segment = segment_from_sentence(sentence, len(segments), language)
                    if segment is not None:
                        segments.append(segment)
                        text_parts.append(segment["text"])

        item_text = clean_text(str(result_item.get("text") or "").strip())
        if item_text and not text_parts:
            text_parts.append(item_text)

    full_text = "\n".join(part for part in text_parts if part).strip()
    if not segments and full_text:
        segments.append(
            {
                "id": 0,
                "start": 0.0,
                "end": 0.0,
                "text": full_text,
                "confidence": 1.0,
                "speaker": None,
                "language": language,
            }
        )

    return {"text": full_text, "segments": segments}


@app.get("/health")
def health() -> dict[str, Any]:
    return {
        "status": "ok" if asr_model is not None else "loading",
        "model": server_config.get("model"),
        "modelReference": server_config.get("model_reference"),
        "device": server_config.get("device"),
    }


@app.post("/v1/audio/transcriptions")
async def transcribe(
    file: UploadFile = File(...),
    model: str = Form("sensevoice"),
    language: str | None = Form(None),
    response_format: str = Form("verbose_json"),
    hotword: str | None = Form(None),
    hotwords: str | None = Form(None),
):
    if asr_model is None:
        return JSONResponse({"error": "ASR model is still loading."}, status_code=503)

    suffix = Path(file.filename or "audio.wav").suffix or ".wav"
    with tempfile.NamedTemporaryFile(delete=False, suffix=suffix) as temp_file:
        temp_path = temp_file.name
        while chunk := await file.read(1024 * 1024):
            temp_file.write(chunk)

    started = time.perf_counter()
    try:
        generate_kwargs: dict[str, Any] = {
            "input": temp_path,
            "language": language or "auto",
            "use_itn": True,
            "batch_size_s": 60,
            "merge_vad": True,
            "merge_length_s": 15,
        }

        hotword_text = hotword or hotwords
        if hotword_text:
            generate_kwargs["hotword"] = hotword_text

        raw_result = asr_model.generate(**generate_kwargs)
        normalized = normalize_result(raw_result, language)
        normalized["duration"] = round(time.perf_counter() - started, 3)
        normalized["model"] = model
    finally:
        try:
            os.remove(temp_path)
        except OSError:
            pass

    if response_format == "text":
        return PlainTextResponse(normalized["text"])

    return JSONResponse(normalized)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8000)
    parser.add_argument("--device", default="cpu")
    parser.add_argument("--model", default="iic/SenseVoiceSmall")
    parser.add_argument("--load-retries", type=int, default=5)
    parser.add_argument("--retry-delay", type=int, default=15)
    args = parser.parse_args()

    global asr_model, server_config
    server_config = {"model": args.model, "device": args.device}
    asr_model = load_model_with_retries(args.model, args.device, args.load_retries, args.retry_delay)

    import uvicorn

    uvicorn.run(app, host=args.host, port=args.port, log_level="info")


if __name__ == "__main__":
    main()
