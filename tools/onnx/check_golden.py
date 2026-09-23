"""Developer/CI diagnostic: run the golden inputs through the official Python ONNX Runtime."""
import json
import sys

import numpy as np
import onnxruntime as ort

golden, model = sys.argv[1], sys.argv[2]
print("onnxruntime", ort.__version__)
cases = json.load(open(golden, encoding="utf-8"))
session = ort.InferenceSession(model, providers=["CPUExecutionProvider"])
agree, worst = 0, 0.0
for c in cases:
    k = len(c["marker_pos"])
    logits = session.run(None, {"input_ids": np.array([c["input_ids"]], np.int64),
                                "marker_pos": np.array([c["marker_pos"]], np.int64),
                                "marker_mask": np.ones((1, k), bool)})[0][0]
    p = np.exp(logits - logits.max()); p /= p.sum()
    agree += int(p.argmax() == int(np.argmax(c["probabilities"])))
    worst = max(worst, float(np.abs(p - np.array(c["probabilities"])).max()))
print("python-ort agree %d/%d maxdiff %.3f" % (agree, len(cases), worst))
