# Build-time dependency preparation. The portable app does not require Python/NuGet.
param([string]$Python = 'python')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $root 'build\windows-dependencies'
New-Item -ItemType Directory -Force -Path $cache,(Join-Path $root 'model'),(Join-Path $root 'licenses') | Out-Null

function Assert-Hash([string]$Path, [string]$Expected) {
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Expected) { throw "Checksum mismatch: $Path" }
}
function Fetch([string]$Url, [string]$Path, [string]$Hash) {
    if (-not (Test-Path -LiteralPath $Path)) { Invoke-WebRequest -UseBasicParsing $Url -OutFile $Path }
    Assert-Hash $Path $Hash
}
function Fetch-Asset([string]$Tag, [string]$Name, [string]$Path, [string]$Hash) {
    if (-not (Test-Path -LiteralPath $Path)) {
        $repo = if ($env:GH_REPO) { $env:GH_REPO } else { 'kmu9842/EmotionCat' }
        $release = gh api "repos/$repo/releases/tags/$Tag" | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0) { throw 'Release lookup failed.' }
        $assets = gh api "repos/$repo/releases/$($release.id)/assets?per_page=100" | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0) { throw 'Asset lookup failed.' }
        $asset = @($assets | Where-Object { $_.name -eq $Name -and $_.state -eq 'uploaded' })
        if ($asset.Count -ne 1) { throw "Missing or ambiguous release asset: $Name" }
        $info = New-Object Diagnostics.ProcessStartInfo
        $info.FileName = (Get-Command gh).Source
        $info.Arguments = 'api repos/' + $repo + '/releases/assets/' + $asset[0].id + ' -H "Accept: application/octet-stream"'
        $info.UseShellExecute = $false; $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
        $download = New-Object Diagnostics.Process; $download.StartInfo = $info
        [void]$download.Start(); $errors = $download.StandardError.ReadToEndAsync()
        $stream = [IO.File]::Create($Path)
        try { $download.StandardOutput.BaseStream.CopyTo($stream) } finally { $stream.Dispose() }
        $download.WaitForExit()
        if ($download.ExitCode -ne 0) { throw $errors.Result }
    }
    Assert-Hash $Path $Hash
}

$ortZip = Join-Path $cache 'onnxruntime-directml-1.24.4.zip'
$dmlZip = Join-Path $cache 'directml-1.15.4.zip'
Fetch 'https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime.directml/1.24.4/microsoft.ml.onnxruntime.directml.1.24.4.nupkg' $ortZip '57e9f11b73437bef7a309496135d4c1f96b1a8e9ddba60013fa27bfc1d788681'
Fetch 'https://api.nuget.org/v3-flatcontainer/microsoft.ai.directml/1.15.4/microsoft.ai.directml.1.15.4.nupkg' $dmlZip '4e7cb7ddce8cf837a7a75dc029209b520ca0101470fcdf275c1f49736a3615b9'
Expand-Archive -LiteralPath $ortZip -DestinationPath (Join-Path $cache 'ort') -Force
Expand-Archive -LiteralPath $dmlZip -DestinationPath (Join-Path $cache 'dml') -Force
Copy-Item -LiteralPath (Join-Path $cache 'ort\runtimes\win-x64\native\onnxruntime.dll') -Destination $root
Copy-Item -LiteralPath (Join-Path $cache 'dml\bin\x64-win\DirectML.dll') -Destination $root
Copy-Item -LiteralPath (Join-Path $cache 'ort\LICENSE') -Destination (Join-Path $root 'licenses\ONNXRuntime-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $cache 'ort\ThirdPartyNotices.txt') -Destination (Join-Path $root 'licenses\ONNXRuntime-ThirdPartyNotices.txt')
Copy-Item -LiteralPath (Join-Path $cache 'dml\LICENSE.txt') -Destination (Join-Path $root 'licenses\DirectML-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $cache 'dml\ThirdPartyNotices.txt') -Destination (Join-Path $root 'licenses\DirectML-ThirdPartyNotices.txt')
Fetch-Asset 'model-v1' 'laya-multilingual-fp32.onnx' (Join-Path $cache 'laya-multilingual-fp32.onnx') '64d49d6850ae48c8c5065291055ba3f2cb4eb72f4caa3fb5115c2141ea8f9db0'
Fetch-Asset 'model-v2' 'tokenizer.bin' (Join-Path $root 'model\tokenizer.bin') '32fa4053b15e4ea0791340f1092e93eb682b158d7c35972c933f46ab03bc22b4'
Fetch-Asset 'model-v2' 'Laya-APACHE-2.0.txt' (Join-Path $root 'licenses\Laya-APACHE-2.0.txt') 'a6cba85bc92e0cff7a450b1d873c0eaa2e9fc96bf472df0247a26bec77bf3ff9'
$venv = Join-Path $cache 'python'
if (-not (Test-Path -LiteralPath (Join-Path $venv 'Scripts\python.exe'))) {
    & $Python -m venv $venv
    if ($LASTEXITCODE -ne 0) { throw 'Build Python environment creation failed.' }
}
$buildPython = Join-Path $venv 'Scripts\python.exe'
& $buildPython -m pip install --disable-pip-version-check -r (Join-Path $root 'tools\onnx\requirements-directml.txt')
if ($LASTEXITCODE -ne 0) { throw 'Build dependency installation failed.' }
& $buildPython (Join-Path $root 'tools\onnx\prepare_directml.py') --src (Join-Path $cache 'laya-multilingual-fp32.onnx') --out (Join-Path $root 'model\laya-multilingual-gpu.onnx')
if ($LASTEXITCODE -ne 0) { throw 'GPU model preparation failed.' }
Write-Output 'Prepared GPU-only Windows runtime and model.'
