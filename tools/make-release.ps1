<#
  打包发布资产。
  用法: powershell -NoProfile -File tools\make-release.ps1 -Version 0.4.0
        -Version 是必填项, 且必须与仓库根 Directory.Build.props 里的
        <Version> 完全一致 (脚本会校验, 不一致直接报错)。
  产物: dist\Zephyr-<ver>-win-x64.zip            (依赖框架, 需 .NET 8 桌面运行时)
        dist\Zephyr-<ver>-win-x64-portable.zip   (自包含单文件)

  注意: 从 0.2.0 起有两个 exe。
  ZephyrLauncher.exe 是开机自启动链的第一跳 (普通权限), 由它经 UAC 提权拉起
  Zephyr.exe。两者必须同目录, 少一个自启动就会在 Enable() 时明确报错。
#>
param([string]$Version, [switch]$SkipVerify)

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
$proj  = Join-Path $root "src\Zephyr\Zephyr.csproj"
$lproj = Join-Path $root "src\Zephyr.Launcher\Zephyr.Launcher.csproj"
$fdDir = Join-Path $dist "app"
$scDir = Join-Path $dist "portable"
$lsDir = Join-Path $dist "launcher"

# 版本号只在 Directory.Build.props 里定义一处, 三个项目 (Zephyr / Core / Launcher)
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
Get-Process Zephyr -ErrorAction SilentlyContinue | ForEach-Object {
  try { $_.Kill(); $_.WaitForExit(5000) } catch { }
}
Start-Sleep -Seconds 2
if (Get-Process Zephyr -ErrorAction SilentlyContinue) {
  throw "Zephyr is still running and cannot be stopped (it is elevated). Close it from the tray menu, or run this script as Administrator."
}

# dotnet publish 是合并输出, 不会删除上一次的残留。三个输出目录都要清:
#   - dist\app: 残留的旧 DLL 会被打进框架包 (t5 P1-4)。
#   - dist\portable / dist\launcher: 单文件发布后目录里本不该有 Zephyr.Core.dll,
#     但上一轮多文件发布的残留不会被清掉; 完整性清单按"磁盘上存在就收录"生成,
#     于是清单里会多出一条包里根本没有的 Core.dll, 启动器每次开机都会误报。
# 一次性清空, 保证产物只包含本次 publish 写出的文件。
foreach ($dir in @($fdDir, $scDir, $lsDir)) {
  if (Test-Path $dir) {
    Write-Host "[0/5] clear stale output: $dir"
    Remove-Item $dir -Recurse -Force
  }
}

# 打包前先过门禁。以前这个脚本一次都没调过 verify.ps1: 本地随手打出来的包
# 可能连单测都没跑过, 而 CI 只在 push/PR 时跑, 没有 tag 触发的发布流水线。
# 于是"发出去的包"和"验证过的代码"之间没有任何强制关系。
# -SkipVerify 留给"已经验过、只想重新打包"的场景, 用了会显式说明。
if (-not $SkipVerify) {
  $verify = Join-Path $root "tools\verify.ps1"
  if (-not (Test-Path $verify)) {
    throw "tools\verify.ps1 not found at $verify. Release packages must pass the gate first (or pass -SkipVerify knowingly)."
  }

  Write-Host "[gate] tools\verify.ps1 -FailOnSkip"
  & $verify -FailOnSkip
  if ($LASTEXITCODE -ne 0) {
    throw "verify.ps1 failed (exit $LASTEXITCODE). Refusing to build a release package from unverified code.$([Environment]::NewLine)Fix the failures, or pass -SkipVerify if you have verified this exact tree by other means."
  }
} else {
  Write-Host "[gate] SKIPPED (-SkipVerify): this package has not been verified by tools\verify.ps1"
}

Write-Host "[1/5] publish main app (framework-dependent, single-file)"
dotnet publish $proj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o $fdDir | Out-Host
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Write-Host "[2/5] publish launcher (framework-dependent, single-file, same dir)"
dotnet publish $lproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o $fdDir | Out-Host
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
  # tools/ 不进发布包: verify.ps1、make-release.ps1 是开发工具, add-wujie-layers.py 等
  # 需要 Python, 普通用户没有。仓库里 tools/ 保留不动, 只是不分发。
  Add-Entry $zip (Get-Item (Join-Path $root "README.md"))     "Zephyr/README.md"
  Add-Entry $zip (Get-Item (Join-Path $root "THIRDPARTY.md")) "Zephyr/THIRDPARTY.md"
  Add-Entry $zip (Get-Item (Join-Path $root "LICENSE"))       "Zephyr/LICENSE"
}

# 发布包里的程序条目白名单。单文件发布后 dist\app 只会写这两个 exe 加一份清单;
# 若这里还按"目录里有什么就打什么", 一旦 publish 因故退化成多文件输出 (或残留了上次的
# DLL), 就会静默打出一堆散碎文件 —— 正是要修的发布包不整洁问题。
# 显式白名单 + 缺文件即报错, 让"打空包/打残包"在打包阶段就失败而不是到用户手上才发现。
$script:AppFileWhitelist = @("Zephyr.exe", "ZephyrLauncher.exe", "Zephyr.integrity.json")

# 生成启动器在开机自启前比对的完整性清单 (t11)。
#
# 为什么必须有这一步: ZephyrLauncher.exe 每次被计划任务拉起时都会读同目录的
# Zephyr.integrity.json 并比对 Zephyr.exe / Zephyr.Core.dll 的 SHA-256 与
# 长度。没有这个文件, 校验会落到 PinsMissing 分支, 只写日志不拦人 —— 等于校验
# 形同不存在。所以清单必须在打包阶段生成, 而不是靠用户或开发者手工维护。
#
# 局限（不要被它误导）: 清单和 exe 在同一个目录里, 能替换 exe 的人也能一起替换
# 清单。它是"检测"不是"防御", 真正的修复是给 exe 做代码签名。
#
# 只收录真实存在的文件: 便携包是单文件发布, Zephyr.Core.dll 被并进主 exe,
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
  $out = Join-Path $Dir "Zephyr.integrity.json"
  [System.IO.File]::WriteAllText($out, $json, (New-Object System.Text.UTF8Encoding($false)))
  Write-Host ("      integrity manifest: {0} ({1} files)" -f $out, $files.Count)
}

function New-FrameworkPkg([string]$Dest, [string]$BinDir) {
  if (Test-Path $Dest) { Remove-Item $Dest -Force }

  # 先解析白名单为真实文件, 缺任何一个就整体失败。顺序: 全部校验通过再建 zip,
  # 避免留下一个半成品 zip 让人误以为打包成功。
  $items = @()
  foreach ($n in $script:AppFileWhitelist) {
    $p = Join-Path $BinDir $n
    if (-not (Test-Path $p)) {
      throw "expected '$n' in $BinDir but it is missing. Single-file publish may have failed, or the output was cleaned. Refusing to build a partial package."
    }
    $items += (Get-Item $p)
  }

  # 目录里若还有白名单之外的文件, 说明 publish 没有真正单文件化 (或残留了旧依赖)。
  # 不静默忽略: 打出来的包会和 README 承诺的文件清单不一致。
  $extra = @(Get-ChildItem $BinDir -File | Where-Object { $script:AppFileWhitelist -notcontains $_.Name -and $_.Extension -ne ".pdb" })
  if ($extra.Count -gt 0) {
    $names = ($extra | ForEach-Object { $_.Name }) -join ", "
    throw "unexpected file(s) in ${BinDir}: $names. Publish should produce only $($script:AppFileWhitelist -join ', '). Single-file publish is probably not active."
  }

  $zip = [System.IO.Compression.ZipFile]::Open($Dest, "Create")
  foreach ($f in $items) { Add-Entry $zip $f ("Zephyr/" + $f.Name) }
  Add-Docs $zip
  $zip.Dispose()

  Write-Host ("      framework pkg: {0} app file(s): {1}" -f $items.Count, (($items | ForEach-Object { $_.Name }) -join ", "))
}

function New-PortablePkg([string]$Dest, [string]$MainExe, [string]$LauncherExe, [string]$Manifest) {
  if (Test-Path $Dest) { Remove-Item $Dest -Force }

  # 三个输入来自不同 publish 目录, 缺任何一个都要明确指出是哪个, 而不是抛 Get-Item 的原始异常。
  foreach ($p in @($MainExe, $LauncherExe, $Manifest)) {
    if (-not (Test-Path $p)) {
      throw "portable package input is missing: $p. Check that the self-contained publishes above succeeded."
    }
  }

  $zip = [System.IO.Compression.ZipFile]::Open($Dest, "Create")
  Add-Entry $zip (Get-Item $MainExe) ("Zephyr/" + (Split-Path $MainExe -Leaf))
  Add-Entry $zip (Get-Item $LauncherExe) ("Zephyr/" + (Split-Path $LauncherExe -Leaf))
  # 便携包的主 exe 与启动器分别来自两个 publish 目录, 不能靠"目录里有什么就打什么",
  # 清单必须显式加进去, 否则启动器在用户机器上永远读不到它。
  Add-Entry $zip (Get-Item $Manifest) "Zephyr/Zephyr.integrity.json"
  Add-Docs $zip
  $zip.Dispose()

  Write-Host ("      portable pkg: 3 app file(s): {0}, {1}, {2}" -f (Split-Path $MainExe -Leaf), (Split-Path $LauncherExe -Leaf), (Split-Path $Manifest -Leaf))
}

Write-Host "[5/5] pack"
$fdZip = Join-Path $dist ("Zephyr-" + $Version + "-win-x64.zip")

# 框架依赖包: 清单写在 publish 输出目录里, New-FrameworkPkg 只按白名单取里面那三个文件,
# 目录里若混进别的文件会直接报错而不是一起打进去。
New-IntegrityManifest $fdDir @("Zephyr.exe", "Zephyr.Core.dll")
New-FrameworkPkg $fdZip $fdDir

# 便携包: 主 exe 与启动器来自两个目录, 清单单独生成后显式塞进 zip。
$scManifest = Join-Path $scDir "Zephyr.integrity.json"
New-IntegrityManifest $scDir @("Zephyr.exe", "Zephyr.Core.dll")

$scZip = Join-Path $dist ("Zephyr-" + $Version + "-win-x64-portable.zip")
New-PortablePkg $scZip (Join-Path $scDir "Zephyr.exe") (Join-Path $lsDir "ZephyrLauncher.exe") $scManifest

foreach ($z in @($fdZip, $scZip)) {
  $a = [System.IO.Compression.ZipFile]::OpenRead($z)
  "{0}  {1:N0} B  {2} entries" -f (Split-Path $z -Leaf), (Get-Item $z).Length, $a.Entries.Count
  $a.Dispose()
}

Write-Host "done. remember to restart Zephyr if you want it running again."
