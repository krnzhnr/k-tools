// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;

namespace KTools_App.ViewModels;

/// <summary>
/// Модель представления для динамической настройки параметров скрипта.
/// Разделяет «изменено в памяти» (StageSetting), «записано на диск» (CommitSetting)
/// и «не удалось сохранить»: сырые значения параметров в журнал не попадают,
/// а состояние фиксации публикуется в интерфейс через <see cref="CommitStatusText"/>.
/// </summary>
public sealed partial class ScriptSettingsViewModel : ThreadSafeViewModel
{
    private const string LogSource = "ScriptSettingsViewModel";
    private const string EventCommitted = "settings.script_parameter.committed";
    private const string EventCommitPending = "settings.script_parameter.committed_pending";
    private const string EventCommitFailed = "settings.script_parameter.commit_failed";
    private const string EventStagedDropped = "settings.script_parameter.staged_flush_failed";

    private readonly ISettingsManager _settingsManager;
    private readonly ILogService _logService;
    private readonly object _stagedLock = new();
    private readonly Dictionary<string, object> _staged = new(StringComparer.Ordinal);

    [ObservableProperty]
    private AbstractScript? _activeScript;

    [ObservableProperty]
    private string _settingsGroup = string.Empty;

    [ObservableProperty]
    private SettingCommitState _lastCommitState = SettingCommitState.None;

    [ObservableProperty]
    private string _lastCommitErrorCode = PersistenceErrorCodes.None;

    [ObservableProperty]
    private string _lastCommitSummary = string.Empty;

    [ObservableProperty]
    private string _commitStatusText = "Изменения параметров применяются без записи до потери фокуса поля.";

    public ScriptSettingsViewModel(ISettingsManager settingsManager, ILogService logService)
    {
        _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
    }

    /// <summary>
    /// Количество несохранённых правок, ожидающих фиксации.
    /// </summary>
    public int StagedCount
    {
        get
        {
            lock (_stagedLock)
            {
                return _staged.Count;
            }
        }
    }

    /// <summary>
    /// Инициализирует настройки для конкретного скрипта.
    /// При переключении на другой скрипт накопленные правки фиксируются,
    /// чтобы введённые значения не терялись молча.
    /// </summary>
    public void InitializeScript(AbstractScript script)
    {
        ArgumentNullException.ThrowIfNull(script);

        if (!ReferenceEquals(ActiveScript, script))
        {
            FlushStaged();
        }

        ActiveScript = script;
        SettingsGroup = _settingsManager.GetSafeGroupName(script.Name);
    }

    /// <summary>
    /// Возвращает сохраненное значение настройки.
    /// Несохранённая правка видна сразу, до фактической записи.
    /// </summary>
    public T GetSetting<T>(string key, T defaultValue)
    {
        if (string.IsNullOrEmpty(SettingsGroup))
        {
            return defaultValue;
        }

        if (TryGetStaged(key, out object? staged) && staged is T typed)
        {
            return typed;
        }

        return _settingsManager.GetSetting(SettingsGroup, key, defaultValue);
    }

    /// <summary>
    /// Зафиксировать значение параметра в настройках и сообщить фактический результат записи.
    /// </summary>
    /// <param name="key">Ключ параметра.</param>
    /// <param name="value">Новое значение параметра.</param>
    /// <returns>Типизированный результат persistence-слоя настроек.</returns>
    public PersistenceResult SaveSetting<T>(string key, T value)
    {
        return CommitSetting(key, value);
    }

    /// <summary>
    /// Зафиксировать значение параметра: записать в кэш настроек, получить подтверждённый
    /// результат и опубликовать его в интерфейсе. Сырое значение в журнал не пишется.
    /// </summary>
    /// <param name="key">Ключ параметра.</param>
    /// <param name="value">Новое значение параметра.</param>
    /// <returns>Типизированный результат persistence-слоя настроек.</returns>
    public PersistenceResult CommitSetting<T>(string key, T value)
    {
        if (string.IsNullOrEmpty(SettingsGroup) || string.IsNullOrEmpty(key))
        {
            PersistenceResult notApplied = PersistenceResult.Failed(
                PersistenceErrorCodes.NotApplied,
                PersistenceSummaries.NotApplied,
                null);
            PublishCommit(notApplied, key, "not-applied", value);
            return notApplied;
        }

        RemoveStaged(key);

        PersistenceResult result;
        try
        {
            result = PersistenceResult.Normalize(ApplyValue(key, value));
        }
        catch (Exception ex)
        {
            result = PersistenceResult.Failed(
                PersistenceErrorCodes.WriteFailed,
                PersistenceSummaries.WriteFailure,
                ex);
        }

        PublishCommit(result, key, "commit", value);
        return result;
    }

    /// <summary>
    /// Передать значение в типизированный перегруженный метод менеджера настроек,
    /// чтобы контракт persistence-слоя оставался явным, а не приводился к object без нужды.
    /// </summary>
    private PersistenceResult? ApplyValue<T>(string key, T value)
    {
        return value is string text
            ? _settingsManager.SetSetting(SettingsGroup, key, text)
            : _settingsManager.SetSetting(SettingsGroup, key, (object?)value);
    }

    /// <summary>
    /// Зафиксировать накопленные правки одним проходом. Возвращает число зафиксированных ключей.
    /// </summary>
    public int FlushStaged()
    {
        if (string.IsNullOrEmpty(SettingsGroup))
        {
            ClearStaged();
            return 0;
        }

        List<KeyValuePair<string, object>> pending;
        lock (_stagedLock)
        {
            if (_staged.Count == 0)
            {
                return 0;
            }

            pending = new List<KeyValuePair<string, object>>(_staged);
            _staged.Clear();
        }

        OnPropertyChanged(nameof(StagedCount));

        int committed = 0;
        foreach (KeyValuePair<string, object> pair in pending)
        {
            PersistenceResult result;
            try
            {
                result = PersistenceResult.Normalize(
                    _settingsManager.SetSetting(SettingsGroup, pair.Key, pair.Value));
            }
            catch (Exception ex)
            {
                result = PersistenceResult.Failed(
                    PersistenceErrorCodes.WriteFailed,
                    PersistenceSummaries.WriteFailure,
                    ex);
            }

            committed++;
            PublishCommit(result, pair.Key, "flush", pair.Value);
        }

        return committed;
    }

    /// <summary>
    /// Зафиксировать правку только в памяти, без записи и без журналирования.
    /// Используется на каждое нажатие клавиши: реальная фиксация выполняется
    /// один раз при потере фокуса или явном действии пользователя.
    /// </summary>
    /// <param name="key">Ключ параметра.</param>
    /// <param name="value">Новое значение параметра.</param>
    public void StageSetting<T>(string key, T value)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        bool added;
        lock (_stagedLock)
        {
            added = !_staged.ContainsKey(key);
            _staged[key] = value!;
        }

        if (added)
        {
            OnPropertyChanged(nameof(StagedCount));
        }
    }

    private void RemoveStaged(string key)
    {
        bool removed;
        lock (_stagedLock)
        {
            removed = _staged.Remove(key);
        }

        if (removed)
        {
            OnPropertyChanged(nameof(StagedCount));
        }
    }

    private void ClearStaged()
    {
        bool had;
        lock (_stagedLock)
        {
            had = _staged.Count > 0;
            _staged.Clear();
        }

        if (had)
        {
            OnPropertyChanged(nameof(StagedCount));
        }
    }

    private bool TryGetStaged(string key, out object? value)
    {
        lock (_stagedLock)
        {
            return _staged.TryGetValue(key, out value);
        }
    }

    private void PublishCommit(PersistenceResult result, string key, string stage, object? explicitValue = null)
    {
        object? effectiveValue = explicitValue;
        if (effectiveValue is null && TryGetStaged(key, out object? stagedValue))
        {
            effectiveValue = stagedValue;
        }

        string displayValue = effectiveValue is not null
            ? effectiveValue.ToString() ?? string.Empty
            : string.Empty;

        Dictionary<string, object?> properties = new(StringComparer.Ordinal)
        {
            ["Key"] = key,
            ["Stage"] = stage,
            ["ErrorCode"] = result.ErrorCode,
            ["Persisted"] = result.Persisted,
            ["Value"] = displayValue,
            ["ValueHash"] = PersistenceValueHash.Compute(key)
        };

        if (result.IsSuccess)
        {
            if (result.Persisted)
            {
                LastCommitState = SettingCommitState.Persisted;
                LastCommitErrorCode = PersistenceErrorCodes.None;
                LastCommitSummary = string.Empty;
                CommitStatusText = string.Create(
                    CultureInfo.CurrentCulture,
                    $"Параметр '{key}' сохранён в настройках.");
                _logService.Write(
                    EventCommitted,
                    LogLevel.Info,
                    LogStatus.Changed,
                    string.IsNullOrEmpty(displayValue)
                        ? $"Параметр '{key}' сохранён в настройках"
                        : $"Параметр '{key}' сохранён в настройках: '{displayValue}'",
                    null,
                    LogSource,
                    properties: properties);
                return;
            }

            LastCommitState = SettingCommitState.None;
            LastCommitErrorCode = PersistenceErrorCodes.None;
            LastCommitSummary = string.Empty;
            CommitStatusText = string.Create(
                CultureInfo.CurrentCulture,
                $"Параметр '{key}' не изменился.");
            return;
        }

        if (result.IsPending)
        {
            LastCommitState = SettingCommitState.ChangedInMemory;
            LastCommitErrorCode = PersistenceErrorCodes.None;
            LastCommitSummary = string.IsNullOrEmpty(result.UserSummary)
                ? PersistenceSummaries.PendingSave
                : result.UserSummary;
            CommitStatusText = string.Create(
                CultureInfo.CurrentCulture,
                $"Параметр '{key}': {LastCommitSummary}");
            _logService.Write(
                EventCommitPending,
                LogLevel.Info,
                LogStatus.Changed,
                $"Параметр '{key}' применён в текущем сеансе, запись отложена",
                null,
                LogSource,
                properties: properties);
            return;
        }

        LastCommitState = SettingCommitState.Failed;
        LastCommitErrorCode = result.ErrorCode;
        LastCommitSummary = string.IsNullOrEmpty(result.UserSummary)
            ? PersistenceSummaries.WriteFailure
            : result.UserSummary;
        CommitStatusText = string.Create(
            CultureInfo.CurrentCulture,
            $"Параметр '{key}' не сохранён: {LastCommitErrorCode}. {LastCommitSummary}");
        _logService.Write(
            EventCommitFailed,
            LogLevel.Warning,
            LogStatus.Failed,
            $"Не удалось сохранить параметр '{key}': {LastCommitErrorCode}",
            null,
            LogSource,
            properties: properties);

        if (stage == "flush")
        {
            _logService.Write(
                EventStagedDropped,
                LogLevel.Warning,
                LogStatus.Failed,
                $"Отложенная правка параметра '{key}' потеряна из-за ошибки записи",
                null,
                LogSource,
                properties: properties);
        }
    }
}
