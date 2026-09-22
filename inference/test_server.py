"""Protocol/privacy checks; --smoke MODEL additionally runs the real local model."""
import argparse
import contextlib
import http.client
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import threading
import time
import tempfile
import types
import unittest
from unittest import mock

from server import EmotionWorker, LocalServer, make_handler, validate_request, model_options
from runtime_paths import RESOURCE_ROOT, data_directory, model_directory

TOKEN = "test-only-token-01234567890123456789"
EMOTIONS = [
    {"id": "neutral", "name": "기본", "description": "neutral, ordinary factual text without emotion"},
    {"id": "angry", "name": "화남", "description": "anger, frustration, irritation, fury"},
    {"id": "heart", "name": "하트", "description": "love, affection, adoration"},
    {"id": "excited", "name": "신남", "description": "joy, excitement, happiness, celebration"},
    {"id": "sad", "name": "슬픔", "description": "sadness, grief, disappointment, loneliness"},
]


def request(port, method, path, body=None, token=TOKEN, origin=None):
    conn = http.client.HTTPConnection("127.0.0.1", port, timeout=30)
    headers = {"X-EmotionCat-Token": token}
    if body is not None:
        headers["Content-Type"] = "application/json; charset=utf-8"
        body = json.dumps(body, ensure_ascii=False).encode("utf-8")
    if origin:
        headers["Origin"] = origin
    conn.request(method, path, body=body, headers=headers)
    response = conn.getresponse()
    data = json.loads(response.read().decode("utf-8"))
    status = response.status
    conn.close()
    return status, data


class RuntimePathTests(unittest.TestCase):
    def test_platform_defaults_never_put_mac_models_in_bundle(self):
        user = Path(tempfile.gettempdir()) / "EmotionCat path test user"
        self.assertEqual(data_directory(platform="win32", environ={}, user_home=user), RESOURCE_ROOT)
        self.assertEqual(data_directory(platform="darwin", environ={}, user_home=user), user / "Library" / "Application Support" / "EmotionCat" / "inference")
        self.assertEqual(data_directory(platform="linux", environ={}, user_home=user), user / ".local" / "share" / "EmotionCat" / "inference")
        self.assertEqual(data_directory(platform="linux", environ={"XDG_DATA_HOME": str(user / "data")}, user_home=user), user / "data" / "EmotionCat" / "inference")

    def test_explicit_directory_precedes_environment_model_root(self):
        with tempfile.TemporaryDirectory(prefix="emotioncat-path-") as temp:
            explicit = Path(temp) / "Application Support" / "inference"
            environment = Path(temp) / "model cache"
            env = {"EMOTIONCAT_MODEL_DIR": str(environment)}
            self.assertEqual(model_directory(environ=env), environment.resolve())
            self.assertEqual(model_directory(str(explicit), environ=env), explicit.resolve() / "models")
            with mock.patch.dict(os.environ, env):
                self.assertEqual(EmotionWorker("multilingual", 2).model_root, environment.resolve())
                self.assertEqual(EmotionWorker("multilingual", 2, str(explicit)).model_root, explicit.resolve() / "models")

    def test_download_and_worker_share_external_model_path(self):
        import download_model
        with tempfile.TemporaryDirectory(prefix="emotioncat-model-") as temp:
            explicit = Path(temp) / "read write data"
            def fake_snapshot(**kwargs):
                target = Path(kwargs["local_dir"])
                self.assertEqual(target, explicit / "models" / "multilingual")
                for name in ["rl_agent_config.json", "model.safetensors", "tokenizer/tokenizer.json", "encoder/config.json"]:
                    file = target / name
                    file.parent.mkdir(parents=True, exist_ok=True)
                    file.write_text("test fixture", encoding="utf-8")
            with mock.patch.dict(sys.modules, {"huggingface_hub": types.SimpleNamespace(snapshot_download=fake_snapshot)}):
                with contextlib.redirect_stdout(io.StringIO()):
                    download_model.download("multilingual", str(explicit))
            worker = EmotionWorker("multilingual", 2, str(explicit))
            manifest = json.loads((worker.model_root / "multilingual" / "emotioncat-model.json").read_text(encoding="utf-8"))
            self.assertEqual(manifest["revision"], download_model.MODELS["multilingual"][1])


class ProtocolTests(unittest.TestCase):
    def setUp(self):
        self.worker = EmotionWorker("multilingual", 2)
        self.worker.status = "ready"
        self.worker.classify = lambda text, criteria, instructions, source: {"emotion": "heart", "confidence": 0.9}
        self.server = LocalServer(("127.0.0.1", 0), make_handler(self.worker, TOKEN))
        self.port = self.server.server_address[1]
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()

    def test_auth_and_browser_origin_are_rejected(self):
        self.assertEqual(request(self.port, "GET", "/health", token="wrong")[0], 403)
        self.assertEqual(request(self.port, "GET", "/health", origin="http://example.test")[0], 403)
        self.assertEqual(request(self.port, "GET", "/health")[1]["status"], "ready")

    def test_busy_and_loading_do_not_queue_text(self):
        body = {"text": "private ephemeral input", "emotions": EMOTIONS}
        self.worker.status = "loading"
        self.assertEqual(request(self.port, "POST", "/classify", body)[0], 503)
        self.worker.status = "ready"
        with self.worker.lock:
            self.assertEqual(request(self.port, "POST", "/classify", body)[0], 503)
        self.assertEqual(request(self.port, "POST", "/classify", body)[1]["emotion"], "heart")

    def test_custom_schema_validation(self):
        with self.assertRaises(ValueError):
            validate_request({"text": "hello", "emotions": EMOTIONS + [EMOTIONS[0]]})
        with self.assertRaises(ValueError):
            validate_request({"text": "x" * 1001, "emotions": EMOTIONS})
        with self.assertRaises(ValueError):
            validate_request({"text": "hello", "emotions": [{"id": "bad\nheader"}, {"id": "good"}]})
        _, choices, _, _ = validate_request({"text": "hello", "emotions": [{"id": "focus", "description": "concentration"}, {"id": "tired", "description": "fatigue"}]})
        self.assertEqual(choices, {"focus": "concentration", "tired": "fatigue"})

    def test_model_meanings_map_back_to_stable_image_ids(self):
        options, ids = model_options({"angry": "분노", "love": "사랑", "custom": "사랑"})
        self.assertEqual(list(options), ["분노", "사랑", "사랑 (custom)"])
        self.assertEqual(ids["분노"], "angry")
        self.assertEqual(ids["사랑"], "love")
        self.assertEqual(ids["사랑 (custom)"], "custom")

    def test_inference_errors_do_not_expose_input(self):
        def fail(text, *_):
            raise RuntimeError("private content=" + text)
        self.worker.classify = fail
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            status, response = request(self.port, "POST", "/classify", {"text": "secret-private-test", "emotions": EMOTIONS})
        self.assertEqual(status, 500)
        self.assertNotIn("secret-private-test", json.dumps(response) + out.getvalue() + err.getvalue())
        self.assertFalse(self.worker.lock.locked())


def smoke(model):
    # Launch a real subprocess, drive the same authenticated protocol as the app,
    # and always release the several-GB model process when this test finishes.
    port = 47829
    process = subprocess.Popen([sys.executable, "-u", str(Path(__file__).with_name("server.py")),
                                "--port", str(port), "--token", TOKEN, "--model", model],
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        deadline = time.monotonic() + 180
        while time.monotonic() < deadline:
            if process.poll() is not None:
                raise RuntimeError("Worker exited before readiness")
            try:
                _, health = request(port, "GET", "/health")
                if health["status"] == "error":
                    raise RuntimeError(health["error"])
                if health["status"] == "ready":
                    break
            except (ConnectionError, OSError):
                pass
            time.sleep(0.5)
        else:
            raise RuntimeError("Worker readiness timeout")
        print("Authenticated real-model HTTP smoke: " + model, flush=True)
        samples = {
            "angry": "I am so angry and furious. This is unacceptable!",
            "heart": "I love you so much, my darling! Sending you hugs and kisses.",
            "excited": "Yay! I won! This is amazing, I am so happy and excited!",
            "sad": "I am crying. I feel so lonely and sad. I miss my friend so much.",
        } if model == "multilingual" else {
            "angry": "진짜 너무 화나고 짜증나. 왜 자꾸 이런 일이 생기는 거야!",
            "heart": "너를 정말 사랑해. 보고 싶고 꼭 안아주고 싶어.",
            "excited": "야호 드디어 성공했다! 너무 기쁘고 신나! 최고야!",
            "sad": "너무 슬프고 외로워. 눈물이 나고 아무것도 하고 싶지 않아.",
        }
        matched = 0
        for expected, text in samples.items():
            status, result = request(port, "POST", "/classify", {"text": text, "emotions": EMOTIONS})
            if status != 200:
                raise RuntimeError("Inference HTTP " + str(status) + ": " + str(result))
            assert result["emotion"] in {e["id"] for e in EMOTIONS}
            assert abs(sum(result["probabilities"].values()) - 1.0) < 0.002
            matched += result["emotion"] == expected
            print(json.dumps({"expected": expected, **result}, ensure_ascii=False), flush=True)
        print("Sample agreement: %d/%d (smoke examples, not an accuracy benchmark)" % (matched, len(samples)), flush=True)
    finally:
        process.terminate()
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--smoke", choices=["multilingual", "multilingual"])
    args = parser.parse_args()
    if args.smoke:
        smoke(args.smoke)
    else:
        unittest.main(argv=[sys.argv[0]], verbosity=2)
