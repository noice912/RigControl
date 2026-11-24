# Fills third_party\openrgb, third_party\pawnio and third_party\equalizerapo from the official installers, which
# RigControl then carries inside its exe (see third_party\BUNDLED.md). The binaries are not kept in git.
#
# Usage:  .\prepare-bundled.ps1 [-From <folder with the downloaded installers>]   (default: your Downloads folder)
#
# Each installer must match the SHA-256 pinned below, or nothing is copied. To move to a newer version, download the
# official file, check it, and update its name and hash here.
param([string]$From = (Join-Path $env:USERPROFILE 'Downloads'))
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

$pinned = @(
    @{ Name = 'OpenRGB_1.1_Windows_64_8e16de2.msi'; Sha = 'A4CE48A5E1532750AD4F5248424D42F7F2C710775CEE88DB070CCDB63C549CAA'; Source = 'https://openrgb.org/releases.html' },
    @{ Name = 'PawnIO_setup.exe';                   Sha = '1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032'; Source = 'https://pawnio.eu/' },
    @{ Name = 'EqualizerAPO-x64-1.4.2.exe';         Sha = '7403BE7427BBE1936A40DDED082829B6E217FC4F5990FEE5CBA501F0AE055AFA'; Source = 'https://sourceforge.net/projects/equalizerapo/' }
)

function Find-Installer($name) {
    $hit = Get-ChildItem $From -Recurse -Depth 2 -File -Filter $name -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $hit) { throw "$name not found under $From. Download it from its official page and run this again." }
    return $hit.FullName
}

$files = @{}
foreach ($p in $pinned) {
    $path = Find-Installer $p.Name
    $sha = (Get-FileHash $path -Algorithm SHA256).Hash
    if ($sha -ne $p.Sha) { throw "$($p.Name) does not match its pinned SHA-256 (got $sha). Not copied." }
    $files[$p.Name] = $path
    Write-Host "OK  $($p.Name)"
}

# OpenRGB: unpack the official MSI (an administrative image: no install, no admin rights) and keep its program folder.
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('rigcontrol-openrgb-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
try {
    $p = Start-Process msiexec.exe -ArgumentList @('/a', "`"$($files['OpenRGB_1.1_Windows_64_8e16de2.msi'])`"", '/qn', "TARGETDIR=`"$tmp`"") -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "Unpacking the OpenRGB MSI failed (msiexec exit code $($p.ExitCode))." }
    $exe = Get-ChildItem $tmp -Recurse -File -Filter OpenRGB.exe | Select-Object -First 1
    if (-not $exe) { throw 'OpenRGB.exe was not in the MSI.' }
    $target = Join-Path $here 'openrgb\files'
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    New-Item -ItemType Directory $target | Out-Null
    Copy-Item (Join-Path $exe.DirectoryName '*') $target -Recurse -Force
    # Leftover copy of the MSI that /a writes next to the files: not needed.
    Get-ChildItem $target -Filter *.msi -Recurse | Remove-Item -Force
    Write-Host ("OpenRGB: {0} files, {1:N1} MB" -f (Get-ChildItem $target -Recurse -File).Count, ((Get-ChildItem $target -Recurse -File | Measure-Object Length -Sum).Sum / 1MB))
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory (Join-Path $here 'pawnio') -Force | Out-Null
Copy-Item $files['PawnIO_setup.exe'] (Join-Path $here 'pawnio\PawnIO_setup.exe') -Force
New-Item -ItemType Directory (Join-Path $here 'equalizerapo') -Force | Out-Null
Copy-Item $files['EqualizerAPO-x64-1.4.2.exe'] (Join-Path $here 'equalizerapo\EqualizerAPO-x64-1.4.2.exe') -Force
Write-Host 'Done. Publish RigControl to carry them inside the exe.'
