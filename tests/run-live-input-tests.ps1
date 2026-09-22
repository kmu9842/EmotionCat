$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$exe = Join-Path $root 'EmotionCat.LiveInputTests.exe'
New-Item -ItemType Directory -Force -Path (Split-Path $exe) | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
& (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /codepage:65001 /utf8output /main:LiveInputTests "/out:$exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll "/r:$framework\WPF\UIAutomationClient.dll" "/r:$framework\WPF\UIAutomationTypes.dll" "/r:$framework\WPF\WindowsBase.dll" @sources (Join-Path $PSScriptRoot 'LiveInputTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Fixture compile failed.' }
try { & $exe; if ($LASTEXITCODE -ne 0) { throw 'Live fixture test failed.' } }
finally { Remove-Item -LiteralPath $exe -ErrorAction SilentlyContinue }
