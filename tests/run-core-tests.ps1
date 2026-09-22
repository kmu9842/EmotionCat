param([switch]$Integration)

$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) {
    throw '.NET Framework 4.8 C# compiler was not found.'
}

# Integration uses the real local worker and must resolve inference/ from app root.
$testExecutable = Join-Path $projectDirectory ('EmotionCat.CoreTests.' + [Guid]::NewGuid().ToString('N') + '.exe')
try {
    & $compiler /nologo /target:exe /platform:x64 "/out:$testExecutable" /r:System.Web.Extensions.dll /r:System.Drawing.dll `
        (Join-Path $projectDirectory 'src\AppSettings.cs') `
        (Join-Path $projectDirectory 'src\LayaClient.cs') `
        (Join-Path $PSScriptRoot 'CoreTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Core test compilation failed.' }
    if ($Integration) { & $testExecutable --integration }
    else { & $testExecutable }
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
}
finally {
    if (Test-Path -LiteralPath $testExecutable) { Remove-Item -LiteralPath $testExecutable -Force }
}
