#if DEVELOPMENT_BUILD || UNITY_EDITOR
#define LITMOTION_DEBUG
#endif

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices; // [fork] single-evaluation spans
using LitMotion.Collections;
using Unity.Collections;

namespace LitMotion
{
    internal record MotionDebugInfo
    {
        public object StartValue;
        public object EndValue;
        public object Options;
    }

    internal interface IMotionStorage
    {
        bool IsValid(MotionHandle handle);
        bool IsActive(MotionHandle handle);
        bool IsPlaying(MotionHandle handle);
        bool TryCancel(MotionHandle handle, bool checkIsInSequence = true);
        bool TryComplete(MotionHandle handle, bool checkIsInSequence = true);
        void Cancel(MotionHandle handle, bool checkIsInSequence = true);
        void Complete(MotionHandle handle, bool checkIsInSequence = true);
        void SetTime(MotionHandle handle, double time, bool checkIsInSequence = true);
        void StopAtStart(MotionHandle handle); // [fork]
        bool HasSlot(int index); // [fork]
        MotionEvaluation CaptureEvaluation(MotionHandle handle); // [fork]
        bool IsCurrent(in MotionEvaluation evaluation); // [fork]
        ref MotionData GetDataRef(in MotionHandle handle, bool checkIsInSequence = true); // [fork] forward the readonly handle
        ref ManagedMotionData GetManagedDataRef(MotionHandle handle, bool checkIsInSequence = true);
        void AddToSequence(MotionHandle handle, out double motionDuration);
        MotionDebugInfo GetDebugInfo(MotionHandle handle);
        void Reset();
    }

    internal sealed class MotionStorage<TValue, TOptions, TAdapter> : IMotionStorage
        where TValue : unmanaged
        where TOptions : unmanaged, IMotionOptions
        where TAdapter : unmanaged, IMotionAdapter<TValue, TOptions>
    {
        const int InitialCapacity = 32;

        public int Id { get; }
        public int Count => tail;

        SparseSetCore sparseSetCore = new(InitialCapacity);
        SparseIndex[] sparseIndexLookup = new SparseIndex[InitialCapacity];
        MotionData<TValue, TOptions>[] unmanagedDataArray = new MotionData<TValue, TOptions>[InitialCapacity];
        ManagedMotionData[] managedDataArray = new ManagedMotionData[InitialCapacity];
        AllocatorHelper<RewindableAllocator> allocator;
        int tail;
        int generation; // [fork] incremented by Reset(), see MotionEvaluation

        public MotionStorage(int id)
        {
            Id = id;
            allocator = RewindableAllocatorFactory.CreateAllocator();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<MotionData<TValue, TOptions>> GetDataSpan()
        {
            return unmanagedDataArray.AsSpan();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<ManagedMotionData> GetManagedDataSpan()
        {
            return managedDataArray.AsSpan();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureCapacity(int minimumCapacity)
        {
            sparseSetCore.EnsureCapacity(minimumCapacity);
            ArrayHelper.EnsureCapacity(ref sparseIndexLookup, minimumCapacity);
            ArrayHelper.EnsureCapacity(ref unmanagedDataArray, minimumCapacity);
            ArrayHelper.EnsureCapacity(ref managedDataArray, minimumCapacity);
        }

        public unsafe MotionHandle Create(ref MotionBuilder<TValue, TOptions, TAdapter> builder)
        {
            EnsureCapacity(tail + 1);
            var buffer = builder.buffer;

            ref var dataRef = ref unmanagedDataArray[tail];
            ref var managedDataRef = ref managedDataArray[tail];

            ref var state = ref dataRef.Core.State;
            ref var parameters = ref dataRef.Core.Parameters;

            state.Status = MotionStatus.Scheduled;
            state.Time = 0;
            state.AssignPlaybackSpeed(1f); // [fork] initialize speed and clear playback modes
            state.IsPreserved = false;
            state.EvaluationVersion = MotionEvaluation.NextVersion(); // [fork]

            parameters.TimeKind = buffer.TimeKind;
            parameters.Duration = buffer.Duration;
            parameters.Delay = buffer.Delay;
            parameters.DelayType = buffer.DelayType;
            parameters.Ease = buffer.Ease;
            parameters.Loops = buffer.Loops;
            parameters.LoopType = buffer.LoopType;
            dataRef.StartValue = buffer.StartValue;
            dataRef.EndValue = buffer.EndValue;
            dataRef.Options = buffer.Options;

            if (buffer.Ease == Ease.CustomAnimationCurve)
            {
                if (parameters.AnimationCurve.IsCreated)
                {
                    parameters.AnimationCurve.CopyFrom(buffer.AnimationCurve);
                }
                else
                {
#if LITMOTION_COLLECTIONS_2_0_OR_NEWER
                    parameters.AnimationCurve = new NativeAnimationCurve(buffer.AnimationCurve, allocator.Allocator.Handle);
#else
                    parameters.AnimationCurve = new UnsafeAnimationCurve(buffer.AnimationCurve, allocator.Allocator.Handle);
#endif
                }
            }

            managedDataRef.CancelOnError = buffer.CancelOnError;
            managedDataRef.SkipValuesDuringDelay = buffer.SkipValuesDuringDelay;
            managedDataRef.UpdateAction = buffer.UpdateAction;
            managedDataRef.OnLoopCompleteAction = buffer.OnLoopCompleteAction;
            managedDataRef.OnCancelAction = buffer.OnCancelAction;
            managedDataRef.OnCompleteAction = buffer.OnCompleteAction;
            managedDataRef.Extension = new MotionExtensionData(buffer.OnStartAction); // [fork]
            managedDataRef.StateCount = buffer.StateCount;
            managedDataRef.State0 = buffer.State0;
            managedDataRef.State1 = buffer.State1;
            managedDataRef.State2 = buffer.State2;

#if LITMOTION_DEBUG
            managedDataRef.DebugName = buffer.DebugName;
#endif

            if (buffer.ImmediateBind && buffer.UpdateAction != null)
            {
                managedDataRef.UpdateUnsafe(
                    default(TAdapter).Evaluate(
                        ref dataRef.StartValue,
                        ref dataRef.EndValue,
                        ref dataRef.Options,
                        new()
                        {
                            Progress = parameters.Ease switch
                            {
                                Ease.CustomAnimationCurve => buffer.AnimationCurve.Evaluate(0f),
                                _ => EaseUtility.Evaluate(0f, parameters.Ease)
                            },
                            Time = state.Time,
                        }
                ));
            }

            var sparseIndex = sparseSetCore.Alloc(tail);
            sparseIndexLookup[tail] = sparseIndex;

            tail++;

            return new MotionHandle()
            {
                Index = sparseIndex.Index,
                Version = sparseIndex.Version,
                StorageId = Id
            };
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void RemoveAt(int denseIndex)
        {
            tail--;

            MotionExtensionHooks.OnRemoved(ref managedDataArray[denseIndex]); // [fork] releases the SetTarget registration

            // swap elements
            unmanagedDataArray[denseIndex] = unmanagedDataArray[tail];
            unmanagedDataArray[tail] = default;
            managedDataArray[denseIndex] = managedDataArray[tail];
            managedDataArray[tail] = default;

            // swap sparse index
            var prevSparseIndex = sparseIndexLookup[denseIndex];
            var currentSparseIndex = sparseIndexLookup[denseIndex] = sparseIndexLookup[tail];
            sparseIndexLookup[tail] = default;

            // update slot
            if (currentSparseIndex.Version != 0)
            {
                ref var slot = ref sparseSetCore.GetSlotRefUnchecked(currentSparseIndex.Index);
                slot.DenseIndex = denseIndex;
            }

            // free slot
            if (prevSparseIndex.Version != 0)
            {
                sparseSetCore.Free(prevSparseIndex);
            }
        }

        /// <param name="evaluationVersion">[fork] Version given by the job to the motions of this update (see MotionEvaluation).</param>
        public void RemoveAll(NativeList<int> denseIndexList, uint evaluationVersion)
        {
            var list = new NativeArray<SparseIndex>(denseIndexList.Length, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < list.Length; i++)
            {
                list[i] = sparseIndexLookup[denseIndexList[i]];
            }

            for (int i = 0; i < list.Length; i++)
            {
                var denseIndex = sparseSetCore.GetSlotRefUnchecked(list[i].Index).DenseIndex;

                // [fork] A guarded motion that a callback of this update evaluated again (e.g. moved back to life) is not removed.
                if (!IsDenseIndexOutOfRange(denseIndex) && managedDataArray[denseIndex].Extension.IsGuarded &&
                    !IsCurrent(EvaluationAt(denseIndex, evaluationVersion), ref denseIndex)) continue;

                RemoveAt(denseIndex);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsValid(MotionHandle handle)
        {
            ref var slot = ref sparseSetCore.GetSlotRefUnchecked(handle.Index);
            if (IsDenseIndexOutOfRange(slot.DenseIndex)) return false;
            if (IsInvalidVersion(slot.Version, handle)) return false;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsActive(MotionHandle handle)
        {
            ref var slot = ref sparseSetCore.GetSlotRefUnchecked(handle.Index);
            if (IsDenseIndexOutOfRange(slot.DenseIndex)) return false;
            if (IsInvalidVersion(slot.Version, handle)) return false;

            ref var state = ref unmanagedDataArray[slot.DenseIndex].Core.State;
            return state.Status is MotionStatus.Scheduled or MotionStatus.Delayed or MotionStatus.Playing ||
                (state.Status is MotionStatus.Completed && state.IsPreserved);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsPlaying(MotionHandle handle)
        {
            ref var slot = ref sparseSetCore.GetSlotRefUnchecked(handle.Index);
            if (IsDenseIndexOutOfRange(slot.DenseIndex)) return false;
            if (IsInvalidVersion(slot.Version, handle)) return false;

            ref var state = ref unmanagedDataArray[slot.DenseIndex].Core.State;
            return state.Status is MotionStatus.Scheduled or MotionStatus.Delayed or MotionStatus.Playing;
        }

        public bool TryCancel(MotionHandle handle, bool checkIsInSequence = true)
        {
            return TryCancelCore(handle, checkIsInSequence) == 0;
        }

        public void Cancel(MotionHandle handle, bool checkIsInSequence = true)
        {
            switch (TryCancelCore(handle, checkIsInSequence))
            {
                case 1:
                    Error.MotionNotExists();
                    return;
                case 2:
                    Error.MotionHasBeenCanceledOrCompleted();
                    return;
                case 3:
                    Error.MotionIsInSequence();
                    return;
            }
        }

        int TryCancelCore(MotionHandle handle, bool checkIsInSequence)
        {
            ref var slot = ref sparseSetCore.GetSlotRefUnchecked(handle.Index);
            var denseIndex = slot.DenseIndex;
            if (IsDenseIndexOutOfRange(denseIndex) || IsInvalidVersion(slot.Version, handle))
            {
                return 1;
            }

            ref var dataRef = ref unmanagedDataArray[denseIndex];
            ref var state = ref dataRef.Core.State;

            if (state.Status is MotionStatus.None or MotionStatus.Canceled ||
                (state.Status is MotionStatus.Completed && !state.IsPreserved))
            {
                return 2;
            }

            if (checkIsInSequence && state.IsInSequence)
            {
                return 3;
            }

            state.Status = MotionStatus.Canceled;
            state.EvaluationVersion = MotionEvaluation.NextVersion(); // [fork] supersedes the evaluations in progress

            ref var managedData = ref managedDataArray[denseIndex];
            managedData.InvokeOnCancel();

            return 0;
        }

        public bool TryComplete(MotionHandle handle, bool checkIsInSequence = true)
        {
            return TryCompleteCore(handle, checkIsInSequence) == 0;
        }

        public void Complete(MotionHandle handle, bool checkIsInSequence = true)
        {
            switch (TryCompleteCore(handle, checkIsInSequence))
            {
                case 1:
                    Error.MotionNotExists();
                    return;
                case 2:
                    Error.MotionHasBeenCanceledOrCompleted();
                    return;
                case 3:
                    Error.MotionIsInSequence();
                    return;
                case 4:
                    throw new InvalidOperationException("Complete was ignored because it is not possible to complete a motion that loops infinitely. If you want to end the motion, call Cancel() instead.");
                // case 5 [fork]: a callback (OnStart, the bind, OnLoopComplete) moved, canceled or completed the motion
                // before this completion ended: TryComplete() returns false, Complete() does not throw.
            }
        }

        int TryCompleteCore(MotionHandle handle, bool checkIsInSequence)
        {
            ref var slot = ref sparseSetCore.GetSlotRefUnchecked(handle.Index);

            if (IsDenseIndexOutOfRange(slot.DenseIndex) || IsInvalidVersion(slot.Version, handle))
            {
                return 1;
            }

            ref var dataRef = ref unmanagedDataArray[slot.DenseIndex];
            ref var state = ref dataRef.Core.State;
            ref var parameters = ref dataRef.Core.Parameters;

            if (state.Status is MotionStatus.None or MotionStatus.Canceled or MotionStatus.Completed)
            {
                return 2;
            }

            if (checkIsInSequence && state.IsInSequence)
            {
                return 3;
            }

            if (parameters.Loops < 0)
            {
                return 4;
            }

            // [fork] shared dispatch, see Dispatch()
            var denseIndex = slot.DenseIndex;
            dataRef.Core.State.EvaluationVersion = MotionEvaluation.NextVersion();
            var evaluation = BeginEvaluation(denseIndex);

            // OnStart (if the motion has not started yet) runs before the motion is marked as completed.
            if (evaluation.IsGuarded && managedDataArray[denseIndex].Extension.OnStart != null)
            {
                if (!InvokeOnStart(evaluation, ref denseIndex)) return 5;
                dataRef = ref unmanagedDataArray[denseIndex];
            }

            dataRef.Complete<TAdapter>(out var result);

            // As in v2.0.2, the managed entry is read after the evaluation: the adapter may have grown the storage (new arrays).
            // (A guarded evaluation re-reads it anyway after checking that it is still current.)
            ref var managedData = ref managedDataArray[evaluation.IsGuarded ? denseIndex : slot.DenseIndex];
            return Dispatch(new EvaluatedDispatch(MotionEvaluationEntry.Complete, evaluation), denseIndex, ref dataRef, ref managedData, result) ? 0 : 5;
        }

        public void SetTime(MotionHandle handle, double time, bool checkIsInSequence = true)
        {
            SetTime(handle, time, checkIsInSequence, false); // [fork]
        }

        /// <summary>
        /// [fork] Whether a slot exists at this index (a handle built by hand may have any index).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasSlot(int index)
        {
            return index >= 0 && index < sparseSetCore.Capacity;
        }

        /// <summary>
        /// [fork] PlayBackwards() on a motion that is at (or before) its start: evaluates it at its start and binds the value,
        /// even in a delay with SkipValuesDuringDelay (as the runner does when it reaches the start, see Dispatch()).
        /// </summary>
        public void StopAtStart(MotionHandle handle)
        {
            SetTime(handle, MotionExtensionHooks.GetStartTime(ref GetDataRef(handle)), checkIsInSequence: true, bindAlways: true);
        }

        void SetTime(MotionHandle handle, double time, bool checkIsInSequence, bool bindAlways) // [fork] bindAlways
        {
            ref var slot = ref sparseSetCore.GetSlotRefUnchecked(handle.Index);

            var denseIndex = slot.DenseIndex;
            if (IsDenseIndexOutOfRange(denseIndex)) Error.MotionNotExists();

            var version = slot.Version;
            if (version <= 0 || version != handle.Version) Error.MotionNotExists();

            ref var dataRef = ref unmanagedDataArray[denseIndex];
            if (checkIsInSequence && dataRef.Core.State.IsInSequence) Error.MotionIsInSequence();

            // [fork] shared dispatch, see Dispatch()
            dataRef.Core.State.EvaluationVersion = MotionEvaluation.NextVersion();
            var evaluation = BeginEvaluation(denseIndex);
            dataRef.Update<TAdapter>(time, out var result);

            // As in v2.0.2, the managed entry is read after the evaluation: the adapter may have grown the storage (new arrays).
            ref var managedData = ref managedDataArray[denseIndex];
            Dispatch(new EvaluatedDispatch(MotionEvaluationEntry.SetTime, evaluation, bindAlways), denseIndex, ref dataRef, ref managedData, result);
        }

        /// <summary>
        /// [fork] Publishes one completed job. Only the first count entries have an output.
        /// </summary>
        /// <param name="evaluationVersion">Version given by the job to the motions it evaluated.</param>
        /// <remarks>
        /// Whether the motion is guarded is read from the current arrays, not from the arrays the runner read before the batch:
        /// a callback of an earlier motion of the batch may have reallocated them, or made this motion guarded. A motion that is
        /// not guarded is dispatched exactly as in v2.0.2 (with the arrays of the runner).
        /// </remarks>
        public unsafe void DispatchRunnerResults(MotionData<TValue, TOptions>* data, TValue* output, int count, uint evaluationVersion)
        {
            Dispatch(default(RunnerBatchDispatch), 0, new Span<MotionData<TValue, TOptions>>(data, count),
                managedDataArray, new ReadOnlySpan<TValue>(output, count), evaluationVersion);
        }

        void DispatchGuardedRunnerResult(int denseIndex, uint evaluationVersion, TValue result)
        {
            // The evaluation of the job. If a callback of an earlier motion of the batch evaluated this motion again, the output
            // is stale and that evaluation has already dispatched the events.
            var evaluation = EvaluationAt(denseIndex, evaluationVersion);
            if (!IsCurrent(evaluation, ref denseIndex)) return;

            ref var dataRef = ref unmanagedDataArray[denseIndex];
            ref var managedData = ref managedDataArray[denseIndex];
            if (!IsEvaluated(dataRef.Core.State.Status)) return;

            var stoppedAtStart = MotionExtensionHooks.StopAtStartWhenPlayingBackwards<TValue, TOptions, TAdapter>(ref dataRef, ref managedData, ref result);
            Dispatch(new EvaluatedDispatch(MotionEvaluationEntry.Runner, evaluation, stoppedAtStart), denseIndex, ref dataRef, ref managedData, result);
        }

        /// <summary>
        /// [fork] Binds the value of an evaluation and invokes the lifecycle callbacks (OnLoopComplete, OnComplete).
        /// This is the single implementation of what UpdateRunner.Update, SetTime and TryCompleteCore did separately in v2.0.2;
        /// the rules that differed between them are kept as they were (see <see cref="IMotionDispatch"/>). Each rule applies when the
        /// dispatch reaches its stage:
        /// <list type="bullet">
        /// <item><description>Motion no longer evaluated (canceled after its evaluation, by the adapter or by an earlier callback of the
        /// runner batch): Runner and SetTime dispatch nothing; Complete goes on.</description></item>
        /// <item><description>Value bound: Runner and SetTime when Playing, Completed, or Delayed without SkipValuesDuringDelay;
        /// Complete in every case.</description></item>
        /// <item><description>Exception in the bind with CancelOnError: Runner cancels and still invokes OnLoopComplete/OnComplete;
        /// SetTime cancels and stops; Complete does not cancel. OnCancel is invoked unguarded (its exceptions propagate).</description></item>
        /// <item><description>OnLoopComplete: Runner when the loop count advanced (decided before the bind); SetTime only if the value was bound;
        /// Complete when the loop count advanced.</description></item>
        /// <item><description>OnComplete: Runner and SetTime when the motion has just completed; Complete in every case, so a plain
        /// explicit Complete() also completes a motion that the adapter canceled, as in v2.0.2.</description></item>
        /// </list>
        /// Motions that use a fork extension get a guarded evaluation (<see cref="MotionEvaluation.IsGuarded"/>): OnStart is invoked first,
        /// a guarded sequence dispatches its callbacks even when its value is not bound, and the evaluation is checked (see
        /// <see cref="IsCurrent"/>) before the bind and after each later piece of user code up to OnLoopComplete. Once user code has
        /// superseded it (canceled, completed or evaluated the motion again, or removed it), the dispatch stops at the next check
        /// and reaches no later stage, whatever the entry.
        /// Other motions are dispatched exactly as in v2.0.2.
        /// </summary>
        bool Dispatch(EvaluatedDispatch dispatch, int denseIndex, ref MotionData<TValue, TOptions> data, ref ManagedMotionData managed, TValue result)
        {
            // A single evaluation runs the loop body of the runner batch as a batch of one, so the rules exist only once.
            return Dispatch(dispatch, denseIndex, MemoryMarshal.CreateSpan(ref data, 1),
                MemoryMarshal.CreateSpan(ref managed, 1), MemoryMarshal.CreateReadOnlySpan(ref result, 1), 0);
        }

        // [fork] The loop body shared by the runner batch and the single evaluations (Runner, SetTime, Complete). A guarded result
        // of the runner batch leaves the batch and comes back as a single evaluation (DispatchGuardedRunnerResult).
        bool Dispatch<TDispatch>(TDispatch dispatch, int denseIndex, Span<MotionData<TValue, TOptions>> data,
            Span<ManagedMotionData> bindings, ReadOnlySpan<TValue> results, uint evaluationVersion)
            where TDispatch : struct, IMotionDispatch
        {
            for (var i = 0; i < results.Length; i++)
            {
                ref var dataRef = ref data[i];
                ref var managedData = ref bindings[i];
                ref readonly var result = ref results[i];
                if (dispatch.IsRunnerBatch)
                {
                    if (dataRef.Core.State.IsInSequence) continue;
                    if (managedDataArray[i].Extension.IsGuarded)
                    {
                        DispatchGuardedRunnerResult(i, evaluationVersion, result);
                        continue;
                    }
                }

                var status = dataRef.Core.State.Status;
                if (!IsEvaluated(status) && !dispatch.AlwaysCompletes)
                {
                    if (dispatch.IsRunnerBatch) continue;
                    return false;
                }

                var isCompleted = status == MotionStatus.Completed;
                if (dispatch.IsGuarded)
                {
                    if (!BeginGuardedDispatch(dispatch.Evaluation, ref denseIndex, isCompleted)) return false;

                    // A guarded evaluation is checked here (after the adapter and each OnStart callback), then after each later
                    // piece of user code: the bind, the callbacks of a sequence whose value is not bound, OnLoopComplete.
                    // After each check the refs are taken again: the motion may have moved, or the arrays may have been reallocated.
                    // A method cannot re-point the caller's ref locals (C# 9), so these two lines follow each check;
                    // spans and an index would allow a single call, but made the runner about 10% slower on large batches.
                    dataRef = ref unmanagedDataArray[denseIndex];
                    managedData = ref managedDataArray[denseIndex];
                }

                var loopCompletedBeforeBind = dispatch.DecidesLoopBeforeBind && dataRef.Core.State.WasLoopCompleted;

                if (dispatch.AlwaysBinds || status != MotionStatus.Delayed || !managedData.SkipValuesDuringDelay)
                {
                    if (!TryBind(ref managedData, result))
                    {
                        if (dispatch.IsGuarded)
                        {
                            if (!IsCurrent(dispatch.Evaluation, ref denseIndex)) return false;
                            dataRef = ref unmanagedDataArray[denseIndex];
                            managedData = ref managedDataArray[denseIndex];
                        }

                        if (dispatch.CancelsOnError && managedData.CancelOnError)
                        {
                            dataRef.Core.State.Status = MotionStatus.Canceled;
                            managedData.OnCancelAction?.Invoke();
                            if (dispatch.StopsAfterCancel) return false;
                        }
                    }

                    if (dispatch.IsGuarded)
                    {
                        if (!IsCurrent(dispatch.Evaluation, ref denseIndex)) return false;
                        dataRef = ref unmanagedDataArray[denseIndex];
                        managedData = ref managedDataArray[denseIndex];
                        if (isCompleted) EndCompletionHold(ref managedData, ref dataRef.Core.State);
                    }
                }
                else
                {
                    if (dispatch.IsGuarded && MotionExtensionHooks.DispatchSequenceCallbacksWithoutBinding(ref managedData))
                    {
                        if (!IsCurrent(dispatch.Evaluation, ref denseIndex)) return false;
                        dataRef = ref unmanagedDataArray[denseIndex];
                        managedData = ref managedDataArray[denseIndex];
                    }

                    if (dispatch.StopsWhenNotBound) return false;
                }

                if (dispatch.DecidesLoopBeforeBind ? loopCompletedBeforeBind : dataRef.Core.State.WasLoopCompleted)
                {
                    managedData.InvokeOnLoopComplete(dataRef.Core.State.CompletedLoops);
                    if (dispatch.IsGuarded)
                    {
                        if (!IsCurrent(dispatch.Evaluation, ref denseIndex)) return false;
                        dataRef = ref unmanagedDataArray[denseIndex];
                        managedData = ref managedDataArray[denseIndex];
                    }
                }

                if (dispatch.AlwaysCompletes || (isCompleted && dataRef.Core.State.WasStatusChanged))
                {
                    managedData.InvokeOnComplete();
                }
            }

            return true;
        }

        // [fork] Keep the exception region around one binding. A try/catch inside the batch loop is costly on Mono.
        // Dispatch applies the entry-specific cancellation policy after the exception handler returns.
        static bool TryBind(ref ManagedMotionData managedData, in TValue value)
        {
            try
            {
                managedData.UpdateUnsafe(value);
                return true;
            }
            catch (Exception ex)
            {
                MotionDispatcher.GetUnhandledExceptionHandler()?.Invoke(ex);
                return false;
            }
        }

        // [fork] Only guarded evaluations run this preparation before the bind:
        // - the evaluation stops if user code in the adapter superseded it;
        // - the completion reached by the evaluation is published after the user code that belongs to the frame before it:
        //   OnStart, and the update of a guarded sequence (its last callbacks and children). Meanwhile the motion is Playing,
        //   so that this code can still cancel it (as in Complete()). For a sequence the hold lasts until after the bind
        //   (EndCompletionHold).
        bool BeginGuardedDispatch(in MotionEvaluation evaluation, ref int denseIndex, bool isCompleted)
        {
            if (!IsCurrent(evaluation, ref denseIndex)) return false;

            var holdDuringBind = isCompleted && managedDataArray[denseIndex].Extension.IsGuardedSequence;
            if (MotionExtensionHooks.IsStartDue(ref unmanagedDataArray[denseIndex].Core, ref managedDataArray[denseIndex]))
            {
                if (isCompleted) unmanagedDataArray[denseIndex].Core.State.Status = MotionStatus.Playing;
                if (!InvokeOnStart(evaluation, ref denseIndex)) return false;
                if (isCompleted && !holdDuringBind) unmanagedDataArray[denseIndex].Core.State.Status = MotionStatus.Completed;
            }
            else if (holdDuringBind)
            {
                unmanagedDataArray[denseIndex].Core.State.Status = MotionStatus.Playing;
            }

            if (holdDuringBind) MotionExtensionHooks.HoldCompletion(evaluation);
            return true;
        }

        // [fork] After the bind of a guarded evaluation that reached the completion: see BeginGuardedDispatch.
        static void EndCompletionHold(ref ManagedMotionData managedData, ref MotionData.MotionState state)
        {
            if (!managedData.Extension.IsGuardedSequence) return;
            MotionExtensionHooks.ReleaseCompletionHold();
            if (state.Status == MotionStatus.Playing) state.Status = MotionStatus.Completed;
        }

        // [fork] Invokes the OnStart callbacks of a guarded evaluation one by one. Returns false if one of them superseded it.
        bool InvokeOnStart(in MotionEvaluation evaluation, ref int denseIndex)
        {
            var onStart = MotionExtensionHooks.TakeOnStart(ref managedDataArray[denseIndex]);
            var count = MotionStartCallbacks.GetCount(onStart);
            for (int i = 0; i < count; i++)
            {
                MotionExtensionHooks.InvokeCallback(MotionStartCallbacks.GetAt(onStart, i));
                if (!IsCurrent(evaluation, ref denseIndex)) return false;
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool IsEvaluated(MotionStatus status)
        {
            return status is MotionStatus.Delayed or MotionStatus.Playing or MotionStatus.Completed;
        }

        // [fork] A guarded evaluation for the guarded motions, an unguarded one for the others.
        // The current evaluation version of the motion is the one of this evaluation (set by SetTime or Complete just before).
        MotionEvaluation BeginEvaluation(int denseIndex)
        {
            if (!managedDataArray[denseIndex].Extension.IsGuarded) return default;
            return EvaluationAt(denseIndex, unmanagedDataArray[denseIndex].Core.State.EvaluationVersion);
        }

        // [fork]
        MotionEvaluation EvaluationAt(int denseIndex, uint evaluationVersion)
        {
            var sparseIndex = sparseIndexLookup[denseIndex];
            return new MotionEvaluation(Id, sparseIndex.Index, sparseIndex.Version, generation, evaluationVersion);
        }

        /// <summary>
        /// [fork] The current evaluation of a valid motion, for code outside the dispatch that runs user code on its behalf
        /// (the updates of a sequence).
        /// </summary>
        public MotionEvaluation CaptureEvaluation(MotionHandle handle)
        {
            ref var slot = ref GetSlotWithVarify(handle, false);
            return EvaluationAt(slot.DenseIndex, unmanagedDataArray[slot.DenseIndex].Core.State.EvaluationVersion);
        }

        /// <summary>
        /// [fork] See <see cref="IsCurrent(in MotionEvaluation, ref int)"/>. An unguarded evaluation is always current.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsCurrent(in MotionEvaluation evaluation)
        {
            if (!evaluation.IsGuarded) return true;
            var denseIndex = 0;
            return IsCurrent(evaluation, ref denseIndex);
        }

        // [fork] Whether a guarded evaluation is still current after user code: the motion still exists (same slot, no reset)
        // and has not been evaluated again or canceled since. Updates denseIndex to the current position of the motion;
        // the caller must re-read the motion (the arrays may have been reallocated).
        bool IsCurrent(in MotionEvaluation evaluation, ref int denseIndex)
        {
            if (evaluation.StorageGeneration != generation) return false;
            if (evaluation.SlotIndex < 0 || evaluation.SlotIndex >= sparseSetCore.Capacity) return false;

            ref var slot = ref sparseSetCore.GetSlotRefUnchecked(evaluation.SlotIndex);
            if (slot.Version != evaluation.SlotVersion || IsDenseIndexOutOfRange(slot.DenseIndex)) return false;
            if (unmanagedDataArray[slot.DenseIndex].Core.State.EvaluationVersion != evaluation.Version) return false;

            denseIndex = slot.DenseIndex;
            return true;
        }

        public void AddToSequence(MotionHandle handle, out double motionDuration)
        {
            ref var slot = ref GetSlotWithVarify(handle, true);
            ref var dataRef = ref unmanagedDataArray[slot.DenseIndex];

            if (dataRef.Core.State.Status is not MotionStatus.Scheduled)
            {
                throw new ArgumentException("Cannot add a running motion to a sequence.");
            }

            motionDuration = handle.TotalDuration;
            if (double.IsInfinity(motionDuration))
            {
                throw new ArgumentException("Cannot add an infinitely looping motion to a sequence.");
            }

            dataRef.Core.State.IsPreserved = true;
            dataRef.Core.State.IsInSequence = true;
        }

        public ref ManagedMotionData GetManagedDataRef(MotionHandle handle, bool checkIsInSequence = true)
        {
            ref var slot = ref GetSlotWithVarify(handle, checkIsInSequence);
            return ref managedDataArray[slot.DenseIndex];
        }

        public ref MotionData GetDataRef(in MotionHandle handle, bool checkIsInSequence = true)
        {
            // [fork] Reuse the validated data address, including for direct PlaybackSpeed assignments.
            ref var slot = ref sparseSetCore.GetSlotRefUnchecked(handle.Index);
            return ref GetVerifiedData(handle, in slot, checkIsInSequence).Core;
        }

        public MotionDebugInfo GetDebugInfo(MotionHandle handle)
        {
            ref var slot = ref GetSlotWithVarify(handle, false);
            ref var dataRef = ref unmanagedDataArray[slot.DenseIndex];

            return new()
            {
                StartValue = dataRef.StartValue,
                EndValue = dataRef.EndValue,
                Options = dataRef.Options,
            };
        }

        // [fork] The checks are in GetVerifiedData, which also returns the data: GetDataRef needs a single lookup.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        ref SparseSetCore.Slot GetSlotWithVarify(MotionHandle handle, bool checkIsInSequence = true)
        {
            ref var slot = ref sparseSetCore.GetSlotRefUnchecked(handle.Index);
            GetVerifiedData(handle, in slot, checkIsInSequence);
            return ref slot;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        ref MotionData<TValue, TOptions> GetVerifiedData(in MotionHandle handle, in SparseSetCore.Slot slot, bool checkIsInSequence)
        {
            var denseIndex = slot.DenseIndex;
            if (IsDenseIndexOutOfRange(denseIndex)) Error.MotionNotExists();

            ref var data = ref unmanagedDataArray[denseIndex];
            if (IsInvalidVersion(slot.Version, handle) || data.Core.State.Status == MotionStatus.None) Error.MotionNotExists();
            if (checkIsInSequence && data.Core.State.IsInSequence) Error.MotionIsInSequence();
            return ref data;
        }

        public void Reset()
        {
            for (int i = 0; i < tail; i++) MotionExtensionHooks.OnRemoved(ref managedDataArray[i]); // [fork]
            sparseSetCore.Reset();
            sparseIndexLookup.AsSpan().Clear();
            unmanagedDataArray.AsSpan().Clear();
            managedDataArray.AsSpan().Clear();
            tail = 0;
            generation++; // [fork]
            allocator.Allocator.Rewind();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool IsDenseIndexOutOfRange(int denseIndex)
        {
            return denseIndex < 0 || denseIndex >= tail;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool IsInvalidVersion(int version, MotionHandle handle)
        {
            return version <= 0 || version != handle.Version;
        }
    }
}