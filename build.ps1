$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path $compiler
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectDir 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$compilerArgs = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/utf8output', '/codepage:65001', ('/out:' + (Join-Path $projectDir 'EmotionCat.exe')), ('/win32manifest:' + (Join-Path $projectDir 'app.manifest')), '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll', '/reference:System.Net.Http.dll', ('/reference:' + (Join-Path $framework 'WPF\UIAutomationClient.dll')), ('/reference:' + (Join-Path $framework 'WPF\UIAutomationTypes.dll')), ('/reference:' + (Join-Path $framework 'WPF\WindowsBase.dll')))
$iconFile = Join-Path $projectDir 'assets\emotioncat.ico'
if (Test-Path -LiteralPath $iconFile) { $compilerArgs += ('/win32icon:' + $iconFile) }
& $compiler @compilerArgs @sourceFiles
if ($LASTEXITCODE -ne 0) { throw 'EmotionCat build failed.' }
Write-Host 'Built EmotionCat.exe'
