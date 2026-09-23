"""Developer-only: export the Laya multilingual checkpoint to ONNX for native inference.

Users never run Python. This produces laya.onnx (fp32) that quantize_model.py shrinks to int8.
"""
import argparse
import sys

import torch


class Wrapper(torch.nn.Module):
    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, input_ids, marker_pos, marker_mask):
        attention_mask = torch.ones_like(input_ids)
        qtype = torch.zeros(input_ids.shape[0], dtype=torch.long)  # "choice" questions only
        logits, act = self.model(input_ids, attention_mask, marker_pos, marker_mask, qtype)
        return logits


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model-dir", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()
    import laya
    agent = laya.load(args.model_dir, device="cpu")
    model = agent.model.float().eval()
    model.encoder.config._attn_implementation = "eager"
    model.encoder.config.reference_compile = False
    torch.backends.mha.set_fastpath_enabled(False)  # fused encoder-layer op has no ONNX export
    wrapper = Wrapper(model).eval()
    ids = torch.tensor([[2] + list(range(1000, 1060)) + [1]], dtype=torch.long)
    mpos = torch.tensor([[5, 10, 15]], dtype=torch.long)
    mmask = torch.ones((1, 3), dtype=torch.bool)
    with torch.no_grad():
        tokens = torch.export.Dim("tokens", min=8, max=1024)
        options = torch.export.Dim("options", min=2, max=16)
        program = torch.onnx.export(
            wrapper, (ids, mpos, mmask), dynamo=True, opset_version=18,
            input_names=["input_ids", "marker_pos", "marker_mask"], output_names=["logits"],
            dynamic_shapes={"input_ids": {1: tokens}, "marker_pos": {1: options}, "marker_mask": {1: options}},
        )
        program.optimize()
        program.save(args.out, external_data=False)
    print("exported", args.out, file=sys.stderr)


if __name__ == "__main__":
    main()
