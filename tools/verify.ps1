<#
.SYNOPSIS
    WujieFan 统一验证入口。本地与 CI 共用同一份逻辑，ci.yml 只调用本脚本。

.DESCRIPTION
    依次执行（可用开关裁剪）：
      1. 版本一致性：Directory.Build.props <Version> 与三个程序集产物 FileVersion 比对
      2. 构建：dotnet build WujieFan.sln -c Release（退出码决定成败，另查 0 警告）
      3. 单测：dotnet test tests/WujieFan.Core.Tests（xUnit）
      4. 回归：EcReadSmoke --autostart-test（免管理员、纯字符串断言）
      5. XML 契约：EcReadSmoke --xml 产出必须可解析为合法 XML 且含预期 AIDA 源 ID

    本脚本绝不执行任何写 EC、注册计划任务、重启 RTSS 的动作：
    --fanmode-live / --autostart-live / --autostart-cn / --autostart-upgrade
    一律不在此列。需要这些的实机检查见 docs/03-实机验收手册.md，属人工项。

    --checkhw 的退出码依赖机器（本机判"支持"=0，CI runner 必然"不支持"=1），
    且它把"6 个构造身份断言错"与"本机机型不受支持"混成同一个 1。
    因此本脚本不把 --checkhw 当门禁，也不执行它。

.PARAMETER Configuration
    构建配置，默认 Release。

.PARAMETER MinTestCount
    单测用例数下限，默认 218（全局快捷键的键位模型与配置归一化补充 40 条后的基线）。
    只做下界断言，用例增多不会误报；真正的门禁是失败数 = 0。

.PARAMETER SkipBuild
    跳过构建，直接校验已有 Release 产物。

.PARAMETER SkipTest
    跳过单测。

.PARAMETER SkipSmoke
    跳过 EcReadSmoke 的 --autostart-test 与 --xml 两项。

.PARAMETER SkipVersion
    跳过版本一致性检查。

.PARAMETER AllowWarnings
    构建出现 MSBuild 警告时不判失败（默认严格：有警告即失败）。

.PARAMETER FailOnSkip
    把"环境缺失跳过"（退出码 10）也当作失败。CI 使用，避免 runner 上静默变绿。

.EXAMPLE
    pwsh -NoProfile -File tools/verify.ps1

.EXAMPLE
    pwsh -NoProfile -File tools/verify.ps1 -SkipBuild -SkipTest

.NOTES
    退出码：0 = 通过；1 = 断言失败；10 = 因环境缺失跳过。
    任何非 0 非 10 都算失败。
    本文件必须保持 UTF-8 with BOM（PowerShell 5.1 按 ANSI 解码中文会 ParserError）。
#>

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [int]$MinTestCount = 218,
    [switch]$SkipBuild,
    [switch]$SkipTest,
    [switch]$SkipSmoke,
    [switch]$SkipVersion,
    [switch]$AllowWarnings,
    [switch]$FailOnSkip
)

$ErrorActionPreference = 'Stop'

$EXIT_PASS = 0
$EXIT_FAIL = 1
$EXIT_SKIP = 10

$script:RepoRoot = Split-Path -Parent $PSScriptRoot
$script:Skips = @()

# 让 dotnet CLI 输出固定为英文，才能稳定解析 Failed/Passed/Total。
# 中文 Windows 上 CLI 消息会被本地化，正则匹配会失效。
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

# 子进程的中文输出（EcReadSmoke 自己的 Console.WriteLine）按 UTF-8 解码。
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
} catch {
    # 个别宿主不允许改控制台编码，解析会退化为按退出码判断，不影响结论。
}

function Write-Head {
    param([string]$Text)
    Write-Host ''
    Write-Host ('=' * 70)
    Write-Host $Text
    Write-Host ('=' * 70)
}

function Write-Ok {
    param([string]$Text)
    Write-Host "[ OK ] $Text" -ForegroundColor Green
}

function Write-Bad {
    param([string]$Text)
    Write-Host "[FAIL] $Text" -ForegroundColor Red
}

function Write-SkipMsg {
    param([string]$Text)
    Write-Host "[SKIP] $Text" -ForegroundColor Yellow
    $script:Skips += $Text
}

function Write-Info {
    param([string]$Text)
    Write-Host "       $Text"
}

# 运行一个原生命令，回显输出，返回 @{ ExitCode; Lines }
function Invoke-Native {
    param(
        [string]$FilePath,
        [string[]]$Arguments
    )

    Write-Info ("> $FilePath $($Arguments -join ' ')")

    $raw = & $FilePath @Arguments 2>&1
    $code = $LASTEXITCODE

    $lines = @()
    foreach ($item in $raw) {
        $line = $item.ToString()
        $lines += $line
        Write-Host "    $line"
    }

    return @{ ExitCode = $code; Lines = $lines }
}

# 在项目 bin 下按需找最新的一份产物（平台目录有 bin/<cfg>/ 与 bin/x64/<cfg>/ 两种布局）
# 只在当前配置的目录里找：Debug 产物通常更新，混进来会让版本校验读到错的文件。
function Find-Artifact {
    param(
        [string]$ProjectDir,
        [string]$FileName
    )

    $binDir = Join-Path $script:RepoRoot (Join-Path $ProjectDir 'bin')
    if (-not (Test-Path $binDir)) { return $null }

    $hit = Get-ChildItem -Path $binDir -Recurse -File -Filter $FileName -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "[\\/]$([regex]::Escape($Configuration))[\\/]" } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($null -eq $hit) { return $null }
    return $hit.FullName
}

# 读取 Directory.Build.props 里的 <Version>（唯一版本源）
function Get-BuildPropsVersion {
    $propsPath = Join-Path $script:RepoRoot 'Directory.Build.props'
    if (-not (Test-Path $propsPath)) { return $null }

    [xml]$doc = Get-Content -Path $propsPath -Raw -Encoding UTF8
    # 用 local-name 匹配，避免 MSBuild 命名空间写法的差异
    $node = $doc.SelectSingleNode("//*[local-name()='Version']")
    if ($null -eq $node) { return $null }
    return $node.InnerText.Trim()
}

# "0.4.0.0" -> "0.4.0"
function ConvertTo-ShortVersion {
    param([string]$FileVersion)

    if ([string]::IsNullOrWhiteSpace($FileVersion)) { return '' }
    $parts = $FileVersion.Split('.')
    $take = [Math]::Min(3, $parts.Length)
    return ($parts[0..($take - 1)] -join '.')
}

# ---------------------------------------------------------------- 主流程

Write-Head 'WujieFan 统一验证入口 (tools/verify.ps1)'
Write-Info "仓库: $script:RepoRoot"
Write-Info "配置: $Configuration"
Write-Info "PowerShell: $($PSVersionTable.PSVersion)"

$failures = @()

# ---- 1. 构建 -------------------------------------------------------------
if (-not $SkipBuild) {
    Write-Head "1/5 构建 WujieFan.sln -c $Configuration"

    $sln = Join-Path $script:RepoRoot 'WujieFan.sln'
    $r = Invoke-Native -FilePath 'dotnet' -Arguments @('build', $sln, '-c', $Configuration)

    if ($r.ExitCode -ne 0) {
        Write-Bad "dotnet build 退出码 $($r.ExitCode)"
        $failures += "dotnet build 退出码 $($r.ExitCode)"
    } else {
        Write-Ok 'dotnet build 退出码 0'

        # 期待 0 警告：只统计 MSBuild 的 ": warning " 诊断行
        $warnLines = @()
        foreach ($line in $r.Lines) {
            if ($line -match ':\s+warning\s') { $warnLines += $line }
        }

        if ($warnLines.Count -eq 0) {
            Write-Ok '构建 0 警告'
        } elseif ($AllowWarnings) {
            Write-SkipMsg "构建有 $($warnLines.Count) 条警告（-AllowWarnings 下不判失败）"
        } else {
            Write-Bad "构建有 $($warnLines.Count) 条警告（要求 0 警告；确需放行请加 -AllowWarnings）"
            foreach ($w in $warnLines) { Write-Info $w }
            $failures += "构建有 $($warnLines.Count) 条警告"
        }
    }
} else {
    Write-Head '1/5 构建 —— 已按 -SkipBuild 跳过'
}

# ---- 2. 版本一致性 -------------------------------------------------------
if (-not $SkipVersion) {
    Write-Head '2/5 版本一致性 (Directory.Build.props vs 程序集 FileVersion)'

    $propsVersion = Get-BuildPropsVersion
    if ([string]::IsNullOrWhiteSpace($propsVersion)) {
        Write-Bad '读不到 Directory.Build.props 的 <Version>'
        $failures += 'Directory.Build.props 缺少 <Version>'
        $propsVersion = ''
    } else {
        Write-Info "Directory.Build.props Version = $propsVersion"
    }

    if ($propsVersion -ne '') {
        $targets = @(
            @{ Name = 'WujieFan';          Dir = 'src/WujieFan';          File = 'WujieFan.exe' },
            @{ Name = 'WujieFan.Core';     Dir = 'src/WujieFan.Core';     File = 'WujieFan.Core.dll' },
            # Launcher 的 AssemblyName 是 WujieFanLauncher，不是项目名
            @{ Name = 'WujieFan.Launcher'; Dir = 'src/WujieFan.Launcher'; File = 'WujieFanLauncher.exe' }
        )

        foreach ($t in $targets) {
            $path = Find-Artifact -ProjectDir $t.Dir -FileName $t.File
            if ($null -eq $path) {
                Write-SkipMsg "$($t.Name): 找不到 $($t.File) 产物（未构建过？）"
                continue
            }

            $fv = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($path)
            $actual = ConvertTo-ShortVersion $fv.FileVersion
            if ($actual -eq $propsVersion) {
                Write-Ok "$($t.Name): FileVersion $($fv.FileVersion) == $propsVersion"
            } else {
                Write-Bad "$($t.Name): FileVersion $($fv.FileVersion) != $propsVersion  ($path)"
                $failures += "$($t.Name) 的 FileVersion 是 $($fv.FileVersion)，应为 $propsVersion"
            }
        }
    }
} else {
    Write-Head '2/5 版本一致性 —— 已按 -SkipVersion 跳过'
}

# ---- 3. 单测 -------------------------------------------------------------
if (-not $SkipTest) {
    Write-Head '3/5 单测 tests/WujieFan.Core.Tests'

    $testProj = Join-Path $script:RepoRoot 'tests/WujieFan.Core.Tests/WujieFan.Core.Tests.csproj'

    # 产物必须落到工作区之外。在受限宿主里，产物若生成在仓库内，testhost 启动时会崩在
    # ProcessManager.OpenProcess -> EnsureWatchingForExit (Win32Exception 5: 拒绝访问)。
    # 指到 %TEMP% 下的随机目录则能正常跑完；这与路径写法无关，只与落点是否在工作区内有关。
    $art = Join-Path $env:TEMP ('wf-verify-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))

    # -p:Platform=x64 必须显式给：工程声明了 Platforms=x64，缺省会走 AnyCPU 导致平台不匹配。
    $testArgs = @('test', $testProj, '-c', $Configuration, '-p:Platform=x64',
                  '--artifacts-path', $art, '--nologo')

    try {
        $r = Invoke-Native -FilePath 'dotnet' -Arguments $testArgs
    } finally {
        # 无论成败都清掉，避免 %TEMP% 堆积大量 wf-verify-* 目录
        if (Test-Path $art) {
            Remove-Item $art -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    $joined = ($r.Lines -join "`n")

    # 宿主限制识别：testhost 在部分沙箱/受限终端里仍可能起不来，与测试代码无关。
    # 命中特征串时给明确提示，既不静默判失败，也不自动判绿（返回 10）。
    $hostLimited = $false
    $hostPatterns = @(
        'SetParentProcessExitCallback',
        'OpenProcess',
        'Win32Exception',
        'Access is denied',
        '拒绝访问',
        'Test Run Aborted',
        'testhost process crashed'
    )
    foreach ($p in $hostPatterns) {
        if ($joined -match [regex]::Escape($p)) { $hostLimited = $true; break }
    }

    # 汇总行：英文 CLI 与中文 CLI 两种形态都要认（DOTNET_CLI_UI_LANGUAGE 未必生效）。
    #   英文: Failed: 0, Passed: 148, Skipped: 0, Total: 148
    #   中文: 失败: 0，通过: 148，已跳过: 0，总计: 148
    $m = [regex]::Match($joined, 'Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)')
    if (-not $m.Success) {
        $m = [regex]::Match($joined, '失败:\s*(\d+)\s*[，,]\s*通过:\s*(\d+)\s*[，,]\s*已跳过:\s*(\d+)\s*[，,]\s*总计:\s*(\d+)')
    }

    if ($m.Success) {
        $failed = [int]$m.Groups[1].Value
        $passed = [int]$m.Groups[2].Value
        $skipped = [int]$m.Groups[3].Value
        $total = [int]$m.Groups[4].Value

        Write-Info "用例: 总 $total / 通过 $passed / 失败 $failed / 跳过 $skipped"

        if ($failed -gt 0) {
            Write-Bad "单测失败 $failed 个"
            $failures += "单测失败 $failed 个"
        } elseif ($total -lt $MinTestCount) {
            Write-Bad "用例数 $total 低于下限 $MinTestCount（有用例被意外丢失？）"
            $failures += "用例数 $total < $MinTestCount"
        } else {
            Write-Ok "单测全通过（$passed/$total，下限 $MinTestCount）"
        }
    } elseif ($hostLimited -and $r.ExitCode -ne 0) {
        Write-SkipMsg '单测无法启动：testhost 命中宿主限制（OpenProcess / 拒绝访问 / SetParentProcessExitCallback）。'
        Write-Info '这与测试代码无关，通常是产物落点或父进程句柄受限所致。'
        Write-Info '本脚本已把产物放到 %TEMP% 的随机目录；若仍失败，请确认 TEMP 可写，或在 CI / 交互式终端重试。'
        Write-Info '本项计为"环境缺失跳过"（退出码 10），不算通过。'
    } elseif ($r.ExitCode -ne 0) {
        Write-Bad "dotnet test 退出码 $($r.ExitCode)，且未解析到汇总行"
        $failures += "dotnet test 退出码 $($r.ExitCode)"
    } else {
        Write-Bad 'dotnet test 退出码 0 但未解析到用例汇总行，无法确认用例数'
        $failures += 'dotnet test 输出缺少汇总行'
    }
} else {
    Write-Head '3/5 单测 —— 已按 -SkipTest 跳过'
}

# ---- 4/5. EcReadSmoke 免管理员回归 --------------------------------------
if (-not $SkipSmoke) {
    Write-Head '4/5 回归 EcReadSmoke --autostart-test（免管理员）'

    $smokeDll = Find-Artifact -ProjectDir 'tests/EcReadSmoke' -FileName 'EcReadSmoke.dll'
    if ($null -eq $smokeDll) {
        Write-SkipMsg 'EcReadSmoke.dll 不存在（未构建？）'
        $smokeDll = ''
    } else {
        Write-Info "产物: $smokeDll"
        $r = Invoke-Native -FilePath 'dotnet' -Arguments @($smokeDll, '--autostart-test')

        $joined = ($r.Lines -join "`n")
        $m = [regex]::Match($joined, '通过\s*(\d+)\s*/\s*失败\s*(\d+)')

        if ($r.ExitCode -ne 0) {
            Write-Bad "autostart-test 退出码 $($r.ExitCode)"
            $failures += "autostart-test 退出码 $($r.ExitCode)"
        } elseif ($m.Success) {
            $pass = [int]$m.Groups[1].Value
            $fail = [int]$m.Groups[2].Value
            if ($fail -ne 0) {
                Write-Bad "autostart-test 失败 $fail 个"
                $failures += "autostart-test 失败 $fail 个"
            } elseif ($pass -lt 6) {
                Write-Bad "autostart-test 只跑了 $pass 个用例（应 >= 6）"
                $failures += "autostart-test 用例数 $pass < 6"
            } else {
                Write-Ok "autostart-test 通过 $pass / 失败 0"
            }
        } else {
            # 退出码 0 已代表 fail == 0；文本解析失败只是本地化/编码问题
            Write-Ok "autostart-test 退出码 0（未解析到汇总行，按退出码判定）"
        }
    }

    # ---- 5/5 XML 契约 ----
    Write-Head '5/5 XML 契约 EcReadSmoke --xml'

    if ($null -eq $smokeDll -or $smokeDll -eq '') {
        Write-SkipMsg 'EcReadSmoke.dll 不存在，跳过 XML 契约检查'
    } else {
        $r = Invoke-Native -FilePath 'dotnet' -Arguments @($smokeDll, '--xml')

        if ($r.ExitCode -ne 0) {
            Write-Bad "--xml 退出码 $($r.ExitCode)"
            $failures += "--xml 退出码 $($r.ExitCode)"
        } else {
            $all = ($r.Lines -join "`n")

            # BuildXml 产出的是"无根节点、无 XML 声明"的片段（RTSS 的极简状态机要求），
            # 校验时必须自己套一个合成根节点再解析，否则必然报"根级别上的数据无效"。
            $start = $all.IndexOf('<')
            $end = $all.LastIndexOf('>')

            if ($start -lt 0 -or $end -le $start) {
                Write-Bad '--xml 输出里找不到 XML 片段'
                $failures += '--xml 输出无 XML 片段'
            } else {
                $frag = $all.Substring($start, $end - $start + 1)

                if ($frag -match '^\s*<\?xml') {
                    Write-Bad 'XML 片段不应带 XML 声明（RTSS 解析器不接受）'
                    $failures += 'XML 片段带 XML 声明'
                }

                $parsed = $false
                try {
                    [xml]$doc = "<wujiefan>$frag</wujiefan>"
                    $parsed = $true
                } catch {
                    Write-Bad "XML 片段无法解析: $($_.Exception.Message)"
                    $failures += 'XML 片段无法解析'
                }

                if ($parsed) {
                    Write-Ok 'XML 片段可解析（已套合成根节点 <wujiefan>）'

                    # 这些 ID 就是 tools/add-wujie-layers.py 里 Provider=AIDA 源块的 ID，
                    # 图层名通过 %SourceName% 引用它们，故校验 ID 等价于校验图层契约。
                    $fanNodes = @($doc.wujiefan.fan)
                    $tempNodes = @($doc.wujiefan.temp)

                    if ($fanNodes.Count -ne 2) {
                        Write-Bad "预期 2 个 <fan>，实际 $($fanNodes.Count)"
                        $failures += "<fan> 数量 $($fanNodes.Count) != 2"
                    }

                    $expectedIds = @('WUJIE_FAN1', 'WUJIE_FAN2')
                    foreach ($id in $expectedIds) {
                        $hit = @($fanNodes | Where-Object { $_.id -eq $id })
                        if ($hit.Count -eq 1) {
                            Write-Ok "源 ID $id 存在（label=$($hit[0].label) value=$($hit[0].value)）"
                            if ([string]::IsNullOrWhiteSpace($hit[0].value)) {
                                Write-Bad "源 $id 的 <value> 为空"
                                $failures += "$id 的 value 为空"
                            }
                        } else {
                            Write-Bad "缺少源 ID $id"
                            $failures += "缺少源 ID $id"
                        }
                    }

                    if ($tempNodes.Count -ne 1) {
                        Write-Bad "预期 1 个 <temp>，实际 $($tempNodes.Count)"
                        $failures += "<temp> 数量 $($tempNodes.Count) != 1"
                    } elseif ($tempNodes[0].id -ne 'WUJIE_CPUT') {
                        Write-Bad "温度源 ID 应为 WUJIE_CPUT，实际 $($tempNodes[0].id)"
                        $failures += "温度源 ID 错误: $($tempNodes[0].id)"
                    } else {
                        Write-Ok "温度源 WUJIE_CPUT = $($tempNodes[0].value)"
                    }
                }
            }
        }
    }
} else {
    Write-Head '4/5、5/5 EcReadSmoke 回归 —— 已按 -SkipSmoke 跳过'
}

# ---- 汇总 ---------------------------------------------------------------
Write-Head '汇总'

if ($failures.Count -gt 0) {
    Write-Bad "共 $($failures.Count) 项失败："
    foreach ($f in $failures) { Write-Info "- $f" }
    if ($script:Skips.Count -gt 0) {
        Write-Info "另有 $($script:Skips.Count) 项跳过："
        foreach ($s in $script:Skips) { Write-Info "- $s" }
    }
    Write-Host ''
    Write-Host "EXITCODE=$EXIT_FAIL"
    exit $EXIT_FAIL
}

if ($script:Skips.Count -gt 0) {
    Write-Host "[SKIP] 有 $($script:Skips.Count) 项因环境缺失未验证（不算通过）：" -ForegroundColor Yellow
    foreach ($s in $script:Skips) { Write-Info "- $s" }

    if ($FailOnSkip) {
        Write-Bad '已指定 -FailOnSkip：跳过项视为失败'
        Write-Host ''
        Write-Host "EXITCODE=$EXIT_FAIL"
        exit $EXIT_FAIL
    }

    Write-Host ''
    Write-Host "EXITCODE=$EXIT_SKIP"
    exit $EXIT_SKIP
}

Write-Ok '全部检查通过'
Write-Host ''
Write-Host "EXITCODE=$EXIT_PASS"
exit $EXIT_PASS
