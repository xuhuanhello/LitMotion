namespace LitMotion
{
    /// <summary>
    /// The ways a motion is evaluated: by the update runner after the job, by MotionHandle.Time, by Complete().
    /// </summary>
    internal enum MotionEvaluationEntry : byte
    {
        Runner,
        SetTime,
        Complete,
    }

    /// <summary>
    /// The rules of <c>MotionStorage.Dispatch</c> that depend on the entry. They are those of the three code paths of v2.0.2.
    /// </summary>
    /// <remarks>
    /// Each rule applies when the dispatch reaches its stage. A guarded evaluation that user code superseded stops at the next
    /// check (<c>MotionStorage.IsCurrent</c>) and reaches no later stage, whatever these rules say.
    /// Implemented by value types, so that each one specializes the same Dispatch body: the rules of the runner batch are constants.
    /// </remarks>
    internal interface IMotionDispatch
    {
        bool IsRunnerBatch { get; }
        bool IsGuarded { get; }
        MotionEvaluation Evaluation { get; }

        // Bind stage: binds the value even in a delay with SkipValuesDuringDelay.
        bool AlwaysBinds { get; }

        // OnLoopComplete stage: whether it is invoked was decided before the bind (so even if the value was not bound).
        bool DecidesLoopBeforeBind { get; }

        // Bind stage, when the bind throws: cancels the motion if CancelOnError is set; StopsAfterCancel: then dispatches nothing else.
        bool CancelsOnError { get; }
        bool StopsAfterCancel { get; }

        // Bind stage, when the value is not bound: dispatches nothing else.
        bool StopsWhenNotBound { get; }

        // Start: goes on even if the motion is no longer evaluated. OnComplete stage: invokes OnComplete even if the motion did not
        // just complete. So a plain explicit Complete() binds and completes a motion that the adapter canceled, as in v2.0.2.
        bool AlwaysCompletes { get; }
    }

    internal readonly struct RunnerBatchDispatch : IMotionDispatch
    {
        public bool IsRunnerBatch => true;
        public bool IsGuarded => false;
        public MotionEvaluation Evaluation => default;
        public bool AlwaysBinds => false;
        public bool DecidesLoopBeforeBind => true;
        public bool CancelsOnError => true;
        public bool StopsAfterCancel => false;
        public bool StopsWhenNotBound => false;
        public bool AlwaysCompletes => false;
    }

    internal readonly struct EvaluatedDispatch : IMotionDispatch
    {
        readonly MotionEvaluationEntry entry;
        readonly bool bindAlways;

        public EvaluatedDispatch(MotionEvaluationEntry entry, in MotionEvaluation evaluation, bool bindAlways = false)
        {
            this.entry = entry;
            this.bindAlways = bindAlways;
            Evaluation = evaluation;
        }

        public bool IsRunnerBatch => false;
        public bool IsGuarded => Evaluation.IsGuarded;
        public MotionEvaluation Evaluation { get; }
        public bool AlwaysBinds => entry == MotionEvaluationEntry.Complete || bindAlways;
        public bool DecidesLoopBeforeBind => entry == MotionEvaluationEntry.Runner;
        public bool CancelsOnError => entry != MotionEvaluationEntry.Complete;
        public bool StopsAfterCancel => entry == MotionEvaluationEntry.SetTime;
        public bool StopsWhenNotBound => entry == MotionEvaluationEntry.SetTime;
        public bool AlwaysCompletes => entry == MotionEvaluationEntry.Complete;
    }
}
