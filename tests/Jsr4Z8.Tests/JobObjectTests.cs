using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Jsr4Z8.Core.Native;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// JobObject crash-net semantics, exercised ONLY through disposable sleeper
/// children. We never call <see cref="JobObject.TryEnableKillOnCloseForCurrentProcess"/>
/// here: that would enroll the testhost itself into a kill-on-close job and put
/// the whole suite at the mercy of a later handle close. Each test spawns its own
/// `cmd /c timeout /t 60` sleeper, assigns it to a test-created job via the
/// internal seams, and defensively kills the tree in finally.
/// </summary>
public class JobObjectTests
{
    [Fact]
    public void JobKillOnClose_AssignedChild_DiesWhenHandleCloses()
    {
        using Process sleeper = SpawnSleeper();
        SafeHandle? job = JobObject.TryCreateKillOnCloseJob();
        try
        {
            Assert.NotNull(job);
            Assert.True(JobObject.TryAssign(sleeper), "assigning the sleeper to the job should succeed");

            job!.Dispose(); // last handle: the job object is destroyed and kill-on-close fires

            Assert.True(sleeper.WaitForExit(5000),
                "assigned child must be terminated within 5s of the job handle closing");
        }
        finally
        {
            KillTree(sleeper);
        }
    }

    [Fact]
    public void TryCreateKillOnCloseJob_ReturnsUsableHandle()
    {
        using Process sleeper = SpawnSleeper();
        SafeHandle? job = JobObject.TryCreateKillOnCloseJob();
        try
        {
            Assert.NotNull(job);
            Assert.False(job!.IsInvalid, "CreateJobObjectW must return a valid handle");
            Assert.False(job.IsClosed, "fresh job handle must be open");
            Assert.True(JobObject.TryAssign(sleeper), "assigning the sleeper to the job should succeed");
            Assert.False(sleeper.WaitForExit(1500),
                "child must stay alive while the job handle stays open");
        }
        finally
        {
            job?.Dispose();
            KillTree(sleeper);
        }
    }

    /// <summary>Spawns a hidden 60s sleeper (`cmd /c timeout /t 60 >nul`).</summary>
    private static Process SpawnSleeper()
        => Process.Start(new ProcessStartInfo("cmd.exe", "/c timeout /t 60 >nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("cmd.exe sleeper failed to start");

    /// <summary>Best-effort tree kill so no sleeper survives a failing test.</summary>
    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Win32Exception) { }
        catch (InvalidOperationException) { }
    }
}
