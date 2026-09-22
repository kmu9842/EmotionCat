#!/bin/bash
# macOS/Linux installer. App resources are read-only; runtime and weights are user data.
set -Eeuo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
resources="$(cd "$script_dir/../inference" && pwd -P)"
model=multilingual
data_dir=""
explicit_data=0
python_bin=""
active_pid=""
status_file=""
export PATH="/opt/homebrew/bin:/usr/local/bin:${PATH:-/usr/bin:/bin}"
export PYTHONUTF8=1 PYTHONDONTWRITEBYTECODE=1 USE_TF=0 USE_FLAX=0 HF_HUB_DISABLE_TELEMETRY=1

usage() {
    printf '%s\n' 'Usage: setup-laya.sh [--model multilingual] [--data-dir PATH] [--python PATH]'
}
while [[ $# -gt 0 ]]; do
    case "$1" in
        --model|--data-dir|--python)
            [[ $# -ge 2 ]] || { usage >&2; exit 2; }
            case "$1" in
                --model) model="$2" ;;
                --data-dir) data_dir="$2"; explicit_data=1 ;;
                --python) python_bin="$2" ;;
            esac
            shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) usage >&2; exit 2 ;;
    esac
done
[[ "$model" == multilingual ]] || { usage >&2; exit 2; }

valid_python() {
    "$1" -c 'import sys,platform; maximum=(3,11) if sys.platform=="darwin" and platform.machine()=="x86_64" else (3,13); sys.exit(0 if (3,10)<=sys.version_info[:2]<=maximum and sys.maxsize>2**32 else 1)' >/dev/null 2>&1
}
if [[ -z "$python_bin" ]]; then
    for candidate in python3.11 python3.10 python3.12 python3.13 python3; do
        candidate_path="$(command -v "$candidate" 2>/dev/null || true)"
        # Apple's developer-tools stub can open an Xcode installation dialog.
        if [[ "$(uname -s)" == Darwin && "$candidate_path" == /usr/bin/python3 ]]; then
            continue
        fi
        if [[ -n "$candidate_path" ]] && valid_python "$candidate_path"; then
            python_bin="$candidate_path"
            break
        fi
    done
fi
if [[ -z "$python_bin" ]] || ! valid_python "$python_bin"; then
    printf '%s\n' 'Python 3.10-3.13 (64-bit) is required. Intel Mac: use Python 3.10-3.11. Install Python, then retry or pass --python /path/to/python3.' >&2
    exit 1
fi
if [[ "$explicit_data" == 1 ]]; then
    data_dir="$("$python_bin" "$resources/runtime_paths.py" --data-dir "$data_dir")"
else
    data_dir="$("$python_bin" "$resources/runtime_paths.py")"
fi
mkdir -p "$data_dir"
status_file="$data_dir/status.json"

write_status() {
    printf '%s\n' "$2"
    "$python_bin" - "$status_file" "$1" "$model" "$2" <<'PY'
import datetime,json,os,pathlib,sys
path=pathlib.Path(sys.argv[1])
temp=path.with_name(path.name+'.tmp')
temp.write_text(json.dumps({'status':sys.argv[2], 'model':sys.argv[3], 'message':sys.argv[4], 'updated':datetime.datetime.now(datetime.timezone.utc).isoformat()}),encoding='utf-8')
os.replace(temp,path)
PY
}
terminate_child() {
    if [[ -n "$active_pid" ]]; then
        kill -TERM "$active_pid" 2>/dev/null || true
        wait "$active_pid" 2>/dev/null || true
        active_pid=""
    fi
}
on_stop() {
    trap - TERM INT HUP ERR
    terminate_child
    write_status error 'Laya setup was cancelled. Run setup again to resume.' || true
    exit 130
}
on_error() {
    local code="$1"
    trap - ERR
    terminate_child
    write_status error 'Laya setup failed. Check the displayed output, Python version, available space and network; retry to resume.' || true
    exit "$code"
}
trap on_stop TERM INT HUP
trap 'on_error "$?"' ERR
run() {
    "$@" &
    active_pid=$!
    wait "$active_pid"
    active_pid=""
}

worker_python="$data_dir/.venv/bin/python3"
write_status installing 'Preparing isolated Python runtime...'
if [[ ! -x "$worker_python" ]]; then
    run "$python_bin" -m venv "$data_dir/.venv"
fi
if ! valid_python "$worker_python"; then
    write_status error 'The existing runtime uses an unsupported Python version. Choose a new --data-dir or reinstall its .venv with a compatible Python.'
    exit 1
fi
runtime_platform="$("$worker_python" -c 'import sys,platform; print(sys.platform+":"+platform.machine())')"
write_status installing 'Installing CPU PyTorch (first install can take several minutes)...'
case "$runtime_platform" in
    darwin:arm64)
        # macOS CPU/MPS wheels are published on PyPI, not the Linux CPU index.
        run "$worker_python" -m pip install --disable-pip-version-check 'torch==2.8.0' ;;
    darwin:x86_64)
        # Intel wheels stop at 2.2.2; its compile decorators need Python <3.12 and NumPy <2.
        run "$worker_python" -m pip install --disable-pip-version-check 'torch==2.2.2' 'numpy==1.26.4' ;;
    linux:*)
        run "$worker_python" -m pip install --disable-pip-version-check 'torch==2.8.0' --index-url https://download.pytorch.org/whl/cpu ;;
    *)
        write_status error "Unsupported installer platform: $runtime_platform. On Windows use setup-laya.ps1."
        exit 1 ;;
esac
write_status installing 'Installing Laya SDK...'
run "$worker_python" -m pip install --disable-pip-version-check -r "$resources/requirements.txt"
write_status installing 'Checking Laya and ModernBERT runtime imports...'
run "$worker_python" -c 'from transformers.models.modernbert.modeling_modernbert import ModernBertModel; import laya; print("Laya runtime imports verified", flush=True)'
size='843 MB'
[[ "$model" != multilingual ]] || size='644 MB'
write_status downloading "Downloading $model model (about $size; runtime packages are additional)..."
download_args=(--model "$model")
if [[ "$explicit_data" == 1 ]]; then
    download_args+=(--data-dir "$data_dir")
fi
run "$worker_python" -u "$resources/download_model.py" "${download_args[@]}"
write_status ready "Laya $model is installed. Enable Laya in EmotionCat settings."
