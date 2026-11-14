<#
  打包发布资产。
  用法: powershell -NoProfile -File tools\make-release.ps1 -Version 0.2.0
  产物: dist\WujieFan-<ver>-win-x64.zip            (依赖框架, 需 .NET 8 桌面运行时)
        dist\WujieFan-<ver>-win-x64-portable.zip   (自包含单文件)

  注意: 从 0.2.0 起有两个 exe。
  WujieFanLauncher.exe 是开机自启动链的第一跳 (普通权限), 由它经 UAC 提权拉起
  WujieFan.exe。两者必须同目录, 少一个自启动就会在 Enable() 时明确报错。
#>
param([string]$Version = "0.2.0")

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root  = Split-Path -Parent $PSScriptRoot
$dist  = Join-Path $root "dist"
$proj  = Join-Path $root "src\WujieFan\WujieFan.csproj"
$lproj = Join-Path $root "src\WujieFan.Launcher\WujieFan.Launcher.csproj"
$fdDir = Join-Path $dist "app"
$scDir = Join-Path $dist "portable"
$lsDir = Join-Path $dist "launcher"

# 目标 exe 正在运行时 dotnet 会静默跳过拷贝 (MSB3027), 先停掉。
# 它是提权进程, 普通权限停不掉 -> 明确报错, 不要静默产出旧文件。
Get-Process WujieFan -ErrorAction SilentlyContinue | ForEach-Object {
  try { $_.Kill(); $_.WaitForExit(5000) } catch { }
}
Start-Sleep -Seconds 2
if (Get-Process WujieFan -ErrorAction SilentlyContinue) {
  throw "WujieFan is still running and cannot be stopped (it is elevated). Close it from the tray menu, or run this script as Administrator."
}

Write-Host "[1/5] publish main app (framework-dependent)"
dotnet publish $proj -c Release -o $fdDir | Out-Host
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Write-Host "[2/5] publish launcher (framework-dependent, same dir)"
dotnet publish $lproj -c Release -o $fdDir | Out-Host
if ($LASTEXITCODE -ne 0) { throw "launcher publish failed" }

Write-Host "[3/5] publish main app (self-contained single-file)"
dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $scDir | Out-Host
if ($LASTEXITCODE -ne 0) { throw "portable publish failed" }

# 便携包用户机器上可能没有 .NET, 启动器也必须自带运行时, 否则自启动直接挂。
Write-Host "[4/5] publish launcher (self-contained single-file)"
dotnet publish $lproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $lsDir | Out-Host
if ($LASTEXITCODE -ne 0) { throw "portable launcher publish failed" }

function Add-Entry($zip, $file, $entryName) {
  [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
    $zip, $file.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal)
}

function Add-Docs($zip) {
  Get-ChildItem (Join-Path $root "tools") -File | Where-Object { $_.Name -ne "make-release.ps1" } | ForEach-Object {
    Add-Entry $zip $_ ("WujieFan/tools/" + $_.Name)
  }
  Add-Entry $zip (Get-Item (Join-Path $root "README.md"))     "WujieFan/README.md"
  Add-Entry $zip (Get-Item (Join-Path $root "THIRDPARTY.md")) "WujieFan/THIRDPARTY.md"
  Add-Entry $zip (Get-Item (Join-Path $root "LICENSE"))       "WujieFan/LICENSE"
}

function New-FrameworkPkg([string]$Dest, [string]$BinDir) {
  if (Test-Path $Dest) { Remove-Item $Dest -Force }
  $zip = [System.IO.Compression.ZipFile]::Open($Dest, "Create")
  Get-ChildItem $BinDir -File | Where-Object { $_.Extension -ne ".pdb" } | ForEach-Object {
    Add-Entry $zip $_ ("WujieFan/" + $_.Name)
  }
  Add-Docs $zip
  $zip.Dispose()
}

function New-PortablePkg([string]$Dest, [string]$MainExe, [string]$LauncherExe) {
  if (Test-Path $Dest) { Remove-Item $Dest -Force }
  $zip = [System.IO.Compression.ZipFile]::Open($Dest, "Create")
  Add-Entry $zip (Get-Item $MainExe) ("WujieFan/" + (Split-Path $MainExe -Leaf))
  Add-Entry $zip (Get-Item $LauncherExe) ("WujieFan/" + (Split-Path $LauncherExe -Leaf))
  Add-Docs $zip
  $zip.Dispose()
}

Write-Host "[5/5] pack"
$fdZip = Join-Path $dist ("WujieFan-" + $Version + "-win-x64.zip")
New-FrameworkPkg $fdZip $fdDir

$scZip = Join-Path $dist ("WujieFan-" + $Version + "-win-x64-portable.zip")
New-PortablePkg $scZip (Join-Path $scDir "WujieFan.exe") (Join-Path $lsDir "WujieFanLauncher.exe")

foreach ($z in @($fdZip, $scZip)) {
  $a = [System.IO.Compression.ZipFile]::OpenRead($z)
  "{0}  {1:N0} B  {2} entries" -f (Split-Path $z -Leaf), (Get-Item $z).Length, $a.Entries.Count
  $a.Dispose()
}

Write-Host "done. remember to restart WujieFan if you want it running again."
