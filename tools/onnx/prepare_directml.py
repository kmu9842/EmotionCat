"""Build-time only: prepare the FP32 Laya export for strict DirectML inference.

Fixed shapes let ORT fold shape arithmetic instead of falling back to CPU kernels.
Padding is masked in both the encoder and the choice head; no padding token can
participate in attention. End users run only the native app and bundled DLLs.
"""
import argparse
import hashlib

import onnx
from onnx import TensorProto, helper
from onnxconverter_common import float16

SOURCE_SHA256 = "64d49d6850ae48c8c5065291055ba3f2cb4eb72f4caa3fb5115c2141ea8f9db0"
TOKENS, OPTIONS = 1024, 16


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--src", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()
    with open(args.src, "rb") as source:
        digest = hashlib.file_digest(source, "sha256").hexdigest()
    if digest != SOURCE_SHA256:
        raise ValueError("Unexpected FP32 source model; review the graph before conversion.")
    model = onnx.load(args.src)
    ones = [n for n in model.graph.node if n.name == "node_ones_like" and n.op_type == "Expand"]
    if len(ones) != 1 or list(ones[0].output) != ["ones_like"]:
        raise ValueError("Encoder attention-mask construction changed.")
    model.graph.node.remove(ones[0])
    for node in model.graph.node:
        for i, name in enumerate(node.input):
            if name == "ones_like":
                node.input[i] = "attention_mask"
    model.graph.input.append(helper.make_tensor_value_info("attention_mask", TensorProto.INT64, [1, TOKENS]))
    for value in list(model.graph.input) + list(model.graph.output) + list(model.graph.value_info):
        for dim in value.type.tensor_type.shape.dim:
            if dim.dim_param in ("tokens", "options"):
                dim.dim_value = TOKENS if dim.dim_param == "tokens" else OPTIONS
    model = float16.convert_float_to_float16(model, keep_io_types=True, min_positive_val=5.960464477539063e-08)
    types = {v.name: v.type.tensor_type.elem_type for v in list(model.graph.value_info) + list(model.graph.output)}
    for node in model.graph.node:
        # The converter leaves the original integer-to-float rotary-position casts
        # at FP32 even after changing their output type annotations to FP16.
        if node.op_type == "Cast" and types.get(node.output[0]) == TensorProto.FLOAT16:
            for attr in node.attribute:
                if attr.name == "to" and attr.i == TensorProto.FLOAT:
                    attr.i = TensorProto.FLOAT16
        # This export has no empty dimensions. DirectML rejects allowzero=1 with
        # inferred (-1) dimensions; allowzero=0 is equivalent for these shapes.
        if node.op_type == "Reshape":
            for attr in node.attribute:
                if attr.name == "allowzero":
                    attr.i = 0
    onnx.checker.check_model(model)
    onnx.save(model, args.out)
    print(args.out, flush=True)


if __name__ == "__main__":
    main()
