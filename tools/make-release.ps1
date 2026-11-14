<#
  打包发布资产。
  用法: powershell -NoProfile -File tools\make-release.ps1 -Version 0.1.0
  产物: dist\WujieFan-<ver>-win-x64.zip            (依赖框架, 需 .NET 8 桌面运行时)
        dist\WujieFan-<ver>-win-x64-portable.zip   (自包含单文件, 约 62 MB)
#>
param([string]$Version = "0.1.0")

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root  = Split-Path -Parent $PSScriptRoot
$dist  = Join-Path $root "dist"
$proj  = Join-Path $root "src\WujieFan\WujieFan.csproj"
$fdDir = Join-Path $dist "app"
$scDir = Join-Path $dist "portable"

# 目标 exe 正在运行时 dotnet 会静默跳过拷贝 (MSB3027), 先停掉。
# 它是提权进程, 普通权限停不掉 -> 明确报错, 不要静默产出旧文件。
Get-Process WujieFan -ErrorAction SilentlyContinue | ForEach-Object {
  try { $_.Kill(); $_.WaitForExit(5000) } catch { }
}
Start-Sleep -Seconds 2
if (Get-Process WujieFan -ErrorAction SilentlyContinue) {
  throw "WujieFan is still running and cannot be stopped (it is elevated). Close it from the tray menu, or run this script as Administrator."
}

Write-Host "[1/4] publish framework-dependent"
dotnet publish $proj -c Release -o $fdDir | Out-Host
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Write-Host "[2/4] publish self-contained single-file"
dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $scDir | Out-Host
if ($LASTEXITCODE -ne 0) { throw "portable publish failed" }

function Add-Entry($zip, $file, $entryName) {
  [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
    $zip, $file.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal)
}

function New-Pkg([string]$Dest, [string]$BinDir, [string]$SingleExe) {
  if (Test-Path $Dest) { Remove-Item $Dest -Force }
  $zip = [System.IO.Compression.ZipFile]::Open($Dest, "Create")
  if ($SingleExe) {
    Add-Entry $zip (Get-Item $SingleExe) ("WujieFan/" + (Split-Path $SingleExe -Leaf))
  } else {
    Get-ChildItem $BinDir -File | Where-Object { $_.Extension -ne ".pdb" } | ForEach-Object {
      Add-Entry $zip $_ ("WujieFan/" + $_.Name)
    }
  }
  Get-ChildItem (Join-Path $root "tools") -File | Where-Object { $_.Name -ne "make-release.ps1" } | ForEach-Object {
    Add-Entry $zip $_ ("WujieFan/tools/" + $_.Name)
  }
  Add-Entry $zip (Get-Item (Join-Path $root "README.md"))     "WujieFan/README.md"
  Add-Entry $zip (Get-Item (Join-Path $root "THIRDPARTY.md")) "WujieFan/THIRDPARTY.md"
  Add-Entry $zip (Get-Item (Join-Path $root "LICENSE"))       "WujieFan/LICENSE"
  $zip.Dispose()
}

Write-Host "[3/4] pack framework-dependent"
$fdZip = Join-Path $dist ("WujieFan-" + $Version + "-win-x64.zip")
New-Pkg $fdZip $fdDir $null

Write-Host "[4/4] pack portable"
$scZip = Join-Path $dist ("WujieFan-" + $Version + "-win-x64-portable.zip")
New-Pkg $scZip $null (Join-Path $scDir "WujieFan.exe")

foreach ($z in @($fdZip, $scZip)) {
  $a = [System.IO.Compression.ZipFile]::OpenRead($z)
  "{0}  {1:N0} B  {2} entries" -f (Split-Path $z -Leaf), (Get-Item $z).Length, $a.Entries.Count
  $a.Dispose()
}

Write-Host "done. remember to restart WujieFan if you want it running again."