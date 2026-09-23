"""Developer-only: compare int8 quantization variants on the current CPU against fp32."""
import json
import os
import sys
import time

import numpy as np
import onnxruntime as ort
from onnxruntime.quantization import QuantType, quantize_dynamic

src, inputs_path, out_dir = sys.argv[1], sys.argv[2], sys.argv[3]
cases = json.load(open(inputs_path))
VARIANTS = {
    "A_s8_pc_matmul_gather": dict(weight_type=QuantType.QInt8, per_channel=True, ops=["MatMul", "Gather"]),
    "B_s8_pc_matmul": dict(weight_type=QuantType.QInt8, per_channel=True, ops=["MatMul"]),
    "C_s8_pc_rr_matmul_gather": dict(weight_type=QuantType.QInt8, per_channel=True, reduce_range=True, ops=["MatMul", "Gather"]),
    "D_u8_matmul_gather": dict(weight_type=QuantType.QUInt8, per_channel=False, ops=["MatMul", "Gather"]),
    "E_s8_matmul_gather": dict(weight_type=QuantType.QInt8, per_channel=False, ops=["MatMul", "Gather"]),
}


def evaluate(path):
    opts = ort.SessionOptions(); opts.intra_op_num_threads = 2
    s = ort.InferenceSession(path, opts, providers=["CPUExecutionProvider"])
    preds, times = [], []
    for c in cases:
        k = len(c["marker_pos"])
        feed = {"input_ids": np.array([c["input_ids"]], np.int64), "marker_pos": np.array([c["marker_pos"]], np.int64),
                "marker_mask": np.ones((1, k), bool)}
        t = time.perf_counter(); o = s.run(None, feed)[0][0]; times.append((time.perf_counter() - t) * 1000)
        p = np.exp(o - o.max()); preds.append(p / p.sum())
    return preds, float(np.median(times[3:]))


ref, ref_ms = evaluate(src)
acc = sum(int(p.argmax() == c["expected"]) for p, c in zip(ref, cases))
print(json.dumps({"variant": "fp32", "accuracy": acc, "ms": round(ref_ms, 1), "mb": os.path.getsize(src) >> 20}), flush=True)
only = sys.argv[4:] or list(VARIANTS)
for name in only:
    v = VARIANTS[name]
    out = os.path.join(out_dir, name + ".onnx")
    quantize_dynamic(src, out, weight_type=v["weight_type"], per_channel=v["per_channel"], reduce_range=v.get("reduce_range", False),
                     op_types_to_quantize=v["ops"], extra_options={"MatMulConstBOnly": True})
    preds, ms = evaluate(out)
    print(json.dumps({"variant": name, "accuracy": sum(int(p.argmax() == c["expected"]) for p, c in zip(preds, cases)),
                      "agree_fp32": sum(int(p.argmax() == r.argmax()) for p, r in zip(preds, ref)),
                      "max_diff": round(max(float(np.abs(p - r).max()) for p, r in zip(preds, ref)), 3),
                      "ms": round(ms, 1), "mb": os.path.getsize(out) >> 20}), flush=True)
