"""Developer-only: emotion accuracy and CPU latency of ONNX variants, using the app's default labels."""
import argparse
import json
import time

import numpy as np
import onnxruntime as ort

DEFAULTS = {"neutral": "무감정", "angry": "분노", "love": "사랑", "excited": "기쁨",
            "sad": "슬픔", "surprised": "놀람", "sleepy": "졸림", "confused": "혼란"}
INSTRUCTIONS = "문장을 쓴 사람은 어떤 감정을 느끼고 있나요?"


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--tokenizer", required=True)
    p.add_argument("--cases", required=True)
    p.add_argument("onnx", nargs="+")
    a = p.parse_args()
    from transformers import PreTrainedTokenizerFast
    from laya.common import build_sequence
    tok = PreTrainedTokenizerFast(tokenizer_file=a.tokenizer, mask_token="<mask>", cls_token="<bos>",
                                  sep_token="<eos>", pad_token="<pad>")
    q = {"t": "choice", "ins": INSTRUCTIONS, "crit": {v: "" for v in DEFAULTS.values()}}
    keys = list(DEFAULTS)
    cases = json.load(open(a.cases, encoding="utf-8"))
    for path in a.onnx:
        opts = ort.SessionOptions(); opts.intra_op_num_threads = 2
        sess = ort.InferenceSession(path, opts, providers=["CPUExecutionProvider"])
        hit, times = 0, []
        for c in cases:
            ids, markers = build_sequence(tok, c["text"], q, 1024, 192)
            feed = {"input_ids": np.array([ids], np.int64), "marker_pos": np.array([markers], np.int64),
                    "marker_mask": np.ones((1, len(markers)), bool)}
            t = time.perf_counter(); out = sess.run(None, feed)[0][0]; times.append((time.perf_counter() - t) * 1000)
            hit += keys[int(out.argmax())] == c["expected"]
        print(json.dumps({"model": path.rsplit("/", 1)[-1], "accuracy": "%d/%d" % (hit, len(cases)),
                          "median_ms_2threads": round(float(np.median(times[3:])), 1)}))


if __name__ == "__main__":
    main()
