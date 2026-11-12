param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [switch]$NoPublish
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src/SeerHover/SeerHover.csproj"
$dist = Join-Path $root "dist"

Write-Host "Building SeerHover $Version ..."
& dotnet build $proj -c $Configuration -p:SeerHoverVersion=$Version -warnaserror
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

if (-not $NoPublish) {
    Write-Host "Publishing single-file exe to $dist ..."
    & dotnet publish $proj -c $Configuration -r win-x64 --self-contained false `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none -p:SeerHoverVersion=$Version -o $dist
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

    $exe = Join-Path $dist "SeerHover.exe"
    $size = (Get-Item $exe).Length
    Write-Host ("Output: {0} ({1:N0} bytes)" -f $exe, $size)
}
