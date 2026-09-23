# EmotionCat

참고 이미지 기반의 로컬 감정 봉고캣입니다. Windows는 .NET Framework 4.8/WinForms, macOS는 Swift/AppKit을 사용합니다. Windows 감정 모델은 **Laya multilingual 322M / FP16 / DirectML GPU 전용**입니다. 하드웨어 GPU를 사용할 수 없거나 GPU 실행에 실패하면 경고를 표시하고 감정 분석을 끕니다. CPU 추론으로 전환하지 않습니다. 사용자 PC에는 Python·CUDA·로컬 서버 설치가 필요하지 않습니다.

## 다운로드

[Releases](https://github.com/kmu9842/EmotionCat/releases/latest)에서 OS별 ZIP을 받으세요.

- **Windows 10/11 x64 + DirectX 12 지원 하드웨어 GPU:** `EmotionCat-<버전>-windows-x64.zip` 압축을 쓰기 가능한 폴더에 풀고 `EmotionCat.exe`를 실행합니다. DirectML/DXCore를 지원하는 최신 그래픽 드라이버가 필요합니다. NVIDIA·AMD·Intel GPU를 사용하며, 소프트웨어 GPU는 사용하지 않습니다.
- **macOS 13.4+ (Universal):** `EmotionCat-<버전>-macOS-universal.zip` 압축을 풀어 `EmotionCat.app`을 `/Applications`로 옮깁니다. Apple 공증을 받지 않은 자체 서명 앱이므로 차단되면 `xattr -dr com.apple.quarantine /Applications/EmotionCat.app`을 실행합니다. 첫 실행 때 뜨는 **입력 모니터링**·**손쉬운 사용** 권한을 허용하면 바로 동작합니다.
- **Linux:** 데스크톱 앱은 아직 없습니다.

모델과 실행 라이브러리가 ZIP에 포함되어 압축을 풀면 오프라인으로 동작합니다. Windows GPU 모델은 약 646MB이고, macOS는 별도의 int8 모델을 사용합니다. Windows v2.0.0의 CPU 모델을 GPU 빌드에 그대로 복사해서 사용할 수 없습니다.

`v*` 태그를 push하면 `.github/workflows/release.yml`이 두 OS를 빌드해 릴리스로 게시합니다.

## Windows 실행

EmotionCat.exe 또는 Start EmotionCat.cmd를 실행하세요. 고양이를 우클릭하거나 트레이 아이콘을 더블클릭하면 설정이 열립니다.

- **감정 이미지:** 평온·화남·하트·신남·슬픔·놀람·졸림·혼란의 8개 감정과 기본/왼발/오른발/양발 32개 프레임이 연결되어 있습니다. 감정과 동작을 선택해 이미지를 교체할 수 있습니다. 감정 설명은 Laya가 선택할 기준입니다.
- **Laya / 입력:** 전역 키보드 문자를 기록해 Laya에 전달합니다. '입력 확인 · 분류 지시문'에서 최근 입력, 전달/응답 횟수, 실제 모델 결과와 지시문을 확인할 수 있습니다. 한/영 자동 감지가 맞지 않는 입력기는 한글 두벌식 또는 영문 모드를 선택할 수 있습니다.
- **모양 / 동작:** 크기, 작업표시줄 정렬, 문자 입력 후 대기 시간과 표정 유지 시간을 조절합니다. 키 입력 즉시 발이 움직입니다.

## 감정 모델

- Windows `model/laya-multilingual-gpu.onnx`: Laya multilingual 322M(Apache-2.0) FP16 모델입니다. 고정된 입력 크기와 패딩 마스크로 형상 계산을 미리 정리해 모든 모델 노드가 DirectML에서 실행되도록 합니다. `session.disable_cpu_ep_fallback=1`을 적용합니다.
- macOS `model/laya-multilingual-int8.onnx`: 별도의 int8(per-tensor) 모델입니다. 이번 Windows GPU 전환과는 다른 런타임을 사용합니다.
- `model/tokenizer.bin`: 원본 토크나이저(BPE, 어휘 256k)를 앱이 바로 읽는 형식으로 묶은 파일입니다. C#·Swift 구현이 원본과 3,060개 문장에서 토큰 단위로 일치합니다.
- GPU 연결 상태를 확인한 뒤 욕설을 최우선으로 화남에 연결합니다. 한국어 구어체·줄임말·자모 표현의 명확한 감정은 표현 규칙으로 처리하고, 그 밖의 문맥은 GPU 모델로 판단합니다. 설정의 분류 결과에 적용된 규칙 또는 GPU 모델을 구분해서 표시합니다.
- 프롬프트는 한국어 구어체, 감사·애정, 서운함·외로움, 피곤함, 감정 없는 질문·요청을 명시합니다. 기존 기본 프롬프트는 업데이트 시 새 기본값으로 바꾸며, 사용자가 작성한 프롬프트는 유지합니다. 규칙은 모든 신조어·비꼼·복잡한 부정을 완벽하게 이해하지는 않습니다.
- 변환·검증 스크립트는 `tools/onnx/`에 있습니다(개발용). GPU 모델은 SHA-256으로 고정한 [`model-v1`의 FP32 원본](https://github.com/kmu9842/EmotionCat/releases/tag/model-v1)에서 생성합니다. 토크나이저는 [`model-v2`](https://github.com/kmu9842/EmotionCat/releases/tag/model-v2)를 사용합니다.

## 입력과 응답

Windows 전역 키보드 훅에서 문자를 받아 최대 240자를 메모리에 기록합니다. 편집기나 접근성 텍스트 지원 여부에 의존하지 않습니다. 한글 두벌식은 키 입력을 음절로 조합하며, 다른 문자는 현재 키보드 레이아웃으로 변환합니다. 비밀번호 필드와 제외 앱은 건너뜁니다. 기존 문서나 클립보드는 읽지 않으므로 붙여넣기한 본문은 포함되지 않습니다.

문자 입력이 들어오면 0.5초 뒤 실행할 분석을 한 번 예약합니다. 그동안 기록된 최신 문자열을 Laya에 전달하며, 연속 입력으로 예정 시각을 미루지 않습니다. 분석 후에는 다음 문자가 들어와야 다시 예약합니다. Shift·Ctrl·방향키만 누르거나 아무 입력이 없으면 추론하지 않습니다. 입력 대기 스레드도 무입력 시 잠들어 있습니다. Enter·클릭·포커스 이동은 다음 입력 구간을 나누지만 이미 예약한 문장을 취소하지 않습니다. 1초간 새 결과가 없으면 평온으로 돌아옵니다.

추론은 앱 프로세스 안에서만 실행되며 네트워크를 사용하지 않습니다. 입력 내용은 파일에 기록하지 않습니다. 모델은 문맥이나 한국어 표현을 오분류할 수 있으며, 이미지 레이블 지정은 학습이 아니라 요청 시 선택지 설정입니다.

## 빌드와 검증

Windows 개발 빌드는 GitHub CLI 인증과 Python 3.12 이상이 필요합니다. 아래 준비 스크립트가 별도 빌드 환경에서 FP16 모델을 만들고 ONNX Runtime DirectML 1.24.4와 DirectML 1.15.4를 배치합니다. 이 개발 도구들은 배포 ZIP에 포함하지 않습니다.

    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/prepare-windows.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File tests/run-core-tests.ps1 -Golden tests/onnx-golden.json -Integration
    powershell -NoProfile -ExecutionPolicy Bypass -File tests/run-gpu-tests.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File tests/run-korean-tests.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File tests/run-input-tests.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File tests/run-live-input-tests.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File tests/run-visual-tests.ps1

GPU가 없는 CI에서는 `tests/run-core-tests.ps1 -ExpectGpuUnavailable`로 분석이 비활성화되는지 검사합니다. GPU 프로파일·정확도·실입력 검사는 GPU가 있는 컴퓨터에서 실행해야 합니다. `run-gpu-tests.ps1`는 CPU EP 노드가 0개인지와 대기 CPU 사용률을 검사합니다.

Live 검사는 실행 중인 EmotionCat을 종료한 후 실행합니다. 편집 컨트롤이 없는 별도 시험 창에만 OS 키 이벤트를 보내고, 그 프로세스만 감지합니다. 한/영 자동 감지 → 한글 조합 → 0.5초 대기 → Laya(ONNX) → 실제 투명 창의 하트·화남 프레임까지 검증합니다. 무입력과 보조키 입력 중 추론 횟수가 증가하지 않는지도 확인합니다. 사용자의 실제 입력은 테스트로 수집하지 않습니다.

macOS 소스와 Universal 빌드 스크립트는 [macos/README.md](macos/README.md)에 있습니다. GitHub Actions에서 Universal 빌드·서명·스프라이트 검사와 ONNX 기준 데이터 검사를 실행합니다. `main` 빌드의 앱 ZIP과 SHA-256은 [Releases](https://github.com/kmu9842/EmotionCat/releases)의 커밋별 초안에 저장되며, Actions 실행 요약에도 다운로드 링크가 표시됩니다. Actions artifact 저장공간을 사용하지 않습니다. Linux 데스크톱 앱은 아직 없습니다.
