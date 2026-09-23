#!/bin/bash
# Build ZIPs use release assets instead of the account's Actions artifact quota.
set -euo pipefail
: "${GH_TOKEN:?GitHub token is required}"
: "${GH_REPO:?Repository is required}"
: "${BUILD_SHA:?Source commit is required}"
: "${RUNNER_TEMP:?Runner temporary directory is required}"
[[ "$BUILD_SHA" =~ ^[0-9a-f]{40}$ ]] || { printf '%s\n' 'Invalid source commit.' >&2; exit 1; }

archive='macos/build/EmotionCat-macOS.zip'
[[ -s "$archive" ]] || { printf '%s\n' 'The macOS build ZIP is missing or empty.' >&2; exit 1; }
tag="macos-${BUILD_SHA:0:12}"
notes="$RUNNER_TEMP/emotioncat-macos-release.md"
cat > "$notes" <<EOF
Native macOS Universal build (Apple Silicon and Intel).

Source commit: $BUILD_SHA
Build: ${BUILD_RUN_URL:-local}

Compilation and code-signature validation passed.
The Laya ONNX model and ONNX Runtime are bundled; no Python or download is needed.
EOF
(cd macos/build && shasum -a 256 EmotionCat-macOS.zip > EmotionCat-macOS.zip.sha256)

if gh release view "$tag" --json isDraft --jq '.isDraft' > "$RUNNER_TEMP/emotioncat-release-state" 2>/dev/null; then
    [[ "$(cat "$RUNNER_TEMP/emotioncat-release-state")" == true ]] || {
        printf '%s\n' 'This build has already been published; refusing to replace its assets.' >&2
        exit 1
    }
else
    gh release create "$tag" --target "$BUILD_SHA" --draft \
        --title "EmotionCat macOS ${BUILD_SHA:0:12}" --notes-file "$notes"
fi
gh release upload "$tag" "$archive" "$archive.sha256" --clobber
url="$(gh release view "$tag" --json url --jq '.url')"
printf 'Build download: %s\n' "$url"
if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    printf '### macOS build\n\n[Download ZIP and SHA-256 checksum from the draft release](%s)\n\nCommit: %s\n' \
        "$url" "$BUILD_SHA" >> "$GITHUB_STEP_SUMMARY"
fi
