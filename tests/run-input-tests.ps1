$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = Split-Path -Parent $compiler
$testOutputDir = Join-Path $projectDir '.test-output'
New-Item -ItemType Directory -Path $testOutputDir -Force | Out-Null
$testExe = Join-Path $testOutputDir 'EmotionCat.InputTests.exe'
$compilerArgs = @(
    '/nologo', '/target:exe', '/platform:x64', '/optimize+', '/utf8output', '/codepage:65001',
    ('/out:' + $testExe), '/reference:System.dll', '/reference:System.Core.dll',
    ('/reference:' + (Join-Path $framework 'WPF\UIAutomationClient.dll')),
    ('/reference:' + (Join-Path $framework 'WPF\UIAutomationTypes.dll')),
    ('/reference:' + (Join-Path $framework 'WPF\WindowsBase.dll')),
    (Join-Path $projectDir 'src\InputMonitor.cs'),
    (Join-Path $projectDir 'src\TypedTextBuffer.cs'),
    (Join-Path $PSScriptRoot 'InputTests.cs')
)
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Input regression tests did not compile.' }
& $testExe
if ($LASTEXITCODE -ne 0) { throw 'Input regression tests failed.' }
