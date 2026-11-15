$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $Root

Write-Host "==> Testler calistiriliyor..."
dotnet test EkranKaydedici.sln -c Release

Write-Host "==> EkranKaydedici.exe derleniyor..."
$PublishDir = Join-Path $Root "publish-win-x64"
dotnet publish EkranKaydedici\EkranKaydedici.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o $PublishDir

$BundleDir = Join-Path $Root "dist\EkranKaydedici-Windows-x64"
New-Item -ItemType Directory -Path $BundleDir -Force | Out-Null
Copy-Item (Join-Path $PublishDir "EkranKaydedici.exe") (Join-Path $BundleDir "EkranKaydedici.exe") -Force

$FfmpegPath = (Get-Command ffmpeg.exe -ErrorAction SilentlyContinue).Source
if (-not $FfmpegPath) {
    Write-Host "==> ffmpeg.exe indiriliyor..."
    $ZipPath = Join-Path $Root "ffmpeg-tmp.zip"
    $ExtractDir = Join-Path $Root "ffmpeg-tmp"
    Invoke-WebRequest -Uri "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip" -OutFile $ZipPath
    Expand-Archive -Path $ZipPath -DestinationPath $ExtractDir -Force
    $FfmpegExe = Get-ChildItem -Path $ExtractDir -Recurse -Filter "ffmpeg.exe" | Select-Object -First 1
    if ($FfmpegExe) {
        Copy-Item $FfmpegExe.FullName (Join-Path $BundleDir "ffmpeg.exe") -Force
    }
    Remove-Item $ZipPath, $ExtractDir -Recurse -Force -ErrorAction SilentlyContinue
} else {
    Copy-Item $FfmpegPath (Join-Path $BundleDir "ffmpeg.exe") -Force
}

$OutZip = Join-Path $Root "dist\EkranKaydedici-Windows-x64.zip"
Compress-Archive -Path "$BundleDir\*" -DestinationPath $OutZip -Force
Write-Host "==> Hazir: $OutZip"
