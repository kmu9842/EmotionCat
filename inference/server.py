"""Private, loopback-only Laya emotion worker. No input text is logged or saved."""
import argparse
import hmac
import json
import os
import re
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

sys.dont_write_bytecode = True  # A signed app bundle must never acquire __pycache__.
from runtime_paths import model_directory

# Inference cannot contact Hugging Face or download anything. Setup is separate.
os.environ["USE_TF"] = "0"
os.environ["USE_FLAX"] = "0"
os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"
os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
os.environ["TOKENIZERS_PARALLELISM"] = "false"
MODEL_IDS = {"multilingual": "convaiinnovations/laya-multilingual"}
DEFAULT_INSTRUCTIONS = "문장을 쓴 사람은 어떤 감정을 느끼고 있나요?"


def model_options(criteria):
    """Present the configured meanings, not sprite IDs, as model choices."""
    options, reverse = {}, {}
    for key, description in criteria.items():
        label = description.strip() or key
        if label in reverse:
            label = label + " (" + key + ")"
        while label in reverse:
            label += " "
        options[label] = ""
        reverse[label] = key
    return options, reverse


def validate_request(body):
    if not isinstance(body, dict):
        raise ValueError("Expected a JSON object.")
    text = body.get("text")
    emotions = body.get("emotions")
    if not isinstance(text, str) or not text.strip() or len(text) > 1000:
        raise ValueError("text must contain 1 to 1000 characters.")
    if not isinstance(emotions, list) or not 2 <= len(emotions) <= 16:
        raise ValueError("Provide 2 to 16 emotions.")
    criteria = {}
    for item in emotions:
        if not isinstance(item, dict):
            raise ValueError("Each emotion must be an object.")
        key, name, description = item.get("id"), item.get("name", ""), item.get("description", "")
        if not isinstance(key, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", key):
            raise ValueError("Emotion IDs must be 1-64 ASCII letters, numbers, underscores or hyphens.")
        if key in criteria:
            raise ValueError("Emotion IDs must be unique.")
        if not isinstance(name, str) or len(name) > 80 or not isinstance(description, str) or len(description) > 400:
            raise ValueError("Emotion name or description is too long.")
        criteria[key] = (description.strip() or name.strip() or key)[:160]
    instructions = body.get("instructions", DEFAULT_INSTRUCTIONS)
    if not isinstance(instructions, str) or not instructions.strip() or len(instructions) > 1000:
        raise ValueError("instructions must contain 1 to 1000 characters.")
    source = body.get("source", "manual")
    if source not in ("typing", "manual", "test"):
        raise ValueError("Unknown input source.")
    return text, criteria, instructions.strip(), source


class EmotionWorker:
    def __init__(self, model, threads, data_dir=None, device="auto"):
        self.model_name = model
        self.threads = threads
        self.model_root = model_directory(data_dir)
        self.requested_device = device
        self.device = "loading"
        self.device_name = ""
        self.torch_version = ""
        self.status = "loading"
        self.error = ""
        self.agent = None
        self.lock = threading.Lock()
        self.requests = 0
        self.successes = 0
        self.last_elapsed_ms = 0.0
        self.last_input_chars = 0
        self.last_emotion = ""
        self.last_confidence = 0.0
        self.last_source = ""

    def health(self):
        memory = {}
        if self.device.startswith("cuda"):
            import torch
            memory = {"gpu_allocated_mb": round(torch.cuda.memory_allocated() / 1048576, 1),
                      "gpu_reserved_mb": round(torch.cuda.memory_reserved() / 1048576, 1),
                      "gpu_peak_mb": round(torch.cuda.max_memory_allocated() / 1048576, 1)}
        return {"status": self.status, "model": self.model_name,
                "model_id": MODEL_IDS[self.model_name], "error": self.error,
                "device": self.device, "device_name": self.device_name,
                "torch_version": self.torch_version, "requests": self.requests,
                "successes": self.successes, "last_elapsed_ms": self.last_elapsed_ms,
                "last_input_chars": self.last_input_chars, "last_emotion": self.last_emotion,
                "last_confidence": self.last_confidence, "last_source": self.last_source, **memory}

    def load(self):
        try:
            path = self.model_root / self.model_name
            if not (path / "emotioncat-model.json").is_file():
                raise FileNotFoundError("Model is not installed at " + str(path) + ". Use Install Laya in settings.")
            import torch
            self.torch_version = torch.__version__
            torch.set_num_threads(self.threads)
            if torch.get_num_interop_threads() != 1:
                torch.set_num_interop_threads(1)
            device = self.requested_device
            if device == "auto":
                device = "cuda" if torch.cuda.is_available() else "mps" if hasattr(torch.backends, "mps") and torch.backends.mps.is_available() else "cpu"
            if device == "cuda" and not torch.cuda.is_available():
                raise RuntimeError("CUDA is unavailable. Install the CUDA runtime using scripts/setup-laya.ps1 -Device cuda.")
            if device == "mps" and not (hasattr(torch.backends, "mps") and torch.backends.mps.is_available()):
                raise RuntimeError("Apple MPS is unavailable on this Python/PyTorch installation.")
            import laya
            self.agent = laya.load(str(path), device=device)
            if device == "cuda" and self.agent.device.type != "cuda":
                raise RuntimeError("Laya could not use CUDA. CPU fallback was rejected; check the GPU runtime.")
            self.device = str(self.agent.device)
            self.device_name = torch.cuda.get_device_name(self.agent.device) if self.agent.device.type == "cuda" else self.device.upper()
            # CUDA inference already runs under the SDK's BF16/FP16 autocast.
            # Keep weights in that same dtype instead of retaining an FP32 copy.
            if self.agent.device.type == "cuda":
                self.agent.model.to(dtype=self.agent.dtype)
            # Initialize CUDA kernels before reporting ready, without using any user text.
            if self.agent.device.type == "cuda":
                self.agent.predict("This is a neutral test sentence.", {"emotion": {
                    "type": "choice", "instructions": "Which emotion is expressed?",
                    "criteria": {"neutral": "ordinary factual text", "happy": "joy", "sad": "sadness"},
                }})
                torch.cuda.synchronize()
                torch.cuda.empty_cache()
            self.status = "ready"
            print("LAYA_READY " + self.model_name + " " + self.device + " " + self.device_name, flush=True)
        except Exception as exc:
            self.error = "Model initialization failed: " + str(exc)[:600]
            self.status = "error"
            print(self.error, flush=True)

    def classify(self, text, criteria, instructions=DEFAULT_INSTRUCTIONS, source="manual"):
        start = time.perf_counter()
        options, option_ids = model_options(criteria)
        # Preserve enough option text when users create more labels; leave state room.
        max_len = self.agent.cfg.get("max_len", 512)
        instruction_tokens = len(self.agent.tok(instructions, add_special_tokens=False)["input_ids"])
        option_tokens = sum(min(49, 1 + len(self.agent.tok(label, add_special_tokens=False)["input_ids"])) for label in options)
        head_len = min(max_len - 128, max(192, instruction_tokens + option_tokens + 12))
        self.agent.cfg["head_max_len"] = head_len
        # Keep the most recent tokens, because this is a typing companion.
        state_tokens = self.agent.tok(text, add_special_tokens=False)["input_ids"]
        recent_text = self.agent.tok.decode(state_tokens[-(max_len - head_len - 8):], skip_special_tokens=True)
        answer = self.agent.predict(recent_text, {"emotion": {
            "type": "choice",
            "instructions": instructions,
            "criteria": options,
        }})["answers"]["emotion"]
        if answer["choice"] not in option_ids:
            raise RuntimeError("Unexpected model label.")
        selected = option_ids[answer["choice"]]
        probabilities = {option_ids[label]: score for label, score in answer["probabilities"].items()}
        self.device = str(self.agent.device)
        if self.requested_device == "cuda" and self.agent.device.type != "cuda":
            raise RuntimeError("CUDA inference fell back to CPU.")
        self.successes += 1
        self.last_elapsed_ms = round((time.perf_counter() - start) * 1000, 1)
        self.last_input_chars = len(text)
        self.last_emotion = selected
        self.last_confidence = float(answer["confidence"])
        self.last_source = source
        self.error = ""
        return {"emotion": selected, "confidence": answer["confidence"],
                "probabilities": probabilities,
                "elapsed_ms": self.last_elapsed_ms,
                "model": self.model_name, "device": self.device, "source": source,
                "input_chars": len(text), "input_tokens": len(state_tokens)}


class LocalServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = False


def make_handler(worker, token):
    class Handler(BaseHTTPRequestHandler):
        server_version = "EmotionCat"

        def setup(self):
            super().setup()
            self.connection.settimeout(10)

        def log_message(self, *_args):
            pass  # Never log request URLs, bodies, headers or typed text.

        def reply(self, status, body):
            data = json.dumps(body, ensure_ascii=False).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.send_header("Connection", "close")
            self.end_headers()
            try:
                self.wfile.write(data)
            except (BrokenPipeError, ConnectionResetError):
                pass

        def authorized(self):
            supplied = self.headers.get("X-EmotionCat-Token", "")
            if self.headers.get("Origin") or not hmac.compare_digest(supplied.encode("utf-8"), token.encode("utf-8")):
                self.reply(403, {"error": "Forbidden"})
                return False
            return True

        def do_GET(self):
            if self.authorized():
                if self.path == "/health":
                    self.reply(200, worker.health())
                else:
                    self.reply(404, {"error": "Not found"})

        def do_POST(self):
            if not self.authorized():
                return
            if self.path != "/classify":
                self.reply(404, {"error": "Not found"})
                return
            if self.headers.get("Content-Type", "").split(";", 1)[0].strip().lower() != "application/json":
                self.reply(415, {"error": "Use application/json"})
                return
            try:
                length = int(self.headers.get("Content-Length", "0"))
                if not 0 < length <= 32768:
                    self.reply(413, {"error": "Request too large or empty"})
                    return
                text, criteria, instructions, source = validate_request(json.loads(self.rfile.read(length).decode("utf-8")))
            except (ValueError, UnicodeDecodeError) as exc:
                self.reply(400, {"error": "Invalid request: " + str(exc)[:160]})
                return
            worker.requests += 1
            if worker.status != "ready":
                self.reply(503, worker.health())
                return
            if not worker.lock.acquire(blocking=False):
                self.reply(503, {"error": "Model busy; retry with latest text."})
                return
            try:
                self.reply(200, worker.classify(text, criteria, instructions, source))
            except Exception:
                # Model exceptions can include caller values, so expose no exception text.
                worker.error = "Local model inference failed."
                self.reply(500, {"error": "Local model inference failed."})
            finally:
                worker.lock.release()
    return Handler


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", choices=["127.0.0.1"], default="127.0.0.1")
    parser.add_argument("--port", type=int, default=47821)
    parser.add_argument("--token", required=True)
    parser.add_argument("--model", choices=["multilingual"], default="multilingual")
    parser.add_argument("--threads", type=int, default=2)
    parser.add_argument("--device", choices=["auto", "cuda", "cpu", "mps"], default="auto",
                        help="auto uses CUDA when available, otherwise CPU; MPS is explicit/experimental")
    parser.add_argument("--data-dir", help="Writable inference directory containing models/; never modifies app resources")
    args = parser.parse_args()
    if len(args.token) < 24:
        parser.error("--token must contain at least 24 random characters")
    if not 1 <= args.threads <= 8 or not 1024 <= args.port <= 65535:
        parser.error("--threads must be 1-8; --port must be 1024-65535")
    worker = EmotionWorker("multilingual", args.threads, args.data_dir, args.device)
    server = LocalServer((args.host, args.port), make_handler(worker, args.token))
    threading.Thread(target=worker.load, daemon=True).start()
    try:
        server.serve_forever(poll_interval=0.5)
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
