using System.Runtime.CompilerServices;

namespace LitMotion
{
    /// <summary>
    /// An evaluation of a guarded motion (see <see cref="MotionExtensionData.IsGuarded"/>). The default value is an unguarded evaluation.
    /// </summary>
    /// <remarks>
    /// The evaluation is identified by the motion (storage, slot index and version), the reset generation of its storage
    /// (a reset gives the slots their first version again) and the evaluation version of the motion
    /// (<c>MotionData.MotionState.EvaluationVersion</c>), which changes whenever the motion is evaluated again (job, SetTime,
    /// Complete) or canceled. This is the single test of "is this evaluation still valid" (<c>MotionStorage.IsCurrent</c>,
    /// <see cref="MotionManager.IsCurrent"/>): the dispatch after user code, the publication of a job output, the removal at
    /// the end of a runner update and the updates of a sequence all use it.
    /// </remarks>
    internal readonly struct MotionEvaluation
    {
        public readonly bool IsGuarded;
        public readonly int StorageId;
        public readonly int SlotIndex;
        public readonly int SlotVersion;
        public readonly int StorageGeneration;
        public readonly uint Version;

        public MotionEvaluation(int storageId, int slotIndex, int slotVersion, int storageGeneration, uint version)
        {
            IsGuarded = true;
            StorageId = storageId;
            SlotIndex = slotIndex;
            SlotVersion = slotVersion;
            StorageGeneration = storageGeneration;
            Version = version;
        }

        static uint lastVersion;

        /// <summary>
        /// A new value for <c>MotionData.MotionState.EvaluationVersion</c> (never 0).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint NextVersion()
        {
            if (++lastVersion == 0) lastVersion = 1;
            return lastVersion;
        }
    }
}
