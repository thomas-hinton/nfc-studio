$ErrorActionPreference = 'Stop'

$compilerCandidates = @(
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe',
    'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
)

$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) {
    throw 'Compilateur .NET Framework csc.exe introuvable.'
}

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $root 'src\NfcStudio.cs'
$outputDirectory = Join-Path $root 'dist'
$output = Join-Path $outputDirectory 'NfcStudio.exe'

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

& $compiler /nologo /target:winexe /platform:anycpu /optimize+ `
    /out:$output `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    $source

if ($LASTEXITCODE -ne 0) {
    throw "La compilation a échoué avec le code $LASTEXITCODE."
}

Write-Host "Compilation réussie : $output"

