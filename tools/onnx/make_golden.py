"""Developer-only: golden cases the native C#/Swift engines must reproduce exactly.

Native sequence rules (mirror of the former Python worker + laya.build_sequence, choice questions):
  labels[i]  = (description.strip() or name.strip() or id)[:160], duplicates get " (id)" appended
  head_len   = min(1024-128, max(192, len(tok(ins)) + sum(min(49, 1+len(tok(label)))) + 12))
  head       = tok("choice question: " + ins)                     ("<mask>" -> " " in every text)
  option[i]  = [MASK] + tok(" " + label)[:48]
  budget     = head_len - sum(len(option)); if budget < 16: per = max(4, (head_len-16)//n), cut each option to per, recompute
  ids        = [CLS] + head[:max(8, budget)] + [SEP] + options... + [SEP]; marker = index of each [MASK]
  state      = tok(text)[-(1024-head_len-8):][:max(0, 1024-len(ids)-1)]
  ids       += state + [SEP]
  probabilities = softmax(logits) of the fp32 model, for reference only: int8 kernels differ slightly per CPU
Native tests require exact input_ids/marker_pos for every case and >= 80% accuracy on cases with "expected"
(fp32 and the shipped int8 model both score 36/40 on arm64, x86_64/Rosetta, AMD Zen4 and Zen5).
"""
import argparse
import json
import sys

import numpy as np
import onnxruntime as ort

sys.path.insert(0, __file__.rsplit("/", 1)[0])
from tokenizer_pack import Reference, load  # noqa: E402

CLS, SEP, MASK, MAX_LEN = 2, 1, 4, 1024
DEFAULTS = [("neutral", "평온", "무감정"), ("angry", "화남", "분노"), ("love", "하트", "사랑"),
            ("excited", "신남", "기쁨"), ("sad", "슬픔", "슬픔"), ("surprised", "놀람", "놀람"),
            ("sleepy", "졸림", "졸림"), ("confused", "혼란", "혼란")]
INSTRUCTIONS = "문장을 쓴 사람은 어떤 감정을 느끼고 있나요?"


def labels(emotions):
    out, seen = [], set()
    for e in emotions:
        label = ((e["description"].strip() or e["name"].strip() or e["id"]))[:160]
        if label in seen:
            label += " (" + e["id"] + ")"
        while label in seen:
            label += " "
        seen.add(label); out.append(label)
    return out


def build(tok, text, emotions, ins):
    clean = lambda s: s.replace("<mask>", " ")
    labs = labels(emotions)
    head_len = min(MAX_LEN - 128, max(192, len(tok.encode(ins)) + sum(min(49, 1 + len(tok.encode(l))) for l in labs) + 12))
    head = tok.encode(clean("choice question: " + ins))
    opts = [[MASK] + tok.encode(" " + clean(l))[:48] for l in labs]
    budget = head_len - sum(len(o) for o in opts)
    if budget < 16:
        per = max(4, (head_len - 16) // len(opts))
        opts = [o[:per] for o in opts]
        budget = head_len - sum(len(o) for o in opts)
    ids = [CLS] + head[:max(8, budget)] + [SEP]
    markers = []
    for o in opts:
        markers.append(len(ids)); ids.extend(o)
    ids.append(SEP)
    state = tok.encode(clean(text))
    keep = MAX_LEN - head_len - 8
    state = state[-keep:] if keep > 0 else []
    state = state[:max(0, MAX_LEN - len(ids) - 1)]
    ids = (ids + state + [SEP])[:MAX_LEN]
    return ids, [m for m in markers if m < MAX_LEN]


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--tokenizer-json", required=True)
    p.add_argument("--onnx", required=True)
    p.add_argument("--cases", required=True)
    p.add_argument("--out", required=True)
    a = p.parse_args()
    tok = Reference(*load(a.tokenizer_json))
    sess = ort.InferenceSession(a.onnx, providers=["CPUExecutionProvider"])
    defaults = [{"id": i, "name": n, "description": d} for i, n, d in DEFAULTS]
    custom = defaults[:3] + [{"id": "custom_1", "name": "배고픔", "description": "배고픔, 먹고 싶음"},
                             {"id": "custom_2", "name": "", "description": ""},
                             {"id": "dup", "name": "x", "description": "분노"}]
    labeled = json.load(open(a.cases, encoding="utf-8"))
    runs = [(c["text"], defaults, c["expected"]) for c in labeled]  # accuracy cases (app default labels)
    runs += [("오늘 점심 뭐 먹지? 배고파", custom, None), ("hello world 😀", defaults, None),
             ("Hello,   World!!\n줄바꿈\t탭 <mask>", defaults, None), ("ㅋㅋㅋㅋ " * 400, defaults, None)]
    out = []
    for text, emotions, expected in runs:
        ids, markers = build(tok, text, emotions, INSTRUCTIONS)
        logits = sess.run(None, {"input_ids": np.array([ids], np.int64), "marker_pos": np.array([markers], np.int64),
                                 "marker_mask": np.ones((1, len(markers)), bool)})[0][0]
        prob = np.exp(logits - logits.max()); prob /= prob.sum()
        case = {"text": text, "instructions": INSTRUCTIONS, "emotions": emotions, "input_ids": ids,
                "marker_pos": markers, "probabilities": [round(float(x), 6) for x in prob],
                "emotion": emotions[int(prob.argmax())]["id"]}
        if expected:
            case["expected"] = expected
        out.append(case)
    json.dump(out, open(a.out, "w", encoding="utf-8"), ensure_ascii=False, indent=0)
    print("golden cases", len(out))


if __name__ == "__main__":
    main()
