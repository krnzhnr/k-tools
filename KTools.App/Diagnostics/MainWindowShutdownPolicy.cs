using KTools_App.Core;

namespace KTools_App.Diagnostics;

public static class MainWindowShutdownPolicy
{
    public static MainWindowShutdownResult Evaluate(
        SettingsPersistenceResult settings,
        ProcessTerminationSummary processes,
        bool flushSucceeded,
        bool watcherStopped)
    {
        bool settingsPersisted = settings.Persisted;
        bool processesVerified = processes.AllVerified;
        if (settingsPersisted && processesVerified && flushSucceeded && watcherStopped)
        {
            return new MainWindowShutdownResult(
                true,
                true,
                true,
                true,
                LogLevel.Info,
                LogStatus.Succeeded);
        }

        if (processes.HasPartialFailure && settingsPersisted && flushSucceeded && watcherStopped)
        {
            return new MainWindowShutdownResult(
                settingsPersisted,
                false,
                flushSucceeded,
                watcherStopped,
                LogLevel.Warning,
                LogStatus.PartiallySucceeded);
        }

        return new MainWindowShutdownResult(
            settingsPersisted,
            processesVerified,
            flushSucceeded,
            watcherStopped,
            LogLevel.Error,
            LogStatus.Failed);
    }
}
