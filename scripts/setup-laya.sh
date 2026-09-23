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

# Users never install Python: a pinned standalone CPython is downloaded and verified.
python_release='20260901'
python_version='3.11.16'
standalone_python() {
    local target triple digest archive url
    target="$1"
    case "$(uname -s):$(uname -m)" in
        Darwin:arm64) triple=aarch64-apple-darwin; digest=50424fa409e8ae84b82a3052522f64695b47dff2158b70bb7358e0ebd6c085c9 ;;
        Darwin:x86_64) triple=x86_64-apple-darwin; digest=167cc15cf4eeb72944a67bbd2f7120c45fded17d5043d5db64b3144d7adc30ae ;;
        Linux:x86_64) triple=x86_64-unknown-linux-gnu; digest=faa0758583a63f14c5eee516af82738403b59c13edda6fc0a21d953febd89eed ;;
        *) printf '%s\n' "지원하지 않는 시스템입니다: $(uname -s) $(uname -m)" >&2; return 1 ;;
    esac
    if [[ -x "$target/bin/python3" ]] && "$target/bin/python3" -c "import sys; sys.exit(0 if sys.version.startswith('$python_version') else 1)" 2>/dev/null; then
        return 0
    fi
    archive="$(mktemp "${TMPDIR:-/tmp}/emotioncat-python.XXXXXX")"
    url="https://github.com/astral-sh/python-build-standalone/releases/download/$python_release/cpython-$python_version+$python_release-$triple-install_only.tar.gz"
    printf '%s\n' '실행 환경을 내려받는 중… (약 20MB)'
    curl -fL --retry 3 --silent --show-error -o "$archive" "$url"
    [[ "$(shasum -a 256 "$archive" | cut -d' ' -f1)" == "$digest" ]] || { rm -f "$archive"; printf '%s\n' '실행 환경 파일이 손상되었습니다. 다시 시도해 주세요.' >&2; return 1; }
    rm -rf "$target.tmp" && mkdir -p "$target.tmp"
    tar -xzf "$archive" -C "$target.tmp" --strip-components 1
    rm -f "$archive"
    rm -rf "$target" && mv "$target.tmp" "$target"
}
if [[ "$explicit_data" == 0 ]]; then
    case "$(uname -s)" in
        Darwin) data_dir="$HOME/Library/Application Support/EmotionCat/inference" ;;
        *) data_dir="${XDG_DATA_HOME:-$HOME/.local/share}/EmotionCat/inference" ;;
    esac
fi
mkdir -p "$data_dir"
data_dir="$(cd "$data_dir" && pwd -P)"
if [[ -z "$python_bin" ]]; then
    standalone_python "$data_dir/python"
    python_bin="$data_dir/python/bin/python3"
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
    write_status error '설치가 취소되었습니다. 다시 설치하면 이어서 진행합니다.' || true
    exit 130
}
on_error() {
    local code="$1"
    trap - ERR
    terminate_child
    write_status error '설치에 실패했습니다. 인터넷 연결과 저장 공간을 확인한 뒤 다시 시도해 주세요.' || true
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
write_status installing '실행 환경 준비 중…'
if ! "$worker_python" -c "import sys; sys.exit(0 if sys.version.startswith('$python_version') else 1)" 2>/dev/null; then
    rm -rf "$data_dir/.venv"
    run "$python_bin" -m venv "$data_dir/.venv"
fi
runtime_platform="$("$worker_python" -c 'import sys,platform; print(sys.platform+":"+platform.machine())')"
write_status installing 'AI 엔진 설치 중… (처음에는 몇 분 걸립니다)'
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
        write_status error "지원하지 않는 시스템입니다: $runtime_platform"
        exit 1 ;;
esac
write_status installing '감정 분석 라이브러리 설치 중…'
run "$worker_python" -m pip install --disable-pip-version-check -r "$resources/requirements.txt"
write_status installing '설치 확인 중…'
run "$worker_python" -c 'from transformers.models.modernbert.modeling_modernbert import ModernBertModel; import laya; print("Laya runtime imports verified", flush=True)'
size='843 MB'
[[ "$model" != multilingual ]] || size='644 MB'
write_status downloading "감정 모델 내려받는 중… (약 $size)"
download_args=(--model "$model")
if [[ "$explicit_data" == 1 ]]; then
    download_args+=(--data-dir "$data_dir")
fi
run "$worker_python" -u "$resources/download_model.py" "${download_args[@]}"
write_status ready '설치 완료'
