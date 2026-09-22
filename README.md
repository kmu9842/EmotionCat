# EmotionCat

참고 이미지 기반의 로컬 감정 봉고캣입니다. Windows는 .NET Framework 4.8/WinForms, macOS는 Swift/AppKit을 사용합니다. 브라우저 런타임이나 게임 엔진은 포함하지 않습니다. 모델은 별도 Python 프로세스에서 실행하는 **Laya multilingual 322M 하나**입니다.

## Windows 실행

EmotionCat.exe 또는 Start EmotionCat.cmd를 실행하세요. 고양이를 우클릭하거나 트레이 아이콘을 더블클릭하면 설정이 열립니다.

- **감정 이미지:** 평온·화남·하트·신남·슬픔·놀람·졸림·혼란의 8개 감정과 기본/왼발/오른발/양발 32개 프레임이 연결되어 있습니다. 감정과 동작을 선택해 이미지를 교체할 수 있습니다. 감정 설명은 Laya가 선택할 기준입니다.
- **Laya / 입력:** 전역 키보드 문자를 기록해 Laya에 전달합니다. '입력 확인 · 분류 지시문'에서 최근 입력, 전달/응답 횟수, 실제 모델 결과와 지시문을 확인할 수 있습니다. 한/영 자동 감지가 맞지 않는 입력기는 한글 두벌식 또는 영문 모드를 선택할 수 있습니다.
- **모양 / 동작:** 크기, 작업표시줄 정렬, 문자 입력 후 대기 시간과 표정 유지 시간을 조절합니다. 키 입력 즉시 발이 움직입니다.

현재 설치된 NVIDIA 환경은 CUDA를 사용합니다. 새 컴퓨터에서는 Python 3.10–3.13 x64를 설치한 뒤 설정의 모델 설치를 누르거나 다음을 실행하세요.

    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/setup-laya.ps1

설치기는 NVIDIA GPU를 감지하면 CUDA 12.8 PyTorch를 설치하고, 없으면 CPU용 런타임을 설치합니다. GPU 런타임은 최초 약 3.5GB 다운로드이며 모델 파일은 약 644MB입니다. 런타임 설치 크기와 실행 중 GPU 메모리는 다릅니다.

## 입력과 응답

Windows 전역 키보드 훅에서 문자를 받아 최대 240자를 메모리에 기록합니다. 편집기나 접근성 텍스트 지원 여부에 의존하지 않습니다. 한글 두벌식은 키 입력을 음절로 조합하며, 다른 문자는 현재 키보드 레이아웃으로 변환합니다. 비밀번호 필드와 제외 앱은 건너뜁니다. 기존 문서나 클립보드는 읽지 않으므로 붙여넣기한 본문은 포함되지 않습니다.

문자 입력이 들어오면 1초 뒤 실행할 분석을 한 번 예약합니다. 그동안 기록된 최신 문자열을 Laya에 전달하며, 연속 입력으로 예정 시각을 미루지 않습니다. 분석 후에는 다음 문자가 들어와야 다시 예약합니다. Shift·Ctrl·방향키만 누르거나 아무 입력이 없으면 추론하지 않습니다. 입력 대기 스레드도 무입력 시 잠들어 있습니다. Enter·클릭·포커스 이동은 다음 입력 구간을 나누지만 이미 예약한 문장을 취소하지 않습니다. 4초간 새 결과가 없으면 평온으로 돌아옵니다.

설치 이후 추론은 인증된 로컬 주소 127.0.0.1에서만 동작합니다. 입력 내용은 파일에 기록하지 않습니다. 모델은 문맥이나 한국어 표현을 오분류할 수 있으며, 이미지 레이블 지정은 학습이 아니라 요청 시 선택지 설정입니다.

## 빌드와 검증

    powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File tests/run-core-tests.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File tests/run-input-tests.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File tests/run-live-input-tests.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File tests/run-visual-tests.ps1
    inference\.venv\Scripts\python.exe inference/test_server.py

Live 검사는 실행 중인 EmotionCat을 종료한 후 실행합니다. 편집 컨트롤이 없는 별도 시험 창에만 OS 키 이벤트를 보내고, 그 프로세스만 감지합니다. 한/영 자동 감지 → 한글 조합 → 1초 대기 → GPU Laya → 실제 투명 창의 하트·화남 프레임까지 검증합니다. 무입력과 보조키 입력 중 추론 횟수가 증가하지 않는지도 확인합니다. 사용자의 실제 입력은 테스트로 수집하지 않습니다.

macOS 소스와 Universal 빌드 스크립트는 [macos/README.md](macos/README.md)에 있습니다. **현재 Windows에서 구문 검사만 했으며 실제 Mac 빌드·실행·MPS 성능은 검증하지 않았습니다.** GitHub Actions용 macOS 빌드 워크플로도 포함합니다. Linux는 추론 설치 스크립트만 있으며 데스크톱 앱은 아직 없습니다.
