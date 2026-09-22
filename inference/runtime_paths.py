"""Writable inference paths, kept separate from packaged/read-only app resources."""
import argparse
import os
from pathlib import Path
import sys

RESOURCE_ROOT = Path(__file__).resolve().parent


def data_directory(explicit=None, *, platform=None, environ=None, user_home=None):
    """Return the runtime directory without creating it or touching the filesystem."""
    if explicit:
        return Path(explicit).expanduser().resolve()
    platform = sys.platform if platform is None else platform
    environ = os.environ if environ is None else environ
    user_home = Path.home() if user_home is None else Path(user_home)
    if platform == "darwin":
        return user_home / "Library" / "Application Support" / "EmotionCat" / "inference"
    if platform.startswith("linux"):
        xdg_data = environ.get("XDG_DATA_HOME", "")
        base = Path(xdg_data).expanduser() if xdg_data else user_home / ".local" / "share"
        return base / "EmotionCat" / "inference"
    # Preserve the original portable Windows directory layout.
    return RESOURCE_ROOT


def model_directory(explicit_data_dir=None, *, platform=None, environ=None, user_home=None):
    environ = os.environ if environ is None else environ
    if explicit_data_dir:
        return data_directory(explicit_data_dir) / "models"
    external_models = environ.get("EMOTIONCAT_MODEL_DIR", "")
    if external_models:
        return Path(external_models).expanduser().resolve()
    return data_directory(platform=platform, environ=environ, user_home=user_home) / "models"


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-dir")
    parser.add_argument("--print-model-dir", action="store_true")
    args = parser.parse_args()
    print(model_directory(args.data_dir) if args.print_model_dir else data_directory(args.data_dir))
