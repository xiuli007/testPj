"""Offline JSON-lines worker for the supplied sherpa-onnx and CTranslate2 models."""
import json
import os
import sys
import time
import wave
import ctypes
from ctypes import wintypes

os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"

import ctranslate2
import numpy as np
import sherpa_onnx
from opencc import OpenCC
from tokenizers import Tokenizer


def emit(value):
    print(json.dumps(value, ensure_ascii=False), flush=True)


def working_set_megabytes():
    class ProcessMemoryCounters(ctypes.Structure):
        _fields_ = [
            ("cb", wintypes.DWORD),
            ("PageFaultCount", wintypes.DWORD),
            ("PeakWorkingSetSize", ctypes.c_size_t),
            ("WorkingSetSize", ctypes.c_size_t),
            ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
            ("QuotaPagedPoolUsage", ctypes.c_size_t),
            ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
            ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
            ("PagefileUsage", ctypes.c_size_t),
            ("PeakPagefileUsage", ctypes.c_size_t),
        ]

    counters = ProcessMemoryCounters()
    counters.cb = ctypes.sizeof(counters)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    psapi = ctypes.WinDLL("psapi", use_last_error=True)
    kernel32.GetCurrentProcess.restype = wintypes.HANDLE
    psapi.GetProcessMemoryInfo.argtypes = [
        wintypes.HANDLE,
        ctypes.POINTER(ProcessMemoryCounters),
        wintypes.DWORD,
    ]
    psapi.GetProcessMemoryInfo.restype = wintypes.BOOL
    if not psapi.GetProcessMemoryInfo(kernel32.GetCurrentProcess(), ctypes.byref(counters), counters.cb):
        raise ctypes.WinError(ctypes.get_last_error())
    return counters.WorkingSetSize // (1024 * 1024)


def build_translation_prompt(japanese):
    task = (
        "Translate the following text from Japanese into Chinese:\n"
        f"Japanese: {japanese}\n"
        "Chinese:"
    )
    return f"<|im_start|>user\n{task}<|im_end|>\n<|im_start|>assistant\n"


def clean_translation(decoded, simplify):
    if "</think>" in decoded:
        decoded = decoded.rsplit("</think>", 1)[-1]
    decoded = decoded.strip()
    if decoded.startswith("Chinese:"):
        decoded = decoded[len("Chinese:"):].strip()
    if not decoded:
        raise ValueError("LMT 未返回中文文本。")
    return simplify.convert(decoded)


def read_normalized_wave(path):
    with wave.open(path, "rb") as audio:
        if audio.getsampwidth() != 2 or audio.getnchannels() != 1 or audio.getframerate() != 16000:
            raise ValueError("音频必须先转换为 16 kHz、16-bit、单声道 PCM WAV。")
        frames = audio.readframes(audio.getnframes())
    return np.frombuffer(frames, dtype="<i2").astype(np.float32) / 32768.0


def translate(generator, tokenizer, simplify, japanese):
    tokens = tokenizer.encode(build_translation_prompt(japanese)).tokens
    result = generator.generate_batch(
        [tokens],
        beam_size=1,
        max_length=min(len(tokens) + 128, 512),
        end_token=["<|im_end|>", "<|endoftext|>"],
        include_prompt_in_result=False,
    )[0]
    decoded = tokenizer.decode(result.sequences_ids[0], skip_special_tokens=True)
    return clean_translation(decoded, simplify)


def process_audio(recognizer, generator, tokenizer, simplify, samples):
    duration = samples.size / 16000
    rms = float(np.sqrt(np.mean(samples * samples))) if samples.size else 0.0
    peak = float(np.max(np.abs(samples))) if samples.size else 0.0
    active_ratio = float(np.mean(np.abs(samples) > 0.008)) if samples.size else 0.0
    diagnostics = {
        "sample_rate": 16000,
        "duration_seconds": duration,
        "rms": rms,
        "peak": peak,
        "active_ratio": active_ratio,
        "language": "unreported",
    }
    response = {
        "outcome": "too_short",
        "japanese": "",
        "chinese": "",
        "asr_seconds": 0.0,
        "mt_seconds": 0.0,
        "diagnostics": diagnostics,
    }
    if samples.size < 8000:
        return response

    stream = recognizer.create_stream()
    stream.accept_waveform(16000, samples)
    started = time.perf_counter()
    recognizer.decode_stream(stream)
    result = stream.result
    text = result.text.strip()
    language = result.lang.strip().lower()
    # This sherpa Qwen3 backend can leave lang empty even for valid Japanese.
    diagnostics["language"] = (
        "unreported" if not language else
        "ja" if language in ("ja", "japanese") else "other"
    )
    response["asr_seconds"] = time.perf_counter() - started
    if not text:
        response["outcome"] = "no_speech" if rms < 0.003 or active_ratio < 0.025 else "unrecognized"
        return response
    if diagnostics["language"] == "other":
        response["outcome"] = "non_japanese"
        return response

    response["japanese"] = text
    started = time.perf_counter()
    try:
        response["chinese"] = translate(generator, tokenizer, simplify, text)
    except Exception as error:
        response["outcome"] = "error"
        response["error"] = f"本地翻译失败：{type(error).__name__}: {error}"
        response["error_type"] = type(error).__name__
        response["stage"] = "translation"
        return response
    response["mt_seconds"] = time.perf_counter() - started
    response["outcome"] = "translated"
    return response


def main():
    if len(sys.argv) != 3:
        emit({"error": "用法：local_model_worker.py <qwen3-asr-dir> <lmt-ct2-dir>"})
        return 2
    asr_dir, mt_dir = map(os.path.abspath, sys.argv[1:])
    asr_files = (
        os.path.join(asr_dir, "conv_frontend.onnx"),
        os.path.join(asr_dir, "encoder.int8.onnx"),
        os.path.join(asr_dir, "decoder.int8.onnx"),
        os.path.join(asr_dir, "tokenizer"),
    )
    mt_files = (
        os.path.join(mt_dir, "model.bin"),
        os.path.join(mt_dir, "tokenizer.json"),
        os.path.join(mt_dir, "chat_template.jinja"),
    )
    missing = [path for path in (*asr_files, *mt_files) if not os.path.exists(path)]
    if missing:
        emit({"error": "模型目录缺少必需文件：" + "; ".join(missing)})
        return 2

    try:
        emit({"status": "正在加载 sherpa-onnx Qwen3 日语识别模型…"})
        started = time.perf_counter()
        recognizer = sherpa_onnx.OfflineRecognizer.from_qwen3_asr(
            conv_frontend=asr_files[0],
            encoder=asr_files[1],
            decoder=asr_files[2],
            tokenizer=asr_files[3],
            num_threads=2,
            provider="cpu",
            max_new_tokens=128,
        )
        asr_load_seconds = time.perf_counter() - started

        emit({"status": "正在加载 CTranslate2 LMT 日→中 int8 模型…"})
        started = time.perf_counter()
        generator = ctranslate2.Generator(
            mt_dir, device="cpu", compute_type="int8", inter_threads=1, intra_threads=2
        )
        tokenizer = Tokenizer.from_file(mt_files[1])
        simplify = OpenCC("t2s")
        mt_load_seconds = time.perf_counter() - started
        emit({
            "ready": True,
            "asr_load_seconds": asr_load_seconds,
            "mt_load_seconds": mt_load_seconds,
            "working_set_mb": working_set_megabytes(),
        })
    except Exception as error:
        emit({"error": f"本地模型初始化失败：{type(error).__name__}: {error}"})
        return 1

    for line in sys.stdin:
        try:
            request = json.loads(line)
            if request.get("op") != "transcribe_translate":
                raise ValueError("未知操作。")
            audio_path = request["audio_path"]
            samples = read_normalized_wave(audio_path)
            response = process_audio(recognizer, generator, tokenizer, simplify, samples)
            response["working_set_mb"] = working_set_megabytes()
            emit(response)
        except Exception as error:
            emit({"error": f"本地识别/翻译失败：{type(error).__name__}: {error}",
                  "error_type": type(error).__name__,
                  "stage": "request_or_asr"})
    del generator, recognizer
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
