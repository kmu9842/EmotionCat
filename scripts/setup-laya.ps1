param(
    [ValidateSet('multilingual')][string]$Model = 'multilingual',
    [ValidateSet('auto','cuda','cpu')][string]$Device = 'auto',
    [string]$Python = 'python'
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
$inference = Join-Path $root 'inference'
$venv = Join-Path $inference '.venv'
$workerPython = Join-Path $venv 'Scripts\python.exe'
$statusFile = Join-Path $inference 'status.json'
$env:PYTHONUTF8 = '1'
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
    Set-SetupStatus 'installing' 'Preparing isolated Python runtime...'
    if (-not (Test-Path -LiteralPath $workerPython)) {
        & $Python -c "import sys; assert (3,10) <= sys.version_info[:2] <= (3,13), 'Use Python 3.10-3.13 x64'; assert sys.maxsize > 2**32, 'Use 64-bit Python'"
        if ($LASTEXITCODE -ne 0) { throw 'Python 3.10-3.13 x64 is required. Install Python and add it to PATH.' }
        & $Python -m venv $venv
        if ($LASTEXITCODE -ne 0) { throw 'Unable to create inference/.venv.' }
    }
    if ($Device -eq 'auto') {
        $Device = 'cpu'
        if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) {
            & nvidia-smi --query-gpu=name --format=csv,noheader 2>$null | Out-Null
            if ($LASTEXITCODE -eq 0) { $Device = 'cuda' }
        }
    }
    if ($Device -eq 'cuda') {
        Set-SetupStatus 'installing' 'Installing CUDA 12.8 PyTorch (about 3.5 GB on first install)...'
        & $workerPython -m pip install --disable-pip-version-check 'torch==2.8.0+cu128' --index-url https://download.pytorch.org/whl/cu128
    } else {
        Set-SetupStatus 'installing' 'Installing CPU PyTorch...'
        & $workerPython -m pip install --disable-pip-version-check 'torch==2.8.0+cpu' --index-url https://download.pytorch.org/whl/cpu
    }
    if ($LASTEXITCODE -ne 0) { throw 'PyTorch installation failed. Check network access, disk space and Python version.' }
    Set-SetupStatus 'installing' 'Installing Laya SDK...'
    & $workerPython -m pip install --disable-pip-version-check -r (Join-Path $inference 'requirements.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Laya dependencies failed to install.' }
    Set-SetupStatus 'downloading' 'Downloading Laya multilingual 322M (about 644 MB; runtime packages are additional)...'
    & $workerPython -u (Join-Path $inference 'download_model.py') --model multilingual
    if ($LASTEXITCODE -ne 0) { throw 'Model download failed. Run setup again to resume.' }
    Set-SetupStatus 'ready' "Laya $Model is installed. Enable Laya in EmotionCat settings."
    exit 0
} catch {
    Set-SetupStatus 'error' $_.Exception.Message
    exit 1
}
