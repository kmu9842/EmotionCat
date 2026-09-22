#!/bin/bash
# Build on macOS with Apple's Command Line Tools; no package manager or UI runtime.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
[[ "$(uname -s)" == Darwin ]] || { printf '%s\n' 'Build this native AppKit app on macOS (Xcode Command Line Tools required).' >&2; exit 1; }
command -v xcrun >/dev/null || { printf '%s\n' 'Install Apple Command Line Tools: xcode-select --install' >&2; exit 1; }
output="${EMOTIONCAT_BUILD_DIR:-$root/macos/build}"
mkdir -p "$output"
output="$(cd "$output" && pwd -P)"
app="$output/EmotionCat.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources/assets/frames" \
    "$app/Contents/Resources/inference" "$app/Contents/Resources/scripts"
sdk="$(xcrun --sdk macosx --show-sdk-path)"
read -r -a architectures <<< "${ARCHS:-arm64 x86_64}"
[[ ${#architectures[@]} -gt 0 ]] || { printf '%s\n' 'ARCHS must contain arm64 and/or x86_64.' >&2; exit 1; }
binaries=()
for arch in "${architectures[@]}"; do
    [[ "$arch" == arm64 || "$arch" == x86_64 ]] || { printf 'Unsupported architecture: %s\n' "$arch" >&2; exit 1; }
    binary="$output/EmotionCat-$arch"
    xcrun --sdk macosx swiftc -swift-version 5 -O -whole-module-optimization \
        -sdk "$sdk" -target "$arch-apple-macosx12.0" \
        -framework Cocoa -framework ApplicationServices -framework Carbon -module-name EmotionCat \
        "$root"/macos/Sources/*.swift -o "$binary"
    binaries+=("$binary")
done
if [[ ${#binaries[@]} -eq 1 ]]; then
    cp "${binaries[0]}" "$app/Contents/MacOS/EmotionCat"
else
    xcrun lipo -create "${binaries[@]}" -output "$app/Contents/MacOS/EmotionCat"
fi
cp "$root/macos/Info.plist" "$app/Contents/Info.plist"
cp "$root"/assets/frames/*.png "$app/Contents/Resources/assets/frames/"
cp "$root"/inference/*.py "$root/inference/requirements.txt" "$app/Contents/Resources/inference/"
cp "$root/scripts/setup-laya.sh" "$app/Contents/Resources/scripts/"
chmod +x "$app/Contents/MacOS/EmotionCat" "$app/Contents/Resources/scripts/setup-laya.sh"
/usr/bin/plutil -lint "$app/Contents/Info.plist"
/usr/bin/codesign --force --sign "${SIGNING_IDENTITY:--}" "$app"
/usr/bin/codesign --verify --strict "$app"
"$app/Contents/MacOS/EmotionCat" --verify-assets
printf '\nBuilt: %s\nOpen with: open "%s"\n' "$app" "$app"
