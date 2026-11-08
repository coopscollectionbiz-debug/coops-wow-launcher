namespace CoopsWoWLauncher;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (LauncherHandoff.IsHelper(args))
        {
            LauncherHandoff.RunHelper(args);
            return;
        }

        using Mutex instance = LauncherHandoff.InstanceMutex();
        try
        {
            if (!instance.WaitOne(0)) return;
        }
        catch (AbandonedMutexException) { }

        try
        {
            Action? checkpoint = LauncherHandoff.HealthCheckpoint(args);
            if (checkpoint is null && !LauncherHandoff.RecoverOrContinue())
                return;

            ApplicationConfiguration.Initialize();
            Application.Run(new Form1
            {
                StartupCheckpoint = checkpoint,
#if STAGING_FEEDS
                StagingSmoke = args is ["--staging-smoke", "update" or "graphics" or "graphics-install" or "graphics-remove" or "launch"]
                    ? args[1] : null,
#endif
            });
        }
        finally
        {
            instance.ReleaseMutex();
        }
    }
}
