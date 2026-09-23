$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe = Join-Path $root 'EmotionCat.GpuTests.exe'
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 /utf8output "/out:$exe" "/r:$root\EmotionCat.exe" /r:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'GpuTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'GPU test compilation failed.' }
Push-Location $root
try { & $exe; if ($LASTEXITCODE -ne 0) { throw 'GPU tests failed.' } }
finally { Pop-Location; Remove-Item -LiteralPath $exe -ErrorAction SilentlyContinue }
