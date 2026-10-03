using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace KTools_App;

public static class Program
{
    public const string InstanceKey = "KToolsSingleInstanceKey";
    public const int SuccessExitCode = 0;
    public const int HandoffFailedExitCode = 1;
    public const string PendingArgsWriteStage = "pending_args_write";
    public const string ActivationDispatchStage = "activation_dispatch";
    public const string SingleInstanceStage = "single_instance";
    public const string PendingArgsWriteFailedCode = "pending-args-write-failed";
    public const string PendingArgsWriteExceptionCode = "pending-args-write-exception";
    public const string ActivationDispatchFailedCode = "activation-dispatch-failed";
    public const string SingleInstanceFailedCode = "single-instance-failed";

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            AppInstance instance = AppInstance.FindOrRegisterForKey(InstanceKey);
            if (instance.IsCurrent)
            {
                instance.Activated += OnInstanceActivated;
                Application.Start(p =>
                {
                    DispatcherQueueSynchronizationContext context = new(
                        DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(context);
                    _ = new App();
                });
                return SuccessExitCode;
            }

            return ForwardToPrimaryInstance(
                args,
                arguments => PendingArgsChannel.TryWrite(
                    PendingArgsChannel.ResolveDirectory(),
                    arguments,
                    out string? errorCode)
                    ? null
                    : errorCode ?? PendingArgsWriteFailedCode,
                () => RedirectActivation(instance));
        }
        catch (Exception ex)
        {
            ProgramDiagnostics.WriteFailure(SingleInstanceStage, ex, SingleInstanceFailedCode);
            return HandoffFailedExitCode;
        }
    }

    public static int ForwardToPrimaryInstance(
        IReadOnlyList<string> args,
        Func<IReadOnlyList<string>, string?> writeArgs,
        Action redirectActivation)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(writeArgs);
        ArgumentNullException.ThrowIfNull(redirectActivation);

        string? writeError;
        try
        {
            writeError = writeArgs(args);
        }
        catch (Exception ex)
        {
            ProgramDiagnostics.WriteFailure(PendingArgsWriteStage, ex, PendingArgsWriteExceptionCode);
            return HandoffFailedExitCode;
        }

        if (writeError is not null)
        {
            ProgramDiagnostics.WriteFailure(PendingArgsWriteStage, null, writeError);
            return HandoffFailedExitCode;
        }

        try
        {
            redirectActivation();
            return SuccessExitCode;
        }
        catch (Exception ex)
        {
            ProgramDiagnostics.WriteFailure(ActivationDispatchStage, ex, ActivationDispatchFailedCode);
            return HandoffFailedExitCode;
        }
    }

    private static void RedirectActivation(AppInstance instance)
    {
        AppActivationArguments activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        instance.RedirectActivationToAsync(activationArgs).AsTask().GetAwaiter().GetResult();
    }

    private static void OnInstanceActivated(object? sender, AppActivationArguments args)
    {
        try
        {
            App.HandleActivation(args);
        }
        catch (Exception ex)
        {
            ProgramDiagnostics.WriteFailure(ActivationDispatchStage, ex, ActivationDispatchFailedCode);
        }
    }
}

public static class PendingArgsChannel
{
    public const string FileExtension = ".txt";
    public const string FileSearchPattern = "*" + FileExtension;
    public const int MaxRetainedFiles = 256;
    public const int MaxLineLength = 2048;
    public const int MaxLines = 512;
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    public static string ResolveDirectory()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(appData))
        {
            appData = Path.GetTempPath();
        }

        return Path.Combine(appData, "KTools", "PendingArgs");
    }

    public static bool TryWrite(string directory, IReadOnlyList<string> args, out string? errorCode)
    {
        errorCode = null;
        if (string.IsNullOrWhiteSpace(directory))
        {
            errorCode = "pending-args-directory-invalid";
            return false;
        }

        string staging = string.Empty;
        try
        {
            Directory.CreateDirectory(directory);
            Prune(directory, MaxAge, MaxRetainedFiles);
            string target = Path.Combine(directory, Guid.NewGuid().ToString("N") + FileExtension);
            staging = target + ".partial";
            using (StreamWriter writer = new(staging, append: false))
            {
                int written = 0;
                foreach (string arg in args)
                {
                    if (written >= MaxLines)
                    {
                        break;
                    }

                    writer.WriteLine(Bound(arg));
                    written++;
                }
            }

            File.Move(staging, target, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            errorCode = "pending-args-write-failed";
            if (staging.Length > 0)
            {
                TryDelete(staging);
            }

            return false;
        }
    }

    public static IReadOnlyList<string> EnumeratePendingFiles(string directory, int maxFiles)
    {
        List<string> files = new();
        try
        {
            if (!Directory.Exists(directory))
            {
                return files;
            }

            FileInfo[] candidates = new DirectoryInfo(directory)
                .GetFiles(FileSearchPattern)
                .OrderBy(static file => file.LastWriteTimeUtc)
                .Take(Math.Max(1, maxFiles))
                .ToArray();
            foreach (FileInfo file in candidates)
            {
                if (IsExpired(file))
                {
                    TryDelete(file.FullName);
                    continue;
                }

                files.Add(file.FullName);
            }
        }
        catch (Exception)
        {
            return files;
        }

        return files;
    }

    public static int Prune(string directory, TimeSpan maxAge, int maxFiles)
    {
        int deleted = 0;
        try
        {
            if (!Directory.Exists(directory))
            {
                return 0;
            }

            FileInfo[] files = new DirectoryInfo(directory)
                .GetFiles(FileSearchPattern)
                .OrderBy(static file => file.LastWriteTimeUtc)
                .ToArray();
            int keep = Math.Max(1, maxFiles);
            for (int index = 0; index < files.Length; index++)
            {
                bool expired = maxAge > TimeSpan.Zero && DateTime.UtcNow - files[index].LastWriteTimeUtc > maxAge;
                bool overflow = index < files.Length - keep;
                if (!expired && !overflow)
                {
                    continue;
                }

                if (TryDelete(files[index].FullName))
                {
                    deleted++;
                }
            }
        }
        catch (Exception)
        {
            return deleted;
        }

        return deleted;
    }

    public static bool TryDelete(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsExpired(FileInfo file)
    {
        try
        {
            return MaxAge > TimeSpan.Zero && DateTime.UtcNow - file.LastWriteTimeUtc > MaxAge;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static string Bound(string? value)
    {
        string text = value ?? string.Empty;
        return text.Length <= MaxLineLength ? text : text[..MaxLineLength];
    }
}
