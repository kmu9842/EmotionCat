# Local Laya worker

Only **Laya multilingual 322M (0.322B)** is installed and loaded. The English 421M checkpoint and language-based routing are not used. This is a choice classifier, not a generative chat model.

    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/setup-laya.ps1 -Device auto

Windows: Python 3.10–3.13 x64; CUDA 12.8 PyTorch 2.8.0 on NVIDIA, CPU otherwise. CUDA weights use the official SDK's BF16/FP16 inference dtype. The worker warms up before announcing readiness, then releases unused conversion allocations. CPU uses FP32. The multilingual checkpoint download is about 644 MB; Python/PyTorch dependencies are additional.

    bash scripts/setup-laya.sh --model multilingual

macOS: Apple Silicon uses PyTorch 2.8.0 and automatically selects MPS when available; Intel uses the legacy PyTorch 2.2.2 runtime, NumPy 1.26.4 and Python 3.10–3.11. Actual macOS installation, execution and MPS support remain unverified on hardware. Linux's installer currently installs CPU PyTorch.

Writable runtime directories:

| Platform | Runtime |
|---|---|
| Windows | Project inference/ |
| macOS | ~/Library/Application Support/EmotionCat/inference |
| Linux | $XDG_DATA_HOME/EmotionCat/inference, or ~/.local/share/EmotionCat/inference |

--data-dir overrides this directory. Without it, EMOTIONCAT_MODEL_DIR can override the model directory. macOS bundle resources remain read-only; venv, weights and setup status live in user data.

The app owns a loopback-only worker:

    python inference/server.py --host 127.0.0.1 --port 47821 --token RANDOM_PER_RUN_SECRET --model multilingual --threads 2 --device auto

All requests require X-EmotionCat-Token. GET /health reports readiness, model, actual device, request counters, input character count, last label, inference time, and CUDA allocated/reserved/peak MiB. It never returns input text. Reserved memory includes allocated memory; CUDA context/driver memory is additional.

POST /classify accepts text, instructions, source (typing/manual/test), and emotions. Each emotion contains id, name and description. Use 2–16 unique ASCII IDs and concise descriptions. Response fields include emotion, confidence, probabilities, elapsed_ms, device, model, source, input_chars and input_tokens. Confidence is the SDK's entropy-based score, not measured accuracy. Descriptions are request-time criteria, not training examples. Input tokens and instruction/option token budgets are bounded.

No requests run while idle. Concurrent inference returns 503 rather than queuing stale input. The desktop coalesces edits to the latest text. Errors are shown; there is no hidden keyword classifier. Input is never logged or saved. Browser Origin requests are rejected, and Hugging Face is offline during inference.

Sources/licenses: [official SDK](https://github.com/NandhaKishorM/laya), [multilingual checkpoint](https://huggingface.co/convaiinnovations/laya-multilingual), [pinned SDK 0.3.5](https://pypi.org/project/laya/0.3.5/). Model revision is pinned in download_model.py and recorded beside downloaded weights. Setup does not execute model repository code.