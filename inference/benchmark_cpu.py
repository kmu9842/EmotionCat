"""Synthetic local inference benchmark. Never reads captured user input or app state."""
import argparse
import json
from pathlib import Path
import re
import statistics
import time

from server import EmotionWorker

SAMPLES = {
    "english": [
        ("angry", "I am so angry and furious. This is unacceptable!"),
        ("love", "I love you so much, my darling! Sending you hugs and kisses."),
        ("excited", "Yay! I won! This is amazing, I am so happy and excited!"),
        ("sad", "I am crying. I feel so lonely and sad. I miss my friend so much."),
    ],
    "multilingual": [
        ("angry", "진짜 너무 화나고 짜증나. 왜 자꾸 이런 일이 생기는 거야!"),
        ("love", "너를 정말 사랑해. 보고 싶고 꼭 안아주고 싶어."),
        ("excited", "야호 드디어 성공했다! 너무 기쁘고 신나! 최고야!"),
        ("sad", "너무 슬프고 외로워. 눈물이 나고 아무것도 하고 싶지 않아."),
    ],
}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", choices=SAMPLES, default="english")
    parser.add_argument("--threads", type=int, nargs="+", default=[2, 4, 8])
    parser.add_argument("--inference-mode", action="store_true")
    args = parser.parse_args()
    # The checked-in native defaults, not the user's saved configuration.
    source = (Path(__file__).resolve().parent.parent / "src" / "AppSettings.cs").read_text(encoding="utf-8-sig")
    criteria = dict(re.findall(r'new EmotionDefinition\("([a-z]+)", "[^"]+", "([^"]+)"\)', source))
    if len(criteria) != 8:
        raise RuntimeError("Expected the eight shipped default emotions")
    worker = EmotionWorker(args.model, 2)
    start = time.perf_counter()
    worker.load()
    if worker.status != "ready":
        raise RuntimeError(worker.error)
    import torch
    print(json.dumps({"load_seconds": round(time.perf_counter() - start, 2), "torch": torch.__version__, "mkldnn": torch.backends.mkldnn.enabled, "model": args.model}), flush=True)
    lengths = []
    def shapes(_module, positional, keyword):
        lengths.append(keyword["input_ids"].shape[-1])
    handle = worker.agent.model.encoder.register_forward_pre_hook(shapes, with_kwargs=True)
    for threads in args.threads:
        torch.set_num_threads(threads)
        started = time.perf_counter()
        with torch.inference_mode(args.inference_mode):
            first = worker.classify(SAMPLES[args.model][0][1], criteria)
            results = []
            for expected, text in SAMPLES[args.model]:
                result = worker.classify(text, criteria)
                results.append({"expected": expected, "emotion": result["emotion"], "ms": result["elapsed_ms"], "confidence": result["confidence"]})
        print(json.dumps({"threads": threads, "inference_mode": args.inference_mode, "first_ms": first["elapsed_ms"], "median_ms": statistics.median(r["ms"] for r in results), "range_ms": [min(r["ms"] for r in results), max(r["ms"] for r in results)], "tokens": lengths[-5:], "results": results}), flush=True)
    handle.remove()


if __name__ == "__main__":
    main()
