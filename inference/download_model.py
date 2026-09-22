"""Download only the selected, revision-pinned Laya checkpoint (no typed input)."""
import argparse
import json
import os
import sys

sys.dont_write_bytecode = True
from runtime_paths import model_directory

os.environ.setdefault("HF_HUB_DISABLE_TELEMETRY", "1")
os.environ.setdefault("HF_HUB_DISABLE_XET", "1")

MODELS = {
    "multilingual": ("convaiinnovations/laya-multilingual", "052592a15d198d9ad47da779604259b10b47b7aa"),
}


def download(model, data_dir=None):
    from huggingface_hub import snapshot_download
    repo, revision = MODELS[model]
    target = model_directory(data_dir) / model
    print("Downloading " + repo + " (about " + ("843 MB" if model == "english" else "644 MB") + ")", flush=True)
    snapshot_download(repo_id=repo, revision=revision, local_dir=str(target),
                      allow_patterns=["rl_agent_config.json", "model.safetensors", "tokenizer/*", "encoder/config.json", "README.md", "LICENSE*"],
                      max_workers=3)
    required = ["rl_agent_config.json", "model.safetensors", "tokenizer/tokenizer.json", "encoder/config.json"]
    if not all((target / item).is_file() for item in required):
        raise RuntimeError("Model download is incomplete. Run setup again to resume.")
    (target / "emotioncat-model.json").write_text(json.dumps({"model": model, "repo": repo, "revision": revision}, indent=2), encoding="utf-8")
    print("MODEL_READY " + str(target), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", choices=MODELS, default="multilingual")
    parser.add_argument("--data-dir", help="Writable inference directory; selected model goes in models/")
    args = parser.parse_args()
    download(args.model, args.data_dir)
