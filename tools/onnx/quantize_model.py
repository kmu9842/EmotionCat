"""Developer-only: int8 weight quantization for CPU inference (MatMul and embedding Gather)."""
import argparse

from onnxruntime.quantization import QuantType, quantize_dynamic

p = argparse.ArgumentParser()
p.add_argument("--src", required=True)
p.add_argument("--out", required=True)
a = p.parse_args()
quantize_dynamic(a.src, a.out, weight_type=QuantType.QInt8, per_channel=False,  # per-channel int8 collapses on AMD Zen4 (3/40); per-tensor keeps fp32 accuracy
                 op_types_to_quantize=["MatMul", "Gather"],
                 extra_options={"MatMulConstBOnly": True})
