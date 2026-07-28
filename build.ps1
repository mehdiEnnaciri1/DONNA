# Publie DONNA en single-file self-contained, puis compile l'installeur Inno
# Setup. Voir ARCHITECTURE.md §11.
#
# Prérequis : SDK .NET 10 (dotnet) + Inno Setup 6 (ISCC.exe).

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

Write-Host "== Publication de Donna.exe (Release, win-x64, single-file, self-contained) =="
dotnet publish "$root\Donna\Donna.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true

Write-Host "== Recherche d'ISCC.exe (Inno Setup) =="
$iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
if (-not $iscc) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    $found = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($found) {
        $iscc = Get-Item $found
    } else {
        throw "ISCC.exe (Inno Setup) introuvable. Installe Inno Setup 6 : https://jrsoftware.org/isdl.php"
    }
}
else {
    $iscc = Get-Item $iscc.Source
}

Write-Host "== Compilation de l'installeur =="
& $iscc.FullName "$root\installer\donna.iss"

Write-Host "== Terminé : dist\Donna-Setup.exe =="
