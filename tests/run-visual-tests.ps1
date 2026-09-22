$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path $compiler
$testExe = Join-Path $projectDir 'EmotionCat.VisualTests.exe'
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectDir 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$compileArgs = @('/nologo','/target:exe','/platform:x64','/utf8output','/codepage:65001','/main:EmotionCat.VisualTests',('/out:'+$testExe),'/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll',('/reference:'+(Join-Path $framework 'WPF\UIAutomationClient.dll')),('/reference:'+(Join-Path $framework 'WPF\UIAutomationTypes.dll')),('/reference:'+(Join-Path $framework 'WPF\WindowsBase.dll')))
& $compiler @compileArgs @sourceFiles (Join-Path $PSScriptRoot 'VisualTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Visual tests compile failed.' }
try { & $testExe; if ($LASTEXITCODE -ne 0) { throw 'Visual tests failed.' } }
finally { if (Test-Path -LiteralPath $testExe) { Remove-Item -LiteralPath $testExe } }
