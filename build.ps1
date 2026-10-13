param([string]$OutputName = 'Snapline.exe')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path $compiler)) { throw '.NET Framework 4.x is required.' }
New-Item -ItemType Directory -Force -Path (Join-Path $root 'dist') | Out-Null
& $compiler /nologo /target:winexe /optimize+ /platform:anycpu /win32manifest:"$root\src\app.manifest" /out:"$root\dist\$outputName" /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Core.dll "$root\src\Snapline.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item -LiteralPath (Join-Path $root 'src\Snapline.exe.config') -Destination (Join-Path $root "dist\$OutputName.config") -Force
Write-Host "Built $root\dist\$outputName"
