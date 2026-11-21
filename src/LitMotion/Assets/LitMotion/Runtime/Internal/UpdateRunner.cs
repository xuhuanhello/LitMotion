using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace LitMotion
{
    internal interface IUpdateRunner
    {
        IMotionStorage Storage { get; }
        public void Update(double time, double unscaledTime, double realtime);
        public void Reset();
    }

    internal sealed class UpdateRunner<TValue, TOptions, TAdapter> : IUpdateRunner
        where TValue : unmanaged
        where TOptions : unmanaged, IMotionOptions
        where TAdapter : unmanaged, IMotionAdapter<TValue, TOptions>
    {
        public UpdateRunner(MotionStorage<TValue, TOptions, TAdapter> storage, double time, double unscaledTime, double realtime)
        {
            this.storage = storage;
            prevTime = time;
            prevUnscaledTime = unscaledTime;
            prevRealtime = realtime;
        }

        readonly MotionStorage<TValue, TOptions, TAdapter> storage;

        double prevTime;
        double prevUnscaledTime;
        double prevRealtime;

        public MotionStorage<TValue, TOptions, TAdapter> Storage => storage;
        IMotionStorage IUpdateRunner.Storage => storage;

        public unsafe void Update(double time, double unscaledTime, double realtime)
        {
            var count = storage.Count;
            using var output = new NativeArray<TValue>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            using var completedIndexList = new NativeList<int>(count, Allocator.TempJob);

            var deltaTime = time - prevTime;
            var unscaledDeltaTime = unscaledTime - prevUnscaledTime;
            var realDeltaTime = realtime - prevRealtime;
            prevTime = time;
            prevUnscaledTime = unscaledTime;
            prevRealtime = realtime;

            var evaluationVersion = MotionEvaluation.NextVersion(); // [fork]

            fixed (MotionData<TValue, TOptions>* dataPtr = storage.GetDataSpan())
            {
                // update data
                var job = new MotionUpdateJob<TValue, TOptions, TAdapter>()
                {
                    DataPtr = dataPtr,
                    DeltaTime = deltaTime,
                    UnscaledDeltaTime = unscaledDeltaTime,
                    RealDeltaTime = realDeltaTime,
                    EvaluationVersion = evaluationVersion, // [fork]
                    Output = output,
                    CompletedIndexList = completedIndexList.AsParallelWriter()
                };
                job.Schedule(count, 16).Complete();

                // [fork] The batch contains exactly count outputs, including if a bind grows the storage.
                storage.DispatchRunnerResults(dataPtr, (TValue*)output.GetUnsafePtr(), count, evaluationVersion);
            }

            storage.RemoveAll(completedIndexList, evaluationVersion); // [fork] evaluationVersion: see MotionStorage.RemoveAll
        }

        public void Reset()
        {
            prevTime = 0;
            prevUnscaledTime = 0;
            prevRealtime = 0;
            storage.Reset();
        }
    }
}
