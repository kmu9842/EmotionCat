#!/bin/bash
# CI helper: download the bundled model files from the model release and verify SHA-256.
# Uses the REST asset endpoint because `gh release download` intermittently lists no assets.
set -euo pipefail
: "${GH_TOKEN:?GitHub token is required}"
: "${GH_REPO:?Repository is required}"
: "${MODEL_RELEASE:?Model release tag is required}"
dest="${1:?Usage: fetch-model.sh DEST_DIR}"
mkdir -p "$dest"
digest() { if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }
api() { curl -fsSL --retry 3 -H "Authorization: Bearer $GH_TOKEN" -H 'Accept: application/vnd.github+json' "https://api.github.com/repos/$GH_REPO/$1"; }
# The release object's embedded asset list can lag; the assets endpoint is authoritative.
release_id="$(api "releases/tags/$MODEL_RELEASE" | jq -er .id)"
assets="$(api "releases/$release_id/assets?per_page=100")"
fetch() {
    local name="$1" expected="${2:-}" id
    id="$(printf '%s' "$assets" | jq -er --arg n "$name" 'first(.[] | select(.name == $n and .state == "uploaded") | .id)')"
    curl -fsSL --retry 3 -H "Authorization: Bearer $GH_TOKEN" -H 'Accept: application/octet-stream' \
        -o "$dest/$name" "https://api.github.com/repos/$GH_REPO/releases/assets/$id"
    if [[ -n "$expected" && "$(digest "$dest/$name")" != "$expected" ]]; then
        printf 'Checksum mismatch: %s\n' "$name" >&2; exit 1
    fi
    printf 'Fetched %s\n' "$name"
}
fetch laya-multilingual-int8.onnx "${MODEL_SHA256:?}"
fetch tokenizer.bin "${TOKENIZER_SHA256:?}"
fetch Laya-APACHE-2.0.txt
