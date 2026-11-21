# LitMotion fork 扩展说明

本包是 `annulusgames/LitMotion` v2.0.2 的 fork，本文说明 fork 新增的功能（新增文件的位置见 6.3 末尾）。包名不变（`com.annulusgames.lit-motion`），通过 Git 地址加 `?path=src/LitMotion/Assets/LitMotion` 引用。目标环境是 Unity 2022.3、UGUI。

新增四项功能：序列回调、开始回调、按目标批量停止、播放控制。不使用这些功能的代码，行为与 v2.0.2 相同，唯一例外见第 3 节。

---

## 1. 功能用法与确切语义

### 1.1 序列回调：`AppendCallback` / `InsertCallback`

```cs
LSequence.Create()
    .Append(fadeIn)
    .AppendCallback(() => button.interactable = true)   // fadeIn 结束处
    .InsertCallback(0.25f, PlaySound)                    // 序列开始后 0.25 秒
    .Append(scaleUp)
    .Run();
```

- `AppendCallback(cb)`：位置是下一个 `Append` 的起点，也就是最近一次 `Append`/`AppendInterval` 的末尾。紧随其后的 `Join` 插在回调的位置。它不考虑 `Insert`/`InsertCallback` 加入的内容。
- `InsertCallback(position, cb)`：沿用 LitMotion `Insert` 的语义。它不影响后续 `Append`/`Join` 的锚点，但会把序列时长延长到 `position`。`position` 必须是不小于 0 的有限值，`cb` 不能为 null。
- 触发规则（裁决 B）。下面的“时间”指**当前序列自身的时间**；对最外层序列来说就是它的总时间。嵌套序列也按自身时间判断，与“跟随根序列方向”的要求不同，见第 5 节第 1 条。
  - 每一轮循环里，每个回调有且仅有一个触发点。Yoyo/Flip 的回程轮（奇数轮）取镜像位置，回调按位置降序触发。
  - 时间向前推进并到达或越过触发点时触发一次。Restart、Yoyo、Flip、Incremental 四种循环类型每轮都触发。
  - 时间向后（负的 `PlaybackSpeed`、往回设 `Time`）一律不触发，只重新上膛：新时间之后的触发点恢复为未触发。
  - 一次更新跨过多轮时，被跳过的每一整轮的回调都按顺序补发。补发发生在当前轮的子动效求值之前；只有最终所在的那一轮，回调才按位置与子动效交错执行。
  - `Complete()` 和往前设 `Time` 会补发跳过的回调，包括剩余轮次。`Cancel()` 不触发。
  - 回调在触发点所在的那次更新里执行，从不在 `Run()` 里同步执行。序列在延迟中、且 `skipValuesDuringDelay` 为 true（值不绑定）时，回调照常派发。
  - 同一次更新内的顺序（最终所在轮、非镜像轮）：
    1. 起点在新位置之后、尚未开始的子动效（从后往前）；
    2. 起点不晚于新位置的子动效按起点从早到晚求值，每个子动效之前先执行位置不晚于其起点的回调；
    3. 剩余的回调。

    因此起点恰好在某回调位置的子动效，在该回调之后才开始。镜像轮里先写全部子动效，再执行本层回调。
  - 到达终点的那次更新里，回调执行时序列尚未发布为“已完成”（仍为 Playing）。回调可以取消序列；取消后，本次更新剩余的回调和 OnComplete 都不再发生。`Runner`、设 `Time`、`Complete()` 三个入口一致。
  - 折返点：位于序列末尾的回调在 Yoyo/Flip 折返时触发两次（去程末一次、回程初一次）。相邻两轮之间没有每轮延迟时，两次在同一时刻；有每轮延迟（`DelayType.EveryLoop`）时，两次相隔一个延迟。
  - 非单调缓动（OutBack、OutBounce 等）：每轮内，采样到的位置第一次到达或越过触发点时触发一次；一轮结束时该轮所有回调都已触发。位置回落不会重新上膛，只有时间后退才会。时间后退时，当前轮内满足以下任一条件的回调保持已触发：位置在新位置之前，或在新时间之前（含）已触发。触发与否取决于更新的采样时刻，例如 `0 → 0.36 → 0.5` 会触发 0.9 处的回调，`0 → 0.5` 不会。
- 回调里对所在序列设 `Time`、调用 `Complete()` 或 `Cancel()`：本次更新剩余部分作废，新状态从该回调的位置继续，不会重复触发也不会跳过中间的回调。
- 异常：回调抛出的异常交给 `MotionDispatcher.RegisterUnhandledExceptionHandler` 注册的处理器，其余回调照常执行。
- 开销：一次更新跳过很多轮时（轮很短，或无限循环序列的时间步很大），开销与跳过的轮数乘以回调数成正比。
- 分配：执行回调不分配。构建带回调的序列使用池化数组，池预热后不分配。捕获变量的 lambda 在创建时分配。受保护序列（第 4 节）在首次启用保护时，按事件（OnComplete、OnCancel 各自）从多播委托中拆出一次内部清理；某个事件登记了两个以上用户回调时，这次拆分会分配。构建时就受保护的序列在构建时拆分；运行中才因 `PlayBackwards` 等启用保护的序列，在启用时拆分。

### 1.2 开始回调：`WithOnStart`

```cs
LMotion.Create(0f, 1f, 0.3f).WithDelay(0.5f)
    .WithOnStart(() => panel.SetActive(true))
    .BindToAlpha(canvasGroup);
```

- 不在 `Bind()`/`Run()` 内同步触发。它在第一次推进到延迟结束之后的那次更新里触发，先于本次写值，也先于 OnLoopComplete 和 OnComplete。条件是 `Time >= Delay` 或动效已完成。默认的 `ImmediateBind` 会在 `Bind()` 时写一次初始值，这是原库行为，与 OnStart 无关。
- 每个动效最多触发一次。循环（包括每轮延迟）、改 `Time`、`Restart()`、倒放后再正放，都不会再次触发。
- 设 `Time` 跨过延迟、或父序列推进到该子动效时，也会触发。
- `Complete()` 一个尚未开始的动效：先 OnStart，再写终值，再 OnComplete。`Goto` 到终点同样如此。
- 延迟期间被取消的动效不触发。被 `Pause()`/`Goto()` 暂停期间推迟，恢复后触发；暂停中完成，则在 OnComplete 之前触发。
- OnStart 执行期间，即使本次更新已把动效推进到终点，动效仍视为“已开始、未完成”（`IsActive()` 为 true），因此可以在 OnStart 里取消它。三种入口（Runner、设 `Time`、`Complete()`）行为一致。
- 多次调用 `WithOnStart`：每次调用登记为一个回调，按登记顺序逐个执行。某一个回调取消、完成或移动了动效，后面的回调不再执行，本次求值也停止。某一个抛异常，交给异常处理器，后面的照常执行。调用者自己用 `+` 组合成的委托算一个回调。
- 序列本身用 `WithOnStart` 时，该序列受保护（第 4 节）。
- 分配：单个缓存委托不分配。多次调用 `WithOnStart` 会分配一个小列表。捕获变量的 lambda 在创建时分配。

### 1.3 按目标批量停止：`SetTarget` / `LMotion.CancelAll` / `LMotion.CompleteAll`

```cs
LMotion.Create(...).BindToAnchoredPosition(rect).SetTarget(rect);
LMotion.CancelAll(rect);    // 返回取消的个数
LMotion.CompleteAll(rect);  // 返回完成（或取消）的个数
```

- 一个动效只有一个目标。每次调用 `SetTarget` 都是一次新登记：换成别的目标是替换；再次登记同一个目标也是新登记，这个动效会排到该目标登记顺序的末尾。
- 目标用 `object.Equals`/`GetHashCode` 比较：Unity 对象按实例（已销毁的对象仍可作键），字符串按值。自定义键在登记期间不能改变 `Equals`/`GetHashCode` 的结果，否则可能找不到（不抛异常）。
- 目标不能为 null，也不能是 `MotionHandle`。无效句柄（包括手工构造的越界索引）抛 `InvalidOperationException`。
- 销毁目标对象不会取消动效。需要随对象销毁时，组合使用 `AddTo(gameObject)`。
- 已加入序列的动效由序列控制，批量操作会跳过它们。
- `CompleteAll` 遇到无限循环的动效改为取消，同样计数。已完成但 `Preserve()` 的动效保持原样，不计数。
- 一次批量调用只处理调用开始时已存在的登记，按登记顺序处理。回调里新做的登记不受本次调用影响，包括把同一个动效重新登记到同一目标，或换走再换回。存活的登记保持原有顺序，排在回调中新增的登记之前。
- 返回值按最终状态计数：一个动效被处理完时，如果处于本次调用要求的状态（`CompleteAll` 要求已完成，`CancelAll` 和无限循环要求已取消），就计入，否则不计入。这一规则不区分动效是否使用新功能。
  - 动效自己的回调把它挪回去、或取消了它，不计入。
  - 动效自己的回调完成了它（例如在 OnStart 里调用 `Complete()`），最终是已完成，计入；完成只通知一次。
  - 单个句柄的 `TryComplete()` 另有规则。对使用新功能的动效，完成被回调取代时返回 false，`Complete()` 不抛。上面 OnStart 里嵌套完成的例子也属于这种情况：外层 `TryComplete()` 返回 false，而 `CompleteAll` 计 1。对普通动效，与 v2.0.2 相同，总是返回 true。
- 登记的释放：普通生命周期里，登记在动效从存储中移除时释放，也就是完成或取消后的下一次更新（序列的子动效也一样），以及调度器 `Reset()`/`MotionDispatcher.Clear()` 时。此外，批量操作结束后、登记条目数超过阈值时、进出 Play 模式时，会提前剔除已经失效（例如已完成但尚未移除）的条目。进出 Play 模式时，`ManualMotionDispatcher` 和编辑模式下播放的动效的登记会保留；编辑模式下也可以批量取消这些动效，此时它们的 OnCancel 在编辑模式执行。
- 分配：前提是内部表容量足够、对象池已预热。在此前提下，对已用过的目标登记和批量操作都不分配。新目标首次登记、或同一目标继续增加并存登记时，内部表可能扩容（摊还，容量够后不再分配）。值类型目标（如 int）每次调用都装箱。批量操作的快照数组来自 `ArrayPool`，首次使用或超过池中尺寸时会分配。

### 1.4 播放控制

`Pause` / `Play` / `TogglePause` / `PlayForward` / `PlayBackwards` / `Restart` / `Goto(time, andPlay)`，各有 `Try` 版本（返回是否成功），另有 `IsPaused()`。

- 基于 `Time` 和 `PlaybackSpeed` 实现，不额外分配。
- `Pause()` 记下当前速度并把 `PlaybackSpeed` 设为 0（延迟期间也暂停）。`Play()` 恢复原速度，包括方向。
- 直接给 `PlaybackSpeed` 赋值会结束 `Pause()` 和 `PlayBackwards()` 模式：之后 `Play()` 什么也不做，直接赋的负速度按原库方式倒放（不在起点停下）。
- `PlayForward()`：结束暂停和倒放模式，速度取正（保留大小）。`Time` 为负时先设为 0。
- `PlayBackwards()`：结束暂停，速度取负（保留大小）。
  - 倒放到起点（`Time == Delay`）时，在同一次更新里停在起点：按起点时刻用 adapter 求值并写入、暂停、保持存活、不发 OnComplete。即使没有 `Preserve()` 也不会被回收，不再需要时请取消。
  - 调用时已在起点或起点之前：立即移到起点、写入起点值并暂停。
  - 零时长加每轮延迟（`DelayType.EveryLoop`）的动效起点是 `Time 0`。它位于延迟中，但即使 `skipValuesDuringDelay` 为 true，上述两种停靠都会写入起点值；之后 `PlayForward()` 会再等一次延迟。
  - 已完成的 `Preserve` 动效若时间已越过终点，会立即从终点开始倒退。
  - 倒放过的动效从此受保护（第 4 节）。
- `Restart()`：`Time = 0`（重新等待延迟），恢复播放，速度取正。OnStart 不再触发，完成时 OnComplete 会再次触发。
- 完成后还要 `Restart()` 或倒放的动效，请先调用 `Preserve()`（未 Preserve 的动效完成后即被回收）；不再需要时 `Cancel()`，或用 `AddTo()` 绑定生命周期。
- `Goto(time, andPlay = false)`：时间包含延迟（与 `MotionHandle.Time` 一致），钳制在 `[0, TotalDuration]`。`andPlay` 为 false 时暂停，为 true 时恢复。到达终点即完成：先补 OnStart，再 OnComplete（即使处于暂停），未 `Preserve()` 的动效随后被回收。零时长动效也是如此。
- 有效性：以下情况抛 `InvalidOperationException`，`Try` 版本返回 false：
  - 句柄无效，包括手工构造的越界索引；
  - 动效已取消，或已完成且未 `Preserve()`；
  - 动效在序列中。

  已完成但 `Preserve()` 的动效仍然有效，可以参与播放控制（如 `PlayBackwards`、`Restart`、`Goto`）。非法参数（NaN，无限循环动效的 `+∞`）抛 `ArgumentOutOfRangeException`，`Try` 版本同样抛出。
- `IsPaused()` 不抛异常：无效、不活跃或在序列中时返回 false。

---

## 2. 与 DOTween 的差异

只列出已确认的差异（对照 DOTween 1.3.045）。

1. `AppendCallback` 的位置不考虑 `Insert` 加入的内容：`AppendInterval(1).InsertCallback(5, a).AppendCallback(b)` 中 b 在 1 秒处，DOTween 在 5 秒处。
2. `Complete()` 会补发尚未触发的序列回调（包括剩余轮次），相当于 DOTween 的 `Complete(true)`；DOTween 默认的 `Complete()` 不触发。
3. 往前设 `Time` 会补发跳过的序列回调，相当于 DOTween 的 `GotoWithCallbacks`；DOTween 默认的 `Goto` 不触发。
4. `Goto` 的时间包含延迟，DOTween 的 `Goto` 位置不含延迟。`Goto` 到终点会完成动效并触发 OnComplete（即使暂停）。
5. 折返点的末尾回调：本库在 Yoyo/Flip 折返时固定触发两次（相邻轮之间无每轮延迟时在同一时刻）。DOTween 实测依更新步长而异（单步跨过折返点时只触发一次）。
6. 嵌套序列：父序列 Yoyo 回程时，本库不触发子序列的回调（子序列按自己的时间判断，回程中它的时间在后退）；DOTween 会触发。见第 5 节。
7. 暂停用 `PlaybackSpeed = 0` 实现，直接给 `PlaybackSpeed` 赋值会结束暂停；DOTween 的暂停与 `timeScale` 互相独立。
8. `PlayBackwards()` 倒到起点后停在 `Time == Delay` 并暂停，不倒回延迟期，也不触发 OnComplete。零时长加每轮延迟的动效例外，起点是 `Time 0`。
9. `CompleteAll(target)` 遇到已完成的 `Preserve` 动效保持原样；DOTween `Kill(target, true)` 会销毁它们。无限循环动效被取消，与 DOTween 一样不保留。
10. `CancelAll`/`CompleteAll` 跳过序列中的子动效。
11. 销毁目标对象不会自动停止动效（需配合 `AddTo`）。
12. 回调和绑定中的异常交给 LitMotion 的未处理异常处理器（默认 `Debug.LogException`）。例外：`CancelOnError` 直接调用 OnCancel，OnCancel 自身抛出的异常会向外传播（原库行为）。

---

## 3. 与 v2.0.2 的行为差异

只有一条，只影响不使用新功能的代码在一种重入场景下的结果：

- **Runner 只派发本批 job 的动效。** v2.0.2 的派发循环上界是存储数组的容量，会访问本次更新中新建的动效，并读取 job 输出数组之外的值（越界读，结果未定义）。现在上界是本批的数量 `count`。
  - 可观察的变化：在普通 `Bind` 回调里新建一个动效并设它的 `Time`，v2.0.2 中新动效在本次更新里被写两次（第二次是越界值），现在只写一次。新动效从下一次更新开始正常推进。
  - 副作用：存储未满时，更新开销下降，因为不再遍历空槽。

依据：
- `LegacyParityTest`（15 例）把 v2.0.2 的派发规则和重入怪癖写成用例，原样在 v2.0.2 和本 fork 上都通过。包括：显式 `Complete()` 在 adapter 里取消动效后仍写终值并通知；adapter 在 `Complete()`/设 `Time` 的求值中扩容存储并登记 continuation 后，完成会调用它（托管条目在求值返回之后才读取，与原版相同）；序列跳到子项起点时的子项求值顺序。
- 原库的 137 个测试与 v2.0.2 逐项结果相同。
- 复核者的 20 例三入口对照（普通回调、延迟、错误、回收）与 v2.0.2 逐条一致。

其余改动对不使用新功能的代码不可观察：
- job 额外给求值和标记回收的动效写一个求值版本号；
- `SetTime`/`Complete`/`Cancel` 更新该版本号；
- Runner 和批末回收多读一次“是否受保护”的标志；
- `PlaybackSpeed` 的 setter 改为一次查找，同时清除非托管的暂停、倒放模式；
- 序列构建在 `Append`/`Insert` 时记录子项是否使用新功能；
- 序列 setter 里新增的检查和“同点子项在回调之后”的顺序只对受保护序列生效。

---

## 4. 重入契约

**受保护的动效**指用了新功能、其求值会运行新功能用户代码的动效。满足以下任一条件即受保护：
- 用了 `WithOnStart`；
- 调用过 `PlayBackwards()`；
- 是带回调的序列、含受保护子项的序列，或序列本身受保护（如序列用了 `WithOnStart`）。

受保护是**粘性的**：一旦受保护，就保持到动效结束。之后结束倒放模式（例如直接赋 `PlaybackSpeed`）也不会关闭保护。只用了 `SetTarget`/`Pause` 的动效按普通动效派发。

**任何情况下都保证内存安全**：不越界，不写已归还池的对象，不破坏调度器状态。

**保证行为正确**（对受保护的动效）：在它的任一回调里，对自己、对所在的序列、对其他动效或序列做以下操作，行为都有定义：
- 回调包括 OnStart、绑定回调、OnLoopComplete、OnComplete、OnCancel、序列回调、子动效的回调，以及 adapter 的 `Evaluate`；
- 操作包括 `Cancel`、`Complete`、设 `Time`、改 `PlaybackSpeed`、播放控制方法、`SetTarget`/`CancelAll`/`CompleteAll`、创建新动效。

规则：回调返回后，外层在继续写值、派发后续回调、完成或回收之前，先确认这次求值没有被取代。被取代的求值直接停止：不写旧值，不再派发后续事件，不回收任何东西。

- **取代**指：该动效被再次求值（设 `Time`、`Complete()`、或同批更新里先于它被其他回调求值）、被取消、或被移除（含调度器重置）。
- **统一判定**：“这次求值是否仍然有效”只有一个判定（`MotionStorage.IsCurrent`，见 `MotionEvaluation`）。求值的身份是（存储、槽位索引、槽位版本、存储重置代数、动效的求值版本号）；版本号在 job、`SetTime`、`Complete`、`Cancel` 时更新。以下几处都用这同一个判定：
  - 回调后的派发；
  - Runner 是否发布 job 输出；
  - 批末是否回收；
  - 序列内部的遍历、回调与子项清理。

  序列另加自己的租用号与更新号，合成一个判定（`MotionSequenceSource.IsCurrent`）。
- **Runner 发布与回收**：动效是否受保护，从当前数组读取，不用批次开始时取得的数组；回调扩容存储、或在回调里让动效受保护，都不会绕过检查。同批更新中，A 的回调对尚未派发的 B 做了上述操作时，B 本批的 job 输出作废，B 的事件由那次新的求值派发。B 若已被本批标记回收、又被 A 的回调重新求值（例如设 `Time`），本批不会回收它。
- **粒度**是“一个登记的回调”：OnStart 每次 `WithOnStart` 调用算一个。原库的 `WithOnLoopComplete`/`WithOnComplete`/`WithOnCancel` 多次调用由原库合并成一个多播委托，按一个回调对待（见第 5 节）。
- **完成的发布**：本次求值到达终点时，在它之前要执行的用户代码运行期间，动效仍为 Playing，可以被取消。这些代码是 OnStart，以及受保护序列在最后一帧的子项与回调。
- **受保护序列的生命周期**：
  - 序列完成或取消时，先取消它的子动效并把序列对象归还池，然后才调用用户的 OnComplete/OnCancel。
  - 若某个子动效的回调重新打开了序列（例如设序列的 `Time`），清理随即停止：不再取消其余子项，不归还序列，也不调用用户的 OnComplete/OnCancel。
  - 归还前，旧动效与序列对象解除关系。之后再对旧句柄设 `Time`，不会驱动复用了该对象的新序列；用户回调仍留在旧动效上。
- **一致性**：除取代检查外，受保护的动效与同一个普通动效派发完全相同，包括写值、OnLoopComplete、OnComplete、OnCancel、异常去向（含 `CancelOnError`，以及 OnCancel 自身抛异常时向外抛出）和回收时机。`DispatchParityTest` 对三种入口逐个场景比较“加/不加一个空 OnStart”的事件序列。
- **异常处理器抛出**：异常照常向外传播，本次求值在抛出处结束。序列这次更新在其目标时间收束，未执行的回调保持上膛，下一次向前推进时执行。跨轮补发阶段的例外见第 5 节。

**只保证内存安全、不保证行为**（与 v2.0.2 相同，不支持）：在回调里调用 `MotionDispatcher.Clear()`、`ManualMotionDispatcher.Reset()` 等整体清空存储的操作。当次更新剩余部分的行为不作保证。若同批已有待回收的动效，批末回收会抛 `IndexOutOfRangeException`（v2.0.2 同样如此），也可能误删清空后新建的动效（v2.0.2 同样如此）。

普通动效保持 v2.0.2 的重入行为，不做取代检查。例如绑定回调里设自己的 `Time`，两个值都会写入；受保护动效的回调设一个普通动效的 `Time`，那个普通动效本批的 job 输出仍会再写一次。

---

## 5. 已知限制

1. **嵌套序列按自己的时间判断方向。** 裁决 B 要求嵌套序列跟随根序列方向（父 Yoyo 回程中，子序列回调按镜像位置触发），没有实现。现状：父序列回程时，子序列自己的时间在后退，它的回调只重新上膛，下一次去程再触发；父序列后退时，子序列的时间可能在前进，它的回调可能触发。
2. **普通序列中途变成受保护**：普通序列若在运行中因调用 `PlayBackwards` 等变成受保护，从那时起改用受保护序列的规则。其中可观察的是同一次更新内子项的求值顺序：起点恰好在新位置的子项与较早子项之间，由 `同点,较早` 变为 `较早,同点`，之后一直保持。
3. **非单调缓动依赖采样**：见 1.1。时间后退到更早的轮次时，该轮只按采样位置判断哪些回调已触发。
4. **原库回调的多播**：多次 `WithOnLoopComplete`（或 OnComplete/OnCancel）由原库合并为一个委托。第一个订阅者设了 `Time`，第二个仍会执行；第一个抛异常，第二个不执行（原库行为）。需要逐个保护时，请合并成一个回调自行处理。
5. **跨轮补发中的异常**：一次更新跨过整轮、在补发跳过轮次的回调时，回调抛异常而异常处理器也抛出，会留下未结束的序列更新。之后回退到该轮，可能发出本应保持上膛的回调；会写值的分支还可能把处理器抛出的异常再交给处理器一次。只在异常处理器自身抛异常时出现。
6. **回调里清空调度器**：见第 4 节，只保证内存安全；同批有待回收项时可能抛 `IndexOutOfRangeException`（v2.0.2 同样如此）。
7. **`ManualMotionDispatcher` 在回调里首次调度新的值类型**会抛 “Collection was modified”（v2.0.2 同样如此）。事先为该类型建好 runner 即可避免，例如提前调用 `dispatcher.EnsureStorageCapacity<TValue, TOptions, TAdapter>(n)`，或提前创建一个该类型的动效。
8. **版本号回绕**：
   - 求值版本号是一个**全局**的 32 位计数器，所有动效的创建、求值（job、`SetTime`、`Complete`）、取消都会消耗它。一次求值被误判为仍有效，需要同一个动效（同存储、同槽位版本、同重置代数）在这次求值的用户代码执行期间，恰好再次得到相同的版本号，也就是计数器在此期间整整回绕一圈（约 43 亿次操作）。
   - 目标登记号是全局的 24 位计数器。约 1677 万次登记之后会回绕，新登记可能与一个长期存活的旧登记同号；之后回收其中一个时，可能误删另一个的登记，使它不再被 `CancelAll`/`CompleteAll` 找到。这可以跨多次批量调用发生。
9. **丢弃未重置的 `ManualMotionDispatcher`**：它的动效永远不会被移除，其目标登记也会一直保留（动效本身在原库中同样不会被释放）。
10. **v2.0.2 自身的问题未修**：
   - `CallbackTest.Test_WithCancelOnError`、`DelayTest.Test_Delay_SkipValuesDuringDelay`、`SchedulerTest.Test_Scheduler_FixedUpdate` 在原版就失败；
   - 回调里 `Reset()` 后可能误删新建的动效；
   - 上面第 6、7 条。
11. **普通动效性能**：中位数全面优于 v2.0.2；存储占满时偶有慢轮次，作为已知限制接受。

    测量环境：Apple M4，Unity 2022.3.62f3 编辑器，Mono，Burst 1.8.21（同步编译）；空的 `float` 绑定，手动调度器（`ManualMotionDispatcher`）；基准测试为 `Tests/Benchmark/OrdinaryMotionBenchmark.cs`，复现步骤见 6.1 第 6 步。没有在打包环境（IL2CPP Player）和真实界面负载下测过。

    核对者独立复测：六对进程，组内轮换原版与 fork 的启动次序，每个进程 11 个样本，取六个进程中位数的中位数。setter 为每 100 万次赋值的 ms，更新折合每 100 次更新的 ms。

    | 场景（活跃数/槽数） | v2.0.2 | 本 fork | 变化 |
    |---|---:|---:|---:|
    | `PlaybackSpeed` 赋值 100 万次 | 6.651 | 5.749 | −13.6% |
    | 50/64 | 0.804 | 0.766 | −4.7% |
    | 64/64 | 0.815 | 0.791 | −2.9% |
    | 500/512 | 1.440 | 1.349 | −6.3% |
    | 10000/16384 | 12.485 | 8.940 | −28.4% |
    | 16384/16384 | 15.856 | 13.969 | −11.9% |

    占满场景（16384/16384）的情况：
    - 六轮里有一轮比同组原版慢 8.9%（17.030 对 15.635 ms），其余五轮快 9.9%–19.8%。
    - 相对中位绝对偏差：原版 0.75%，fork 3.00%。
    - 慢段出现在主线程的绑定阶段，期间没有记录到垃圾回收；限定为单个 job worker 时仍然存在。具体原因未定位。

    项目决定把这一项作为已知限制接受：界面动画的规模远小于这个场景，且中位数优于原版。

    <details>
    <summary>开发者此前的五轮测量与逐项试验记录</summary>

    单位为 ms。更新场景写作“活跃数/实际槽数”，耗时折合 100 次更新。每格为五次独立编辑器进程的中位数，方括号给出这五个进程中位数的最小值和最大值。

    | 场景 | v2.0.2 | 优化前 fork | 当前 fork | 当前相对 v2.0.2 |
    |---|---:|---:|---:|---:|
    | 直接赋速 100 万次 | 6.415 [6.391–6.551] | 7.834 [7.705–7.904] | 5.985 [5.886–6.037] | -6.7% |
    | 50/64 | 0.802 [0.793–0.817] | 0.797 [0.789–0.807] | 0.780 [0.770–0.804] | -2.7% |
    | 64/64 | 0.807 [0.796–0.814] | 0.821 [0.817–0.844] | 0.795 [0.773–0.824] | -1.5% |
    | 64/128 | 0.860 [0.855–0.877] | 0.810 [0.801–0.861] | 0.798 [0.781–0.820] | -7.2% |
    | 500/512 | 1.433 [1.428–1.458] | 1.489 [1.477–1.520] | 1.388 [1.366–1.497] | -3.1% |
    | 512/512 | 1.463 [1.454–1.480] | 1.539 [1.529–1.546] | 1.405 [1.374–1.521] | -4.0% |
    | 512/1024 | 1.669 [1.659–1.671] | 1.541 [1.533–1.547] | 1.415 [1.396–1.583] | -15.2% |
    | 5000/8192 | 7.134 [7.068–7.146] | 6.546 [6.524–6.612] | 5.212 [5.116–6.500] | -26.9% |
    | 8192/8192 | 8.521 [8.497–8.554] | 9.757 [9.724–9.760] | 7.681 [7.557–9.560] | -9.9% |
    | 8192/16384 | 11.492 [11.428–11.570] | 9.690 [9.666–9.810] | 7.738 [7.683–9.910] | -32.7% |
    | 10000/16384 | 12.437 [12.376–12.495] | 11.702 [11.670–11.749] | 8.983 [8.858–11.738] | -27.8% |
    | 16384/16384 | 15.801 [15.780–15.846] | 18.760 [18.549–18.852] | 14.288 [13.707–18.581] | -9.6% |

    三个版本使用相同的计时探针（现为 [OrdinaryMotionBenchmark](Tests/Benchmark/OrdinaryMotionBenchmark.cs)），串行交替启动。每个进程先赋速 20 万次，每组预热 100 次更新，再做 11 次计时取中位数。使用同一缓存空委托绑定 `float`，每步推进 0.001 秒，时长 100 秒。每轮轮换更新场景的次序。512 个及以下每次连测 1000 步再除以 10，其余每次测 100 步。容量按库的 2 倍扩容规则选取，64、512、8192 还在相同活跃数下分别测满容量与两倍容量。

    跨全部场景和版本，合并 55 个样本后的相对中位绝对偏差为 0.4%–13.6%。占满时，原版和当前 fork 的相对中位绝对偏差分别为 0.7% 和 13.6%。当前 fork 有两轮进程中位数超过原版，五轮中位数快 9.6%。这组数字覆盖编辑器中的核心更新与绑定调用。本次未计入 UGUI 布局、渲染，也未测 IL2CPP Player。批量调用和 count 上界在 Player 里同样存在，IL2CPP 的泛型共享、内联、异常处理代码和 Release 托管数据布局会影响收益大小。

    另将评审者原样提供的三场景探针在三个版本上各跑三个独立进程，仍交替启动。按 v2.0.2、优化前、优化后排列，setter 的中位数分别为 **6.273 / 7.735 / 6.049 ms**，10,000 个未满为 **12.324 / 11.749 / 8.688 ms**，16,384 个占满为 **15.623 / 18.558 / 13.535 ms**。这组占满的三个优化版进程中位数为 11.858、13.535、13.800 ms，三轮均快于对应原版。两套探针观测到的波动不同，波动原因尚未定位。

    **逐项试验与撤回记录**

    每行对比紧接的上一保留版本。同一探针在各阶段取 11 次中位数，逐项试验的小规模场景每次测 100 步。单次比较中的小差值有机器漂移，最终结果以上表五轮复测为准。最后一项恢复了绑定异常处理器在 catch 内执行的上下文，表中也记录了这次兼容性修整的计时成本。

    | 变更 | 场景 | 前 → 后（ms） | 变化 | 说明 |
    |---|---|---:|---:|---|
    | 将播放模式移入非托管状态，赋速复用句柄验证得到的数据地址 | setter | 7.846 → 7.300 | -7.0% | 省掉第二次数组定位 |
    | 值类型特化普通 Runner 入口 | 占满 | 18.901 → 18.230 | -3.6% | 入口、保护与强制绑定条件成为常量 |
    | 返回已验证的数据引用 | setter | 6.855 → 6.654 | -2.9% | 复用验证过程中算出的地址 |
    | 将绑定的异常捕获移到小方法 | 占满 | 18.248 → 17.352 | -4.9% | 缩小 Mono 异常处理区域 |
    | 让暂停和倒放模式独占一个字节 | setter | 6.727 → 6.580 | -2.2% | 清模式成为一次写零，原有两个生命周期标志合用一个字节 |
    | 一次把整批输出交给泛型派发 | 占满 | 17.566 → 13.363 | -23.9% | 将逐条进入派发改为一次批量调用，单条求值复用循环 |
    | 把模式清理合入访问检查 | setter | 6.467 → 6.474 | +0.1% | 未见收益，撤回 |
    | 把绑定的 try/catch 放回批循环 | 占满 | 13.363 → 22.320 | +67.0% | 循环显著变慢，撤回 |
    | 用抽象基类虚调用替换存储接口 | setter | 6.467 → 6.391 | -1.2% | 差值在噪声内，撤回 |
    | 验证存储类型时只传 ID | setter | 6.467 → 6.446 | -0.3% | 差值在噪声内，撤回 |
    | 沿内部取址链传只读句柄引用 | setter | 6.467 → 6.015 | -7.0% | 减少按值传递 12 字节句柄产生的复制 |
    | 在绑定的 catch 内调用异常处理器，返回是否成功 | 占满 | 13.385 → 14.248 | +6.4% | 保留异常处理器的调用位置，批循环处理各入口的取消规则 |

    </details>

12. **普通动效相对 v2.0.2 的每帧开销**。

    - 每批取一次求值版本。job 在求值或标记回收时仍给该动效写 4 字节版本号，这也覆盖随后在同批回调里首次开启保护的动效。
    - Runner 只遍历本批 `count` 个输出。对非序列项，它从当前托管数组读粘性保护位，多一次带索引检查的读取与分支。普通项使用批次捕获的引用，延续原版的重入语义。
    - 普通批次使用空值类型入口，一批调用一次 `Dispatch<TDispatch>`。每次实际写值经由小方法 `TryBind` 捕获绑定异常。循环、完成、错误取消和各入口时序都由同一派发体决定。
    - 批末对待回收项检查保护位。普通项直接按原规则移除。编辑器的托管数组步长仍为 96 字节，原版为 72 字节。非托管 `MotionState` 为 24 字节，`float` 动效步长为 104 字节，三版相同。
    - 直接赋速发生时，已有数据地址上多一次播放模式字节写零。暂停与倒放两位一起清除，托管数据里的粘性保护继续有效。

13. **分配**：以下情况会分配：
    - 多次 `WithOnStart`；
    - 值类型目标装箱；
    - 新目标首次登记，或同一目标增加并存登记时内部表扩容；
    - 池未预热时的数组租借；
    - 受保护序列首次启用保护时，某个事件（OnComplete 或 OnCancel）登记了两个以上用户回调，拆分该事件的委托时分配一次（构建时启用则在构建时，运行中通过 `PlayBackwards` 等启用则在启用时）。

---

## 6. 同步上游检查清单

### 6.1 验证步骤（Unity 2022.3.62f3，包副本）

不要让 Unity 直接打开仓库路径：2022.3 会改写旧格式的 `.meta`。

1. 建一个仓库外的临时工程。`Packages/manifest.json` 写入以下依赖（另加工程模板默认的引擎模块），并加 `"testables": ["com.annulusgames.lit-motion"]`：

   ```json
   "com.annulusgames.lit-motion": "file:/abs/path/to/package-copy",
   "com.unity.burst": "1.8.21",
   "com.unity.collections": "1.5.1",
   "com.unity.mathematics": "1.2.6",
   "com.unity.test-framework": "1.3.3",
   "com.unity.test-framework.performance": "3.0.3",
   "com.unity.textmeshpro": "3.2.0-pre.15",
   "com.unity.ugui": "1.0.0"
   ```

   `com.unity.test-framework.performance` 不能省：原库自带的基准测试程序集（`Tests/Benchmark`）引用 `Unity.PerformanceTesting`，缺它会编译失败。TextMeshPro 供 `Runtime/Extensions/TextMeshPro` 使用，验证用的是上面这个版本。

2. 把包复制出去：

   ```sh
   rsync -a --delete src/LitMotion/Assets/LitMotion/ /abs/path/to/package-copy/
   ```

3. 两种模式各跑一次：

   ```sh
   Unity -batchmode -projectPath <工程> -runTests -testPlatform PlayMode -testResults pm.xml -logFile pm.log
   Unity -batchmode -projectPath <工程> -runTests -testPlatform EditMode -testResults em.xml -logFile em.log
   ```

4. 再建一个指向原版（合并前上游）包副本的同配置工程，同样跑两种模式，作为基线。
5. 把 `Tests/Runtime/LegacyParityTest.cs` 临时复制进原版包副本，用 `-testFilter LitMotion.Tests.Runtime.LegacyParityTest` 运行；它必须在原版上也全部通过。
6. 计时：`Tests/Benchmark/OrdinaryMotionBenchmark.cs`（只用公开 API）。它标有 `[Explicit]`，常规全量运行时不执行、结果里记为 Skipped；按名称选中才运行：

   ```sh
   Unity -batchmode -projectPath <工程> -runTests -testPlatform PlayMode \
     -testFilter LitMotion.Tests.Benchmark.OrdinaryMotionBenchmark --burst-force-sync-compilation \
     -testResults bench.xml -logFile bench.log
   ```

   每组的中位数写在用例输出里（`setter: median=…`、`10000/16384: median=…`），各样本也记入 Performance Testing 的结果。与其他版本（如原版）比较时，把这个文件复制进对方包副本的 `Tests/Benchmark/`，用相同配置的工程，交替启动进程（每版至少 3 个），取每个进程的中位数再取中位数。

### 6.2 通过标准

- 原库的测试按完整名称逐项比对：结果与基线完全相同。v2.0.2 有 3 个已知失败，见第 5 节第 10 条。PlayMode 结果里另有 1 个 Skipped，是上面的显式基准测试。
- 所有 fork 测试通过，PlayMode 和 EditMode 都包括在内。
- 回归范围至少包括：
  - `LegacyParityTest`、`DispatchParityTest`（三入口事件对照）；
  - `SequenceCallbackTest`、`ReentrancyTest`；
  - `MotionStartCallbackTest`、`MotionTargetTest`、`PlaybackControlTest`；
  - EditMode 的 `MotionTargetPlayModeTest`。
- 三入口事件对照：`DispatchParityTest` 比较受保护和普通动效的事件序列；如果上游改了派发，还要用普通动效在新旧两个包上各跑一遍相同场景，逐条比较事件轨迹。

当前结果（风格整理之后，隔离工程，按用例全名逐项比对）：PlayMode 共 388 项，384 通过，3 个为上面的原库已知失败，1 个是默认跳过的显式基准测试；EditMode 2/2。

以下两段是历史记录，数字是当时的口径。

性能优化那一轮在隔离工程中按用例全名和结果逐项比对。优化前后 PlayMode 均为 378/381，EditMode 均为 2/2，三个已知失败的报错消息也相同。全部 12 项试验（含撤回候选）均通过逐项比对。原版的 137 个 PlayMode 用例有 134 个通过，其余三个失败与 fork 一致。重点回归包括 11 个 `LegacyParityTest`、54 个 `DispatchParityTest` 和 45 个 `ReentrancyTest`。另将评审者的 20 个普通动效派发探针跑在原版和优化版，三入口的输出轨迹逐条相同。独立计数探针还核对了 64 和 16,384 个普通绑定每步都各写一次。

其后一次修复（`SetTime` 与显式 `Complete()` 改为在求值返回之后读取托管条目）之后：PlayMode 384/387（当时基准测试还不在全量列表里），137 个原库用例与 v2.0.2 按全名逐项相同；EditMode 2/2；`LegacyParityTest` 增至 15 个，原样在 v2.0.2 上也是 15/15。

### 6.3 原文件改动与合并判断

所有改动处都带 `// [fork]` 标记，可用 `grep -rn "\[fork\]" Runtime` 检索。

| 原文件 | 改动 | 上游原意 / 我们为什么改 / 合并时怎么判断 |
|---|---|---|
| `Internal/MotionStorage.cs` | `Dispatch<TDispatch>` 统一 Runner、`SetTime` 和 `TryCompleteCore` 的绑定、异常处理及生命周期通知；各入口的不同规则是 `IMotionDispatch` 的属性（`Internal/MotionDispatch.cs`） | Runner 交付整批输出。普通项在泛型循环中直接派发。受保护项先取身份令牌，必要时执行倒放停靠，再以单条结果进入同一循环。`SetTime` 和 `Complete` 也传入单条结果。普通项保留旧数组引用，每条的保护标志从当前数组读。上游改动任何入口时，把对应规则合入这一个循环，保留各入口决定循环通知的时机。 |
| 同上 | `GetVerifiedData` 返回通过槽位版本、稠密索引和序列访问检查的引用 | `GetDataRef` 直接使用已验证的数据地址，内部取址链按 `in MotionHandle` 传递只读句柄，其他需要槽位的调用也沿用这一份验证。 |
| 同上 | `TryCompleteCore` 新返回码 5 | 回调取代了受保护动效的这次完成：`TryComplete` 返回 false，`Complete()` 不抛。上游若新增返回码，避开 5。 |
| 同上 | `Create` 初始化 `Extension` 与 `EvaluationVersion`；`TryCancelCore` 更新 `EvaluationVersion` | 新功能的每动效状态与求值版本号。取消会取代进行中的求值。 |
| 同上 | `RemoveAll` 新参数（本批版本号）：受保护且被本批回调重新求值的动效不回收 | 上游 `RemoveAll` 原意是移除 job 标记的条目。上游改 `RemoveAll` 时，保留这一检查，以及 v2.0.2 语句顺序（越界时与原版同样抛出）。 |
| 同上 | `RemoveAt`、`Reset` 调用 `MotionExtensionHooks.OnRemoved`；`Reset` 递增 `generation` | 动效被移除时释放目标登记。`generation` 让重置前的求值身份失效（重置会把槽位版本归 1）。上游若新增移除动效的路径，也要调用 `OnRemoved`。上游 `main` 已把 `UpdateRunner.Reset` 改为带时间基准的版本，合并时两者都保留：上游的时间基准在 runner，我们的两行在 storage。 |
| 同上 | 存储接口新增 `StopAtStart`、`HasSlot`、`CaptureEvaluation`、`IsCurrent` | 用于倒放立即停靠（与 Runner 同规则并强制写起点值）、越界句柄判定，以及序列获取和检查自身求值。 |
| `Internal/UpdateRunner.cs` | 将本批数据与输出指针、`count` 一次传给 `storage.DispatchRunnerResults(...)`；生成本批 `evaluationVersion`，传给 job 和 `RemoveAll` | 输出 span 的长度就是本批数量。派发循环位于存储中，保留 `count` 上界以修复越界读（第 3 节）。**合并时**：上游对循环体的改动按 Runner 规则并入 `Dispatch()`；保留 `count` 上界。 |
| `MotionUpdateJob.cs` | 新字段 `EvaluationVersion`；求值后、以及标记回收时，写入 `state.EvaluationVersion` | 让 Runner 判断 job 之后动效是否被回调重新求值（同批作废、回收作废）。上游改 job 时保留这两行写入。 |
| `Internal/MotionData.cs` | `MotionState` 新字段 `uint EvaluationVersion` | 放在原有的 4 字节对齐填充里。`IsPreserved` 和 `IsInSequence` 合用一个字节，腾出的一个字节保存暂停、倒放两种模式。创建时清零，显式赋速只写速度和模式字节。结构大小不变。上游若向 `MotionState` 加字段，注意大小变化（不影响正确性）。 |
| `Internal/ManagedMotionData.cs` | 字段 `Extension`（`MotionExtensionData`，24 字节） | 新功能的每动效状态，随动效条目一起清零。 |
| `Internal/MotionManager.cs` | 新增 `StopAtStart`、`IsValidHandle`、`CaptureEvaluation`、`IsCurrent`，取数据时按 `in` 传递句柄 | 转发到存储。`IsValidHandle` 是新公开入口专用的越界安全版 `IsValid`，原 `IsValid` 保持 v2.0.2 行为。 |
| `MotionHandle.cs` | `PlaybackSpeed` setter 在已验证的 `MotionState` 上调用 `AssignPlaybackSpeed` | 一次定位完成赋速和播放模式清零。保留序列成员写入检查，独立的粘性保护标志继续有效。 |
| `MotionBuilder.cs` | `MotionBuilderBuffer.OnStartAction` 字段及其在 `Return` 中的清零；`CheckBuffer()` 改为 `internal` | `WithOnStart` 的暂存位置；`WithOnStart` 复用原有的构建器检查。 |
| `MotionSequenceBuilder.cs` | `MotionSequenceBuilderSource` 新增回调字段和方法（类末尾一段，`AppendCallback`、`InsertCallback`、`OnChildAdded`、`TransferCallbacks`、`ReleaseCallbacks`）；`Return` 里 `ReleaseCallbacks()`；`Append`/`Insert` 里 `OnChildAdded(handle)`；`Schedule` 里 `TransferCallbacks(source)`；`MotionSequenceBuilder.CheckIsDisposed()` 改为 `internal` | 构建期收集回调与“子项是否受保护”：在加入时记录，`Run()` 时不再扫描子项，不会因失效子项抛异常。上游新增加入子项的方法时，也要调用 `OnChildAdded`。 |
| `MotionSequenceSource.cs` | `partial`（回调逻辑在同目录的 `MotionSequenceSource.Callbacks.cs`，全部字段仍在本文件）；新增 `MotionSequenceCallback`；`Return` 里 `ReleaseCallbacks()`；`Time` setter 加 `BeginCallbacks`/`IsCurrent`/`InvokeCallbacks`/`EndCallbacks`，外包 `try/finally AbortCallbacks`；预写循环对受保护序列不包括起点恰好等于播放头的子项；`OnComplete`/`OnCancel` 每取消一个子项后检查 `IsCurrent`，并在归还后调用用户的 OnComplete/OnCancel | setter 原意是按位置把父时间分发给子动效；我们在子动效之间插入本层回调，并在每段用户代码后检查更新是否仍有效。`OnComplete`/`OnCancel` 原意是取消全部子项并归还；对受保护序列，用户回调已从动效的委托里拆出，由这里在清理之后调用，清理被取代时不调用。对普通序列，这些调用都是空操作。**合并时**：上游若改了 setter 的遍历顺序或子项求值方式，回调插入点要跟着调整，插在“对位置 ≥ 回调位置的子项求值之前”。 |

新增文件按原库的目录习惯放置：

- 对外接口：`Runtime/MotionHandlePlaybackExtensions.cs`、`Runtime/MotionStartCallbackExtensions.cs`、`Runtime/MotionTargetExtensions.cs`、`Runtime/LMotion.Target.cs`、`Runtime/SequenceCallbackExtensions.cs`；
- 序列回调逻辑：`Runtime/MotionSequenceSource.Callbacks.cs`（`MotionSequenceSource` 的 partial）；
- 内部类型：`Runtime/Internal/` 下的 `MotionDispatch.cs`、`MotionEvaluation.cs`、`MotionExtensionData.cs`、`MotionExtensionHooks.cs`、`MotionStartCallbacks.cs`、`MotionTargetRegistry.cs`；
- 测试：`Tests/Runtime/` 下的 `DispatchParityTest.cs`、`GCAllocationCounter.cs`、`LegacyParityTest.cs`、`MotionStartCallbackTest.cs`、`MotionTargetTest.cs`、`PlaybackControlTest.cs`、`ReentrancyTest.cs`、`SequenceCallbackTest.cs`，`Tests/Editor/MotionTargetPlayModeTest.cs`，`Tests/Benchmark/OrdinaryMotionBenchmark.cs`；
- 本文件 `README.md`（包根目录）。
