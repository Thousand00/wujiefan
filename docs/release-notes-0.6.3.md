# Zephyr 0.6.3 发布说明

第五轮全量审计的结论是「新发现实缺陷 0 个，代码已收敛」。这一版只动了一处：切模式时白读的两个寄存器。

## 这一版改了什么

### 切风扇模式少两次 I2C 往返

`FanReader.SetMode` 在写入前会抓一次快照，用于「写到一半失败」时把 EC 还原。快照原来读了四个寄存器 —— 两个手动 PWM、两个模式标志：

```csharp
private readonly record struct ModeSnapshot(int Pwm1, int Pwm2, int Flag1, int Flag2)
```

但回滚 `RecoverToAuto` 只用到 `snapshot.Pwm1` / `snapshot.Pwm2`。两个模式标志一律写成常量：

```csharp
TryWrite(EcRegisters.ModeFlag1, EcRegisters.ModeAuto, problems);
TryWrite(EcRegisters.ModeFlag2, EcRegisters.ModeAuto, problems);
```

回滚的目标固定是自动模式，读回来的旧标志没有任何用处。所以那两次读是纯粹的浪费 —— **每次切模式多两次 I2C 往返，一次都没用过**。

现在快照只留两个字段：

```csharp
private readonly record struct ModeSnapshot(int Pwm1, int Pwm2)
```

行为没有任何变化：回滚照旧把标志写 `ModeAuto`、照旧还原两个手动 PWM 的原值。少的是两笔无用的读。

## 审计报告里我不同意的四条

这份报告质量很高，但核对代码后有四处与实现不符。**没有照着改**，因为照做会引入问题。

### 1. 报告说 `HasData` 是「完全孤儿属性，tests/ 也零引用」

实际有 3 条测试在用它：

| 文件 | 行 | 断言 |
| --- | --- | --- |
| `tests/Zephyr.Core.Tests/FanReaderSamplingTests.cs` | 30 | `Assert.True(s.HasData)` |
| 同上 | 52 | `Assert.True(s.HasData);   // 温度无效不影响风扇读数` |
| 同上 | 119 | `Assert.False(s.HasData)` |

删掉 `HasData` 这三条测试会直接编译失败。它也不是纯孤儿 —— 它是「这次采样到底有没有有效数据」的语义出口，测试靠它判断。

### 2. 报告说 `RuntimeIdentity.cs` 的「只能用 System.*」约束「只在 AGENTS.md 写了」

文件头从 v0.6.0 起就有：

> 这个文件会被主程序、Core 与启动器三处链接编译（见两个 csproj 的 `<Compile Include>`），因为启动器为了缩小攻击面刻意不引用 Core。因此这里不能用任何需要引用 Core 其他类型的写法。

约束一直在源码里，`AGENTS.md` 是后来补的第二份。

### 3. 报告说 `AppSettings.Load()` 里有 `Interlocked.Exchange(ref _loadLockTaken, 1)` 做「跨进程互斥」

不存在这个字段 —— 全仓 grep `_loadLockTaken` 零命中，`AppSettings.cs` 里 `Load()` 之外没有任何 `Interlocked` / `lock` / `Mutex`。

而且即使有也不成立：`Interlocked` 是**进程内**原语，做不到跨进程互斥。

### 4. 报告说 `QueryState` 的四态叫 `NotInstalled / Inconclusive / Installed / Foreign`

实际是：

```csharp
public enum TaskState
{
    Unknown,   // 没查成 (schtasks 超时/起不来)
    Absent,    // 确实没有这个任务
    Ours,      // 任务存在且属于本程序
    Foreign,   // 任务存在但命令不是我们的
}
```

名字和语义都对不上，尤其 `Unknown` 与 `Inconclusive` 的区别报告没抓到 —— 代码里 `Unknown` 是「任何结论都不能基于这个状态」，`Absent` 才是「确实没有」。

## 验证

| 项目 | 结果 |
| --- | --- |
| `dotnet build Zephyr.sln -c Release` | 0 警告 0 错误 |
| `dotnet build src/Zephyr.Launcher`（单独，AGENTS.md 要求） | 0 警告 0 错误 |
| `dotnet test` | 254 / 254 通过 |
| `tools/verify.ps1` | 5/5 全过，EXITCODE=0 |
| XML 契约 | `ZEPHYR_FAN1=1111` / `ZEPHYR_FAN2=2222` / `ZEPHYR_CPUT=33`（230 字节） |

### 新断言做了变异验证

测试名从「写入前先抓四个寄存器的快照」改为「写入前先抓手动PWM的快照」，并新增一条断言：**第一次写入之前，不允许读模式标志**。

把 `Capture` 改成重新读两个标志后，这条测试立刻变红：

```
Assert.Equal() Failure: Values differ
Expected: 0
Actual:   2
```

还原后 254 条全过。这条断言有牙。

## 未验证

- **没有上实机切模式**。改动只删了两次读，写序与回滚目标值都没动，单测覆盖了写序（`fake.Ops` 的时序断言）与回滚路径，但真实的 EC 交互没跑。
- 没有测「I2C 往返省下来到底快了多少」—— 两次读在 300 毫秒锁超时面前可以忽略，这里改的是纯粹的浪费，不是性能瓶颈。

## 下载

| 包 | 大小 | SHA-256 |
| --- | --- | --- |
| `Zephyr-0.6.3-win-x64.zip` | 待填 | 待填 |
| `Zephyr-0.6.3-win-x64-portable.zip` | 待填 | 待填 |

## 已知问题

- **CPU 电压依赖 LHM 内核驱动**。RTSS 叠加层里的 CPU 电压走 `LHMDataProvider`，如果安全软件拦了 `LHMDataProvider.sys`，电压会停在 1.55 V 这个占位值。
- **打包会结束正在运行的实例**。`tools/make-release.ps1` 会主动 `Stop-Process Zephyr`，装完想验证运行状态就不能再打包。
