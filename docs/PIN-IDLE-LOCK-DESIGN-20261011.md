# PIN 会话级空闲锁设计（2026-10-11）

> 关联缺陷：`docs/APP-REVIEW-FINAL-20261003.md:676` 记录的未决产品决策「PIN 是否覆盖桌面贴纸」，
> 本设计只处理**锁定触发时机**，不改变锁定范围。

## 1. 问题

用户反馈：**每次从托盘重新打开窗口都要重新输入 PIN**，且设置里的「闲时」分钟数不产生任何效果。

### 1.1 根因

`ListAutoCloseMinutes` 在全项目只有 4 处被读取（`SettingsService.cs`、`SettingsViewModel.cs`、
`NotesListWindow.xaml.cs`、`ArchivedNotesWindow.xaml.cs`），**全部只用于决定「窗口要不要自动 `Hide()` 到托盘」**，
从未参与锁定判定。

真正的锁定触发点是 `NotesListWindow` 的 `IsVisibleChanged`：

```
窗口被隐藏过一次（点 X / Alt+F4 / 托盘隐藏 / 闲时自动关闭）
  → 下次 Show() 使 IsVisible 变为 true
  → 无条件 PinOverlay.IsLocked = true
```

因此设定 30 分钟与设定 1 分钟在「要不要输 PIN」上完全等价 —— 用户感知即「设定时间没生效」。

### 1.2 叠加缺陷

| # | 缺陷 | 后果 |
|---|------|------|
| 1 | 「闲时」口径为「窗口不在前台的时长」（`Deactivated` 起算 / `Activated` 停表），而非「用户无操作时长」 | 人离开但窗口仍停在前台时**永远不上锁**，恰是防偷窥最需覆盖的场景 |
| 2 | `WndProc` 的 `Show()+Activate()+Topmost=true/false+Focus()`（`NotesListWindow.xaml.cs:192-208`）与 `ShowNotesListRequestedMessage` 处理器均强制 `Activate()` | 每次双击 exe 唤醒第二实例都会给计时「续命」 |
| 3 | `ArchivedNotesWindow` 为 Transient，每次打开都新建，探测主列表解锁状态失败时会**额外再要一次 PIN** | 同一次会话内重复验证 |
| 4 | 设置页未写明锁定范围 | 用户以为桌面便签受保护，实际明文常驻 |

## 2. 目标行为规格

| 场景 | 改后行为 |
|------|----------|
| **进程冷启动**（含托盘退出后重新打开 exe） | **锁定，必须输入 PIN** |
| X 关到托盘 → 重新打开（同进程） | 不锁，直接可用 |
| 闲置未超时 → 切走再切回 | 不锁 |
| 闲置超阈值 → 重新打开 / 切回窗口 | **锁定** |
| 闲置超阈值 → 窗口一直停在前台无人操作 | **锁定**（修复缺陷 1） |
| 锁屏 / 切换到锁屏界面 | **立即锁定** |
| 第二实例唤醒（双击 exe 但进程已在运行） | 不锁（同进程，非冷启动） |
| 桌面便签 NoteWindow | 不锁；设置页显式写明范围 |

**「闲置」的准确定义**：自上次键鼠输入起**无任何输入事件**的时长。与窗口是否可见、是否在前台**无关**。

## 3. 设计

### 3.1 核心：SessionLockCoordinator（新增单例）

新增 `Services/SessionLockCoordinator.cs`，作为**会话级唯一的锁定状态源**，取代各窗口自行判断。

```
状态
  bool          IsLocked          原始锁定态（吸收态）
  DateTime      LastActivityUtc   上次键鼠输入时刻
  event         LockRequested     触发时各窗口覆盖遮罩
  event         Unlocked          解锁时各窗口收起遮罩

活动源
  InputManager.Current.PreProcessInput   全局键鼠输入（覆盖托盘态与全部窗口）
  SystemEvents.SessionSwitch(SessionLock)  锁屏 → 立即强制上锁

计时
  DispatcherTimer 1 秒 tick，仅比对时间戳，不做 UI 工作
  判定：UtcNow - LastActivityUtc >= IdleLockMinutes

时钟注入
  Func<DateTime> _utcNow（internal 构造），测试用假时钟驱动，无需真等 N 分钟
```

**吸收态设计**：一旦 `IsLocked` 为 true，`NotifyUserActivity()` 只更新 `LastActivityUtc` 而**不解除锁定**。
否则用户点一下遮罩就会白锁。唯一解锁入口是 `Unlock()`，只由 PIN 验证成功后调用。

**冷启动初值**：构造时 `IsLocked = PinService.IsPinEnabled && SettingsService.LockOnLaunch`。
`LockOnLaunch` 默认 **true**，且提供开关（关闭后重启即完全无防护，UI 需给出风险提示）。

### 3.2 PinService 职责不变

维持现状「只做 set / verify / clear」（见 `PinService.cs:6-9` 原注释），
状态一律走 coordinator，避免出现两个真相源。

### 3.3 窗口改造

**NotesListWindow**

| 位置 | 现状 | 改后 |
|------|------|------|
| ctor | — | 注入 `SessionLockCoordinator`；订阅 `LockRequested` / `Unlocked` |
| `Loaded` (`:58`) | `IsLocked = _pinService?.IsPinEnabled ?? false` | `SyncLockState()` |
| `IsVisibleChanged` (`:71-85`) | 变可见即强制上锁 | **删除强制上锁**，改为 `SyncLockState()`（仅同步） |
| `_idleCloseTimer` / `Deactivated` (`:30-31,88-129`) | 「不在前台 N 分钟自动关闭」 | **删除** |
| `PinOverlay.Unlocked` | 无处理 | → `coordinator.Unlock()` |

**ArchivedNotesWindow**

- 删除 `Loaded` 中「探测主列表是否已解锁」的整段逻辑（`:33-39`），直接 `SyncLockState()`
- 删除 `_idleCloseTimer` 与 `Activated` / `Deactivated`（`:17,71-81`）
- 顺带修复缺陷 3：解锁一次即全会话解锁

**锁定的生效条件**：`PinOverlay.IsLocked = PinService.IsPinEnabled && coordinator.IsLocked`。
PIN 被清除后立即失效，不必重启。

### 3.4 设置项

| 字段 | 默认 | 说明 |
|------|------|------|
| `IdleLockMinutes` | 10 | 无键鼠输入 N 分钟后上锁；0 = 不启用。选项 0/1/5/10/30/60 |
| `LockOnLaunch` | **true** | 进程冷启动即上锁 |
| ~~`ListAutoCloseMinutes`~~ | — | **移除**，语义被 `IdleLockMinutes` 完全覆盖 |

**移除旧项的理由**：① 它的副作用正是本次痛点的来源（自动隐藏 → 下次 `Show()` 必锁，而旧逻辑无阈值兜底）；
② 有了遮罩锁定后防偷窥目的已达成，且**上锁不打断工作**（窗口留在原位，只盖住内容），优于隐藏到托盘；
③ 两项语义重叠会让设置页更难判断哪个在起作用。

**假安全防护**：`IdleLockMinutes == 0 && LockOnLaunch == false && PIN 已启用` 时，
设置页以危险色显示告警 —— 绝不允许「以为受保护实则未受保护」静默发生（沿用 F-P1-6 的处理原则）。

**范围声明**：PIN 分组下常驻一行
「当前锁定范围：管理中心列表窗口与归档管理窗口；桌面便签不受保护。」

### 3.5 PinLockOverlay 微调

新增 `HintText`，自动锁定时显示「已自动锁定，请输入 PIN」以区别于「刚打开就要输」；
解锁成功后通过既有 `Unlocked` 事件通知宿主，由宿主回调 coordinator 记活动。

## 4. 测试计划

| 用例 | 断言 |
|------|------|
| `ShouldLock_未到时不锁 / 到点锁` | 纯函数边界：`< 分钟` 不锁、`>= 分钟` 锁 |
| `冷启动且启用PIN_初始为锁定` | `LockOnLaunch=true` → `IsLocked=true` |
| `冷启动但LockOnLaunch关闭_初始不锁` | 开关生效 |
| `未启用PIN_初始不锁` | `PinEnabled=false` → `IsLocked=false` |
| `闲置超时后Evaluate_触发锁定` | 假时钟推进 → `LockRequested` 恰好触发一次 |
| `已锁定后用户活动_不得自动解锁` | 吸收态 |
| `Unlock_后重新计时` | `LastActivityUtc` 刷新 |
| `IdleLockMinutes为0_永不自动锁` | 开关生效 |
| `ForceLock_幂等` | 重复调用不重复触发事件 |
| `Hide后Show_不再自动锁定`（回归） | 对应 `IsVisibleChanged` 改动 |
| `锁定态不得因快捷键解锁`（回归） | 现有 `NotesListWindow_LockedByPin_ShouldBlockCtrlNAndCtrlF` |

## 5. 实施顺序

1. `SessionLockCoordinator` + 纯判定函数
2. `AppSettings` 字段与 `SettingsService` 属性
3. `NotesListWindow` / `ArchivedNotesWindow` 改造
4. `PinLockOverlay` 提示文案
5. `SettingsViewModel` / `SettingsWindow.xaml`
6. DI 注册 + `App.OnStartup` 显式解析
7. 测试与全量回归
