"""Developer-only: compare ONNX output against the PyTorch Laya reference."""
import argparse
import json
import sys

import numpy as np
import onnxruntime as ort
import torch

sys.path.insert(0, __file__.rsplit("/tools/", 1)[0] + "/inference")

CRITERIA = {
    "neutral": "평온: 일상적이고 차분한 사실 전달",
    "angry": "화남: 짜증, 분노, 불만",
    "love": "하트: 사랑, 애정, 고마움",
    "excited": "신남: 기쁨, 행복, 들뜸",
    "sad": "슬픔: 우울, 속상함, 외로움",
    "surprised": "놀람: 깜짝 놀람, 충격",
    "sleepy": "졸림: 피곤함, 졸음",
    "confused": "혼란: 헷갈림, 이해가 안 됨",
}
INSTRUCTIONS = "문장을 쓴 사람은 어떤 감정을 느끼고 있나요?"


def build(agent, text):
    from laya.common import build_sequence
    options = {v: "" for v in CRITERIA.values()}
    q = {"t": "choice", "ins": INSTRUCTIONS, "crit": options}
    ids, markers = build_sequence(agent.tok, text, q, agent.cfg["max_len"], 256)
    return ids, markers


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--model-dir", required=True)
    p.add_argument("--onnx", required=True)
    p.add_argument("--cases", required=True)
    a = p.parse_args()
    import laya
    agent = laya.load(a.model_dir, device="cpu")
    agent.model.float().eval()
    torch.backends.mha.set_fastpath_enabled(False)
    sess = ort.InferenceSession(a.onnx, providers=["CPUExecutionProvider"])
    cases = [c["text"] for c in json.load(open(a.cases, encoding="utf-8"))]
    cases += [" ".join(cases[:12]), " ".join(cases) * 2]  # >128 and >512 tokens
    worst, agree = 0.0, 0
    for text in cases:
        ids, markers = build(agent, text)
        i = torch.tensor([ids]); m = torch.tensor([markers]); mm = torch.ones((1, len(markers)), dtype=torch.bool)
        with torch.no_grad():
            ref, _ = agent.model(i, torch.ones_like(i), m, mm, torch.zeros(1, dtype=torch.long))
        ref = torch.softmax(ref.float(), -1).numpy()[0]
        out = sess.run(None, {"input_ids": i.numpy(), "marker_pos": m.numpy(), "marker_mask": mm.numpy()})[0]
        out = np.exp(out[0] - out[0].max()); out /= out.sum()
        worst = max(worst, float(np.abs(out - ref).max()))
        agree += int(out.argmax() == ref.argmax())
    print(json.dumps({"cases": len(cases), "argmax_agree": agree, "max_prob_diff": round(worst, 5),
                      "longest_tokens": len(build(agent, cases[-1])[0])}))


if __name__ == "__main__":
    main()
