// -*- coding: utf-8 -*-
using System;

using KTools_App.Diagnostics;

namespace KTools_App;

/// <summary>
/// Стабильные идентификаторы событий журнала уровня приложения.
/// Единственный источник EventId для private-forwarder <c>App.WriteAppEvent</c>:
/// вызывающая сторона передаёт именованную константу, а не литерал и не
/// вычисляемую строку, поэтому идентификатор greppable и не меняется от запуска к запуску.
/// Идентификаторы контролируемого завершения остаются в
/// <see cref="Diagnostics.CrashCoordinator"/>, чтобы не дублировать уже существующие константы.
/// </summary>
public static class AppEventIds
{
    public const string Started = "app.started";
    public const string StartupFailed = "app.startup_failed";
    public const string WindowActivationFailed = "app.window_activation_failed";
    public const string AdminCheckFailed = "app.admin_check_failed";
    public const string SystemInfoFailed = "app.system_info_failed";
    public const string SettingsResolved = "app.settings_resolved";
    public const string DefaultSettingsInitializationStarted = "app.default_settings_initialization_started";
    public const string ShellIntegrationUpdated = "app.shell_integration_updated";
    public const string ShellIntegrationFailed = "app.shell_integration_failed";
    public const string OwnArgumentsReceived = "app.own_arguments_received";
    public const string DiagnosticsLoggingArmed = "app.diagnostics.logging_armed";
    public const string DiagnosticsStatus = "app.diagnostics.status";
    public const string ControlledShutdownProcessFailure = "app.controlled_shutdown.process_failure";
    public const string ControlledShutdownSettingsPersistenceFailed = "app.controlled_shutdown.settings_persistence_failed";
    public const string ControlledShutdownWatcherFailure = "app.controlled_shutdown.watcher_failure";
    public const string ControlledShutdownFlushFailure = "app.controlled_shutdown.flush_failure";
    public const string PendingArgsDeferred = "app.pending_args.deferred";
    public const string PendingArgsPruned = "app.pending_args.pruned";
    public const string PendingArgsRetry = "app.pending_args.retry";
    public const string PendingArgsReadFailed = "app.pending_args.read_failed";
    public const string PendingArgsProcessed = "app.pending_args.processed";
    public const string PendingArgsDispatchFailed = "app.pending_args.dispatch_failed";
    public const string PendingArgsParseFailed = "app.pending_args.parse_failed";
    public const string PendingArgsDirectoryFailed = "app.pending_args.directory_failed";
    public const string PendingArgsWatcherFailed = "app.pending_args.watcher_failed";
}

/// <summary>
/// Стабильные машиночитаемые коды отказов уровня приложения.
/// Каждый отказ <c>LogLevel.Error</c>/<c>LogLevel.Fatal</c> обязан нести собственный код:
/// без него потребитель не может отличить «журнал не дошёл» от «событие не классифицировано».
/// Формат — kebab-case латиницей, код не переиспользуется на других местах.
/// </summary>
public static class AppErrorCodes
{
    public const string StartupFailed = "app-startup-failed";
    public const string AdminCheckFailed = "app-admin-check-failed";
    public const string SystemInfoFailed = "app-system-info-failed";
    public const string ShellIntegrationFailed = "app-shell-integration-failed";
    public const string WindowActivationFailed = "app-window-activation-failed";
    public const string PendingArgsReadFailed = "app-pending-args-read-failed";
    public const string PendingArgsDispatchFailed = "app-pending-args-dispatch-failed";
    public const string PendingArgsParseFailed = "app-pending-args-parse-failed";
    public const string PendingArgsDirectoryFailed = "app-pending-args-directory-failed";
    public const string PendingArgsWatcherFailed = "app-pending-args-watcher-failed";
    public const string ShutdownProcessFailure = "app-shutdown-process-failure";
    public const string ShutdownProcessTerminationException = "app-shutdown-process-termination-exception";
    public const string ShutdownFlushIncomplete = "app-shutdown-log-flush-incomplete";
    public const string ShutdownFlushException = "app-shutdown-log-flush-exception";
    public const string ShutdownStageFailed = "app-shutdown-stage-failed";
}

/// <summary>
/// Типизированный идентификатор события журнала уровня приложения.
/// Обёртка над <see cref="string"/> проверяет значение по правилам журнала
/// (<see cref="LogRedactor.IsValidEventId"/>) в момент создания, поэтому
/// forwarder не может передать в конвейер невалидный или пустой EventId.
/// </summary>
public readonly struct AppEventId : IEquatable<AppEventId>
{
    private AppEventId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public bool Equals(AppEventId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is AppEventId other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;

    public static AppEventId From(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!LogRedactor.IsValidEventId(value))
        {
            throw new ArgumentException("Идентификатор события журнала не соответствует допустимому формату.", nameof(value));
        }

        return new AppEventId(value);
    }

    public static implicit operator AppEventId(string value) => From(value);

    public static bool operator ==(AppEventId left, AppEventId right) => left.Equals(right);

    public static bool operator !=(AppEventId left, AppEventId right) => !left.Equals(right);
}
