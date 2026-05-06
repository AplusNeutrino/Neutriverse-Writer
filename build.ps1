$ErrorActionPreference = 'Stop'

$source = Join-Path $PSScriptRoot 'src\NeutriverseWriter.cs'
$icon = Join-Path $PSScriptRoot 'assets\app.ico'
$assetSource = Join-Path $PSScriptRoot 'assets'
$outDir = 'D:\Cyberia Cafe\Neutriverse Writer'
$outFile = Join-Path $outDir 'NeutriverseWriter.exe'
$assetOut = Join-Path $outDir 'assets'

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
New-Item -ItemType Directory -Force -Path $assetOut | Out-Null

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
  $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
  throw 'Could not find csc.exe from the .NET Framework runtime.'
}

& $csc `
  /nologo `
  /target:winexe `
  /codepage:65001 `
  /out:$outFile `
  /win32icon:$icon `
  /reference:System.Windows.Forms.dll `
  /reference:System.Drawing.dll `
  /reference:System.dll `
  /reference:System.Core.dll `
  $source
if ($LASTEXITCODE -ne 0) {
  throw "csc.exe failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath (Join-Path $assetSource 'app.ico') -Destination (Join-Path $assetOut 'app.ico') -Force
Copy-Item -LiteralPath (Join-Path $assetSource 'logo.png') -Destination (Join-Path $assetOut 'logo.png') -Force

Write-Host "Built $outFile"
