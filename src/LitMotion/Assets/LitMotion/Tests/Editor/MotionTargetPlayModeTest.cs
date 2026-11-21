using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

namespace LitMotion.Tests.Editor
{
    // Play mode transitions with domain reload disabled (Enter Play Mode Options): the static state of
    // SetTarget/CancelAll must not keep motions of a previous session, and must keep the motions that survive
    // the transition (ManualMotionDispatcher).
    public class MotionTargetPlayModeTest
    {
        static int GetEntryCount(object target)
        {
            var type = typeof(LMotion).Assembly.GetType("LitMotion.MotionTargetRegistry", true);
            var method = type.GetMethod("GetEntryCount", BindingFlags.NonPublic | BindingFlags.Static);
            return (int)method.Invoke(null, new[] { target });
        }

        [UnityTest]
        public IEnumerator Test_RegistryAcrossPlayModeChanges_WithoutDomainReload()
        {
            var previousEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            var previousOptions = EditorSettings.enterPlayModeOptions;
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;

            var manualDispatcher = ManualMotionDispatcher.Default;
            var target = new object();
            MotionHandle playerLoopMotion = default;
            MotionHandle manualMotion = default;

            try
            {
                yield return new EnterPlayMode();
                playerLoopMotion = LMotion.Create(0f, 1f, 100f).RunWithoutBinding().SetTarget(target);
                manualMotion = LMotion.Create(0f, 1f, 100f).WithScheduler(manualDispatcher.Scheduler).RunWithoutBinding().SetTarget(target);
                Assert.That(GetEntryCount(target), Is.EqualTo(2));

                yield return new ExitPlayMode();
                yield return null;

                // Both motions still exist (the player-loop one is frozen until the next session).
                Assert.That(manualMotion.IsActive(), Is.True);
                Assert.That(GetEntryCount(target), Is.EqualTo(2));

                yield return new EnterPlayMode();
                yield return null;

                // The player-loop motion of the previous session has been discarded, and so has its registration.
                Assert.That(playerLoopMotion.IsValid(), Is.False);
                Assert.That(GetEntryCount(target), Is.EqualTo(1));

                // The ManualMotionDispatcher motion survived and can still be found by its target.
                Assert.That(LMotion.CancelAll(target), Is.EqualTo(1));
                Assert.That(manualMotion.IsActive(), Is.False);

                yield return new ExitPlayMode();
            }
            finally
            {
                manualMotion.TryCancel();
                manualDispatcher.Reset();
                EditorSettings.enterPlayModeOptionsEnabled = previousEnabled;
                EditorSettings.enterPlayModeOptions = previousOptions;
            }
        }
    }
}
