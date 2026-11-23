# Zephyr 0.6.3 发布说明

这一版只动了一处：切风扇模式时白读的两个寄存器。行为没有任何变化，少的是两笔无用的 I2C 读。

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

行为没有任何变化：回滚照旧把标志写 `ModeAuto`、照旧还原两个手动 PWM 的原值。写序（先 PWM 后标志）与回滚目标值都没动。

## 验证

| 项目 | 结果 |
| --- | --- |
| `dotnet build Zephyr.sln -c Release` | 0 警告 0 错误 |
| `dotnet build src/Zephyr.Launcher`（单独，AGENTS.md 要求） | 0 警告 0 错误 |
| `dotnet test` | 254 / 254 通过 |
| `tools/verify.ps1` | 5/5 全过，EXITCODE=0 |
| XML 契约 | `ZEPHYR_FAN1=1111` / `ZEPHYR_FAN2=2222` / `ZEPHYR_CPUT=33`（230 字节） |
| 实机启动 | 进程存活、`ui.log` 零新增、热键仍占用、设置完好、AIDA 供数正常 |

### 新断言做了变异验证

测试名从「写入前先抓四个寄存器的快照」改为「写入前先抓手动PWM的快照」，并新增一条断言：**第一次写入之前，不允许读模式标志**。

把 `Capture` 改成重新读两个标志后，这条测试立刻变红：

```
Assert.Equal() Failure: Values differ
Expected: 0
Actual:   2
```

还原后 254 条全过。这条断言能拦住回退。

## 未验证

- **没有上实机切模式**。改动只删了两次读，单测覆盖了写序（`fake.Ops` 的时序断言）与回滚路径，但真实的 EC 交互没跑。
- 没有测「I2C 往返省下来到底快了多少」—— 两次读在 300 毫秒锁超时面前可以忽略，这里改的是纯粹的浪费，不是性能瓶颈。

## 下载

| 包 | 大小 | SHA-256 |
| --- | --- | --- |
| `Zephyr-0.6.3-win-x64.zip` | 275,585 B | `53594f93f66d76553bad091502b8f380f8a00ea3edd009805e854e4f14403c8d` |
| `Zephyr-0.6.3-win-x64-portable.zip` | 98,332,124 B | `f31693a5b8560d7db479d4aa113499daa46258e37a238c96ad4929b9dae8e4d4` |

## 已知问题

- **CPU 电压依赖 LHM 内核驱动**。RTSS 叠加层里的 CPU 电压走 `LHMDataProvider`，如果安全软件拦了 `LHMDataProvider.sys`，电压会停在 1.55 V 这个占位值。
- **打包会结束正在运行的实例**。`tools/make-release.ps1` 会主动 `Stop-Process Zephyr`，装完想验证运行状态就不能再打包。
