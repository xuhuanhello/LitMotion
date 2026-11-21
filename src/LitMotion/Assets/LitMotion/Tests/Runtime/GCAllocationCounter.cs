using System;
using UnityEngine.Profiling;

namespace LitMotion.Tests.Runtime
{
    // Counts the "GC.Alloc" profiler samples of the current thread, like Is.Not.AllocatingGCMemory() does.
    //
    // In the Editor, an update of a motion runner (UpdateRunner.Update) already records one GC.Alloc sample per
    // storage in unmodified LitMotion v2.0.2 (it comes from `new NativeList<int>(..., Allocator.TempJob)` with the
    // collection safety checks of the Editor). Tests that go through runner updates therefore compare the count
    // against the same updates without the feature under test, instead of expecting zero.
    internal static class GCAllocationCounter
    {
        public static int Count(Action action)
        {
            var recorder = Recorder.Get("GC.Alloc");
            recorder.enabled = false;
            recorder.FilterToCurrentThread();
            recorder.enabled = true;
            try
            {
                action();
            }
            finally
            {
                recorder.enabled = false;
                recorder.CollectFromAllThreads();
            }
            return recorder.sampleBlockCount;
        }
    }
}
