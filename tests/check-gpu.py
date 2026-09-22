"""Synthetic-only authenticated GPU probe; never reads a user's typing."""
import json
from pathlib import Path
import os
import time
import urllib.request

settings = json.loads((Path(os.environ["LOCALAPPDATA"]) / "EmotionCat" / "settings.json").read_text(encoding="utf-8-sig"))
emotions = [{"id": x["Id"], "name": x["Name"], "description": x["Description"]} for x in settings["Emotions"]]
samples = [
    ("EN neutral", "The meeting starts at three o'clock tomorrow."),
    ("EN angry", "I am furious! This is so unfair and annoying!"),
    ("EN love", "I love you so much, you mean everything to me."),
    ("EN excited", "Yay! This is amazing! I am so excited!"),
    ("EN sad", "I feel so sad and lonely. I want to cry."),
    ("KO neutral", "내일 회의는 오후 세 시에 시작해요."),
    ("KO angry", "아 진짜 짜증나고 화나네! 너무 열받아!"),
    ("KO love", "너무 사랑해! 정말 좋아하고 아껴!"),
    ("KO excited", "와 신난다! 너무 행복하고 기뻐!"),
    ("KO sad", "너무 슬프고 외로워. 눈물이 나."),
]
rows = []
for name, text in samples:
    body = json.dumps({"text": text, "emotions": emotions, "source": "test"}).encode("utf-8")
    request = urllib.request.Request("http://127.0.0.1:47829/classify", data=body, headers={"Content-Type": "application/json", "X-EmotionCat-Token": "emotioncat-local-gpu-test-token-2026"})
    started = time.perf_counter()
    with urllib.request.urlopen(request, timeout=30) as response:
        result = json.load(response)
    row = {"sample": name, "emotion": result["emotion"], "confidence": result["confidence"], "model_ms": result["elapsed_ms"], "roundtrip_ms": round((time.perf_counter() - started) * 1000, 1), "device": result["device"]}
    rows.append(row)
    print(json.dumps(row), flush=True)
Path("output/verification/gpu-smoke.json").write_text(json.dumps(rows, indent=2), encoding="utf-8")
