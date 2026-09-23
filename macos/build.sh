#!/bin/bash
# Build on macOS with Apple's Command Line Tools; no package manager, UI runtime or Python.
# Emotion inference runs in-process through the bundled ONNX Runtime and int8 Laya model.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
[[ "$(uname -s)" == Darwin ]] || { printf '%s\n' 'Build this native AppKit app on macOS (Xcode Command Line Tools required).' >&2; exit 1; }
command -v xcrun >/dev/null || { printf '%s\n' 'Install Apple Command Line Tools: xcode-select --install' >&2; exit 1; }

# Model files are too large for git; point EMOTIONCAT_MODEL_DIR at the exported ONNX directory.
model_dir="${EMOTIONCAT_MODEL_DIR:-}"
model_files=(laya-multilingual-int8.onnx tokenizer.bin)
[[ -n "$model_dir" ]] || { printf '%s\n' 'Set EMOTIONCAT_MODEL_DIR to a directory containing laya-multilingual-int8.onnx and tokenizer.bin.' >&2; exit 1; }
for file in "${model_files[@]}"; do
    [[ -s "$model_dir/$file" ]] || { printf 'Missing model file: %s/%s (EMOTIONCAT_MODEL_DIR)\n' "$model_dir" "$file" >&2; exit 1; }
done

output="${EMOTIONCAT_BUILD_DIR:-$root/macos/build}"
mkdir -p "$output"
output="$(cd "$output" && pwd -P)"

# ONNX Runtime (official universal2 release), cached and SHA-256 verified.
ort_version=1.23.2
ort_name="onnxruntime-osx-universal2-$ort_version"
ort_sha256=49ae8e3a66ccb18d98ad3fe7f5906b6d7887df8a5edd40f49eb2b14e20885809
ort_url="https://github.com/microsoft/onnxruntime/releases/download/v$ort_version/$ort_name.tgz"
cache="$output/cache"
ort_archive="$cache/$ort_name.tgz"
ort="$cache/$ort_name"
ort_dylib="libonnxruntime.$ort_version.dylib"
mkdir -p "$cache"
verify_ort() { [[ -f "$ort_archive" ]] && [[ "$(/usr/bin/shasum -a 256 "$ort_archive" | cut -d' ' -f1)" == "$ort_sha256" ]]; }
if ! verify_ort; then
    rm -f "$ort_archive" "$ort_archive.part"
    printf 'Downloading %s\n' "$ort_url"
    curl --fail --location --silent --show-error --retry 3 --output "$ort_archive.part" "$ort_url"
    mv "$ort_archive.part" "$ort_archive"
    verify_ort || { rm -f "$ort_archive"; printf '%s\n' "ONNX Runtime archive SHA-256 mismatch (expected $ort_sha256)." >&2; exit 1; }
    rm -rf "$ort"
fi
if [[ ! -f "$ort/lib/$ort_dylib" || ! -f "$ort/include/onnxruntime_c_api.h" ]]; then
    rm -rf "$ort"
    tar -xzf "$ort_archive" -C "$cache"
fi

app="$output/EmotionCat.app"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Frameworks" "$app/Contents/Resources/assets/frames" \
    "$app/Contents/Resources/model" "$app/Contents/Resources/licenses/onnxruntime"
sdk="$(xcrun --sdk macosx --show-sdk-path)"
read -r -a architectures <<< "${ARCHS:-arm64 x86_64}"
[[ ${#architectures[@]} -gt 0 ]] || { printf '%s\n' 'ARCHS must contain arm64 and/or x86_64.' >&2; exit 1; }
binaries=()
for arch in "${architectures[@]}"; do
    [[ "$arch" == arm64 || "$arch" == x86_64 ]] || { printf 'Unsupported architecture: %s\n' "$arch" >&2; exit 1; }
    binary="$output/EmotionCat-$arch"
    xcrun --sdk macosx swiftc -swift-version 5 -O -whole-module-optimization \
        -sdk "$sdk" -target "$arch-apple-macosx13.4" \
        -import-objc-header "$root/macos/Sources/EmotionCat-Bridging-Header.h" -I "$ort/include" \
        -L "$ort/lib" -lonnxruntime -Xlinker -rpath -Xlinker @executable_path/../Frameworks \
        -framework Cocoa -framework ApplicationServices -framework Carbon -module-name EmotionCat \
        "$root"/macos/Sources/*.swift -o "$binary"
    binaries+=("$binary")
done
if [[ ${#binaries[@]} -eq 1 ]]; then
    cp "${binaries[0]}" "$app/Contents/MacOS/EmotionCat"
else
    xcrun lipo -create "${binaries[@]}" -output "$app/Contents/MacOS/EmotionCat"
fi

# ONNX Runtime 1.23.2 requires macOS 13.4+, so the app deployment target matches it.
# The executable loads @rpath/<install name>; bundle the dylib under exactly that name.
cp "$ort/lib/$ort_dylib" "$app/Contents/Frameworks/$ort_dylib"
chmod u+w "$app/Contents/Frameworks/$ort_dylib"
install_name="$(xcrun otool -D "$app/Contents/Frameworks/$ort_dylib" | sed -n '2p')"
if [[ "$install_name" != "@rpath/$ort_dylib" ]]; then
    xcrun install_name_tool -id "@rpath/$ort_dylib" "$app/Contents/Frameworks/$ort_dylib"
    xcrun install_name_tool -change "$install_name" "@rpath/$ort_dylib" "$app/Contents/MacOS/EmotionCat"
fi
xcrun otool -L "$app/Contents/MacOS/EmotionCat" | grep -q "@rpath/$ort_dylib" \
    || { printf '%s\n' "EmotionCat does not link @rpath/$ort_dylib." >&2; exit 1; }
cp "$ort/LICENSE" "$ort/ThirdPartyNotices.txt" "$app/Contents/Resources/licenses/onnxruntime/"

cp "$root/macos/Info.plist" "$app/Contents/Info.plist"
cp "$root"/assets/frames/*.png "$app/Contents/Resources/assets/frames/"
for file in "${model_files[@]}"; do cp "$model_dir/$file" "$app/Contents/Resources/model/$file"; done
if [[ -f "$model_dir/Laya-APACHE-2.0.txt" ]]; then cp "$model_dir/Laya-APACHE-2.0.txt" "$app/Contents/Resources/licenses/"; fi
chmod +x "$app/Contents/MacOS/EmotionCat"
/usr/bin/plutil -lint "$app/Contents/Info.plist"
/usr/bin/codesign --force --sign "${SIGNING_IDENTITY:--}" "$app/Contents/Frameworks/$ort_dylib"
/usr/bin/codesign --force --sign "${SIGNING_IDENTITY:--}" "$app"
/usr/bin/codesign --verify --strict "$app"
"$app/Contents/MacOS/EmotionCat" --verify-assets
"$app/Contents/MacOS/EmotionCat" --verify-model "$root/tests/onnx-golden.json"
printf '\nBuilt: %s\nOpen with: open "%s"\n' "$app" "$app"
