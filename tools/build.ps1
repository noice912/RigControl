# Builds RigControl.exe: one self-contained file with the runtime, the interface and the audio converter inside.
#
#   tools\build.ps1                    the exe goes to <repository>\artifacts\app
#   tools\build.ps1 -Output <folder>   the exe goes to <folder>
#
# Needs the .NET 8 SDK and Node.js (npm). It only builds: it never installs, starts or stops anything.
# Close RigControl first if the output folder holds the copy that is running.
param([string]$Output)
$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($Output)) { $Output = Join-Path $repo 'artifacts\app' }
$Output = [IO.Path]::GetFullPath($Output)
$exe = Join-Path $Output 'RigControl.exe'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET SDK (dotnet) was not found. Install the .NET 8 SDK first.' }
if (-not (Get-Command npm -ErrorAction SilentlyContinue)) { throw 'npm was not found. Install Node.js first.' }

if (Test-Path -LiteralPath $exe) {
    # A running exe can't be overwritten. Say so now instead of failing at the end of a long build.
    try { [IO.File]::Open($exe, 'Open', 'ReadWrite', 'None').Dispose() }
    catch { throw "RigControl.exe in $Output is in use. Close RigControl first, or choose another folder with -Output." }
}

Push-Location $repo
try {
    if (-not (Test-Path 'src\ui\node_modules')) {
        Push-Location 'src\ui'
        try {
            npm ci --no-audit --no-fund
            if ($LASTEXITCODE -ne 0) { throw 'Installing the web interface packages failed (npm ci).' }
        }
        finally { Pop-Location }
    }
    dotnet publish 'src\RigControl.App\RigControl.App.csproj' -c Release -o $Output -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }
}
finally { Pop-Location }

# Everything the app needs is inside the exe; remove the loose debug and documentation files.
Get-ChildItem -LiteralPath $Output -File | Where-Object { $_.Extension -in '.pdb', '.xml' } | Remove-Item -Force

Get-Item -LiteralPath $exe | Select-Object FullName, @{ n = 'SizeMB'; e = { [int]($_.Length / 1MB) } }, LastWriteTime
