param(
    [ValidateSet('multilingual')][string]$Model = 'multilingual',
    [ValidateSet('auto','cuda','cpu')][string]$Device = 'auto',
    [string]$Python = ''
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
$inference = Join-Path $root 'inference'
$venv = Join-Path $inference '.venv'
$workerPython = Join-Path $venv 'Scripts\python.exe'
$statusFile = Join-Path $inference 'status.json'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$env:PYTHONUTF8 = '1'
# Users never install Python: a pinned standalone CPython is downloaded and verified.
$pythonUrl = 'https://github.com/astral-sh/python-build-standalone/releases/download/20260901/cpython-3.11.16+20260901-x86_64-pc-windows-msvc-install_only.tar.gz'
$pythonDigest = '6be524fa6752af802146a4adc7d098565425b0b1c166e19a5a7a4c8cccb86bf6'
$pythonHome = Join-Path $root 'runtime\python'
$env:USE_TF = '0'
$env:HF_HUB_DISABLE_TELEMETRY = '1'

function Set-SetupStatus([string]$State, [string]$Message) {
    Write-Output $Message
    $status = @{status=$State; model=$Model; message=$Message; updated=(Get-Date).ToString('o')}
    $tempStatus = $statusFile + '.tmp'
    [System.IO.File]::WriteAllText($tempStatus, ($status | ConvertTo-Json), [System.Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $tempStatus -Destination $statusFile -Force
}

try {
    if (-not $Python) {
        $Python = Join-Path $pythonHome 'python.exe'
        if (-not (Test-Path -LiteralPath $Python)) {
            Set-SetupStatus 'installing' '실행 환경을 내려받는 중… (약 20MB)'
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            $archive = Join-Path $env:TEMP ('emotioncat-python-' + [Guid]::NewGuid().ToString('N') + '.tar.gz')
            $staging = $pythonHome + '.tmp'
            try {
                Invoke-WebRequest -UseBasicParsing -Uri $pythonUrl -OutFile $archive
                if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLower() -ne $pythonDigest) { throw '실행 환경 파일이 손상되었습니다. 다시 시도해 주세요.' }
                if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
                New-Item -ItemType Directory -Force -Path $staging | Out-Null
                & "$env:WINDIR\System32\tar.exe" -xzf $archive -C $staging --strip-components 1
                if ($LASTEXITCODE -ne 0) { throw '실행 환경 압축을 풀 수 없습니다.' }
                if (Test-Path -LiteralPath $pythonHome) { Remove-Item -LiteralPath $pythonHome -Recurse -Force }
                Move-Item -LiteralPath $staging -Destination $pythonHome
            } finally {
                if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
            }
        }
    }
    Set-SetupStatus 'installing' '실행 환경 준비 중…'
    $venvOk = $false
    if (Test-Path -LiteralPath $workerPython) {
        & $workerPython -c "import sys; sys.exit(0 if sys.version_info[:2] == (3, 11) else 1)" 2>$null
        $venvOk = $LASTEXITCODE -eq 0
    }
    if (-not $venvOk) {
        if (Test-Path -LiteralPath $venv) { Remove-Item -LiteralPath $venv -Recurse -Force }
        & $Python -m venv $venv
        if ($LASTEXITCODE -ne 0) { throw '실행 환경을 만들 수 없습니다.' }
    }
    if ($Device -eq 'auto') {
        $Device = 'cpu'
        if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) {
            & nvidia-smi --query-gpu=name --format=csv,noheader 2>$null | Out-Null
            if ($LASTEXITCODE -eq 0) { $Device = 'cuda' }
        }
    }
    if ($Device -eq 'cuda') {
        Set-SetupStatus 'installing' 'AI 엔진(GPU) 설치 중… (처음에는 약 3.5GB, 몇 분 걸립니다)'
        & $workerPython -m pip install --disable-pip-version-check 'torch==2.8.0+cu128' --index-url https://download.pytorch.org/whl/cu128
    } else {
        Set-SetupStatus 'installing' 'AI 엔진 설치 중… (처음에는 몇 분 걸립니다)'
        & $workerPython -m pip install --disable-pip-version-check 'torch==2.8.0+cpu' --index-url https://download.pytorch.org/whl/cpu
    }
    if ($LASTEXITCODE -ne 0) { throw 'AI 엔진 설치에 실패했습니다. 인터넷 연결과 저장 공간을 확인해 주세요.' }
    Set-SetupStatus 'installing' '감정 분석 라이브러리 설치 중…'
    & $workerPython -m pip install --disable-pip-version-check -r (Join-Path $inference 'requirements.txt')
    if ($LASTEXITCODE -ne 0) { throw '감정 분석 라이브러리 설치에 실패했습니다.' }
    Set-SetupStatus 'downloading' '감정 모델 내려받는 중… (약 644MB)'
    & $workerPython -u (Join-Path $inference 'download_model.py') --model multilingual
    if ($LASTEXITCODE -ne 0) { throw '모델 다운로드에 실패했습니다. 다시 설치하면 이어서 받습니다.' }
    Set-SetupStatus 'ready' '설치 완료'
    exit 0
} catch {
    Set-SetupStatus 'error' $_.Exception.Message
    exit 1
}
