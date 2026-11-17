<#
  打包发布资产。
  用法: powershell -NoProfile -File tools\make-release.ps1 -Version 0.4.0
        -Version 是必填项, 且必须与仓库根 Directory.Build.props 里的
        <Version> 完全一致 (脚本会校验, 不一致直接报错)。
  产物: dist\WujieFan-<ver>-win-x64.zip            (依赖框架, 需 .NET 8 桌面运行时)
        dist\WujieFan-<ver>-win-x64-portable.zip   (自包含单文件)

  注意: 从 0.2.0 起有两个 exe。
  WujieFanLauncher.exe 是开机自启动链的第一跳 (普通权限), 由它经 UAC 提权拉起
  WujieFan.exe。两者必须同目录, 少一个自启动就会在 Enable() 时明确报错。
#>
param([string]$Version)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$script:Usage = "usage: powershell -NoProfile -File tools\make-release.ps1 -Version <x.y.z>"

# 版本号曾在这里有个 0.2.0 的默认值, 漏传时就会静默打出与源码版本不符的包名
# (t5 P0-3)。现在改为必填: 缺失即报错, 不再有"猜一个默认值"的路径。
if ([string]::IsNullOrWhiteSpace($Version)) {
  throw "-Version is required and must match Directory.Build.props.$([Environment]::NewLine)$($script:Usage)"
}

$root  = Split-Path -Parent $PSScriptRoot
$dist  = Join-Path $root "dist"
$proj  = Join-Path $root "src\WujieFan\WujieFan.csproj"
$lproj = Join-Path $root "src\WujieFan.Launcher\WujieFan.Launcher.csproj"
$fdDir = Join-Path $dist "app"
$scDir = Join-Path $dist "portable"
$lsDir = Join-Path $dist "launcher"

# 版本号只在 Directory.Build.props 里定义一处, 三个项目 (WujieFan / Core / Launcher)
# 都由 MSBuild 自动继承。这里校验命令行参数与它一致, 避免包名 0.4.0、程序集 0.4.0、
# 而 zip 里却是别的东西这类对不上的事故 (t5 P0-1/P0-2)。
$propsPath = Join-Path $root "Directory.Build.props"
if (-not (Test-Path $propsPath)) {
  throw "Directory.Build.props not found at $propsPath. It is the single source of truth for the version."
}
[xml]$props = Get-Content -Path $propsPath -Raw
$propsVersion = $null
foreach ($group in $props.Project.PropertyGroup) {
  if ($null -ne $group.Version -and "$($group.Version)".Trim() -ne "") {
    $propsVersion = "$($group.Version)".Trim()
    break
  }
}
if ([string]::IsNullOrWhiteSpace($propsVersion)) {
  throw "Directory.Build.props has no <Version> element. Add one (it is inherited by every project)."
}
if ($Version.Trim() -ne $propsVersion) {
  throw "Version mismatch: -Version '$Version' but Directory.Build.props says '$propsVersion'.$([Environment]::NewLine)Update Directory.Build.props first, then pass the same value.$([Environment]::NewLine)$($script:Usage)"
}
$Version = $propsVersion

# 目标 exe 正在运行时 dotnet 会静默跳过拷贝 (MSB3027), 先停掉。
# 它是提权进程, 普通权限停不掉 -> 明确报错, 不要静默产出旧文件。
Get-Process WujieFan -ErrorAction SilentlyContinue | ForEach-Object {
  try { $_.Kill(); $_.WaitForExit(5000) } catch { }
}
Start-Sleep -Seconds 2
if (Get-Process WujieFan -ErrorAction SilentlyContinue) {
  throw "WujieFan is still running and cannot be stopped (it is elevated). Close it from the tray menu, or run this script as Administrator."
}

# dotnet publish 是合并输出, 不会删除上一次的残留。dist\app 里若还躺着旧版本
# (或旧依赖) 的 DLL, 会被原样打进 zip (t5 P1-4)。发布前先整体清空, 保证产物
# 只包含本次 publish 写出的文件。
if (Test-Path $fdDir) {
  Write-Host "[0/5] clear stale output: $fdDir"
  Remove-Item $fdDir -Recurse -Force
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

# 生成启动器在开机自启前比对的完整性清单 (t11)。
#
# 为什么必须有这一步: WujieFanLauncher.exe 每次被计划任务拉起时都会读同目录的
# WujieFan.integrity.json 并比对 WujieFan.exe / WujieFan.Core.dll 的 SHA-256 与
# 长度。没有这个文件, 校验会落到 PinsMissing 分支, 只写日志不拦人 —— 等于校验
# 形同不存在。所以清单必须在打包阶段生成, 而不是靠用户或开发者手工维护。
#
# 局限（不要被它误导）: 清单和 exe 在同一个目录里, 能替换 exe 的人也能一起替换
# 清单。它是"检测"不是"防御", 真正的修复是给 exe 做代码签名。
#
# 只收录真实存在的文件: 便携包是单文件发布, WujieFan.Core.dll 被并进主 exe,
# 磁盘上并没有这个文件, 硬写进清单会让每个便携包用户每次开机都看到误报。
function New-IntegrityManifest([string]$Dir, [string[]]$Names) {
  $files = [ordered]@{}
  foreach ($n in $Names) {
    $p = Join-Path $Dir $n
    if (-not (Test-Path $p)) { continue }
    $hash = (Get-FileHash -Path $p -Algorithm SHA256).Hash.ToLowerInvariant()
    $len  = (Get-Item $p).Length
    $files[$n] = [ordered]@{ sha256 = $hash; length = $len }
  }
  if ($files.Count -eq 0) {
    throw "no files to pin in $Dir (refusing to write an empty integrity manifest)"
  }

  $doc = [ordered]@{ files = $files }
  $json = $doc | ConvertTo-Json -Depth 6 -Compress:$false
  $out = Join-Path $Dir "WujieFan.integrity.json"
  [System.IO.File]::WriteAllText($out, $json, (New-Object System.Text.UTF8Encoding($false)))
  Write-Host ("      integrity manifest: {0} ({1} files)" -f $out, $files.Count)
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

function New-PortablePkg([string]$Dest, [string]$MainExe, [string]$LauncherExe, [string]$Manifest) {
  if (Test-Path $Dest) { Remove-Item $Dest -Force }
  $zip = [System.IO.Compression.ZipFile]::Open($Dest, "Create")
  Add-Entry $zip (Get-Item $MainExe) ("WujieFan/" + (Split-Path $MainExe -Leaf))
  Add-Entry $zip (Get-Item $LauncherExe) ("WujieFan/" + (Split-Path $LauncherExe -Leaf))
  # 便携包的主 exe 与启动器分别来自两个 publish 目录, 不能靠"目录里有什么就打什么",
  # 清单必须显式加进去, 否则启动器在用户机器上永远读不到它。
  Add-Entry $zip (Get-Item $Manifest) "WujieFan/WujieFan.integrity.json"
  Add-Docs $zip
  $zip.Dispose()
}

Write-Host "[5/5] pack"
$fdZip = Join-Path $dist ("WujieFan-" + $Version + "-win-x64.zip")

# 框架依赖包: 清单写在 publish 输出目录里, New-FrameworkPkg 会把目录里的文件整体打进去。
New-IntegrityManifest $fdDir @("WujieFan.exe", "WujieFan.Core.dll")
New-FrameworkPkg $fdZip $fdDir

# 便携包: 主 exe 与启动器来自两个目录, 清单单独生成后显式塞进 zip。
$scManifest = Join-Path $scDir "WujieFan.integrity.json"
New-IntegrityManifest $scDir @("WujieFan.exe", "WujieFan.Core.dll")

$scZip = Join-Path $dist ("WujieFan-" + $Version + "-win-x64-portable.zip")
New-PortablePkg $scZip (Join-Path $scDir "WujieFan.exe") (Join-Path $lsDir "WujieFanLauncher.exe") $scManifest

foreach ($z in @($fdZip, $scZip)) {
  $a = [System.IO.Compression.ZipFile]::OpenRead($z)
  "{0}  {1:N0} B  {2} entries" -f (Split-Path $z -Leaf), (Get-Item $z).Length, $a.Entries.Count
  $a.Dispose()
}

Write-Host "done. remember to restart WujieFan if you want it running again."
