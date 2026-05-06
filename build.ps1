$ErrorActionPreference = 'Stop'

$source = Join-Path $PSScriptRoot 'src\NeutriverseWriter.cs'
$outDir = 'D:\Cyberia Cafe\Neutriverse Writer'
$outFile = Join-Path $outDir 'NeutriverseWriter.exe'

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$code = Get-Content $source -Raw -Encoding utf8
Add-Type `
  -TypeDefinition $code `
  -ReferencedAssemblies System.Windows.Forms,System.Drawing,System,System.Core `
  -OutputAssembly $outFile `
  -OutputType WindowsApplication

Write-Host "Built $outFile"
