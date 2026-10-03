// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using KTools_App.Diagnostics;

namespace KTools_App.Services.Contracts;

/// <summary>
/// Типизированный статус операции сохранения настроек.
/// </summary>
public enum PersistenceStatus
{
    Succeeded = 0,
    Pending = 1,
    Failed = 2
}

/// <summary>
/// Источник фактического состояния кэша настроек после операции.
/// </summary>
public enum PersistenceSource
{
    None = 0,
    Disk = 1,
    Defaults = 2,
    Fallback = 3
}

/// <summary>
/// Состояние последней фиксации параметра скрипта в настройках.
/// Разделяет «изменено в памяти», «сохранено на диск» и «не удалось сохранить».
/// </summary>
public enum SettingCommitState
{
    None = 0,
    ChangedInMemory = 1,
    Persisted = 2,
    Failed = 3
}

/// <summary>
/// Типизированные коды ошибок persistence-слоя настроек.
/// </summary>
public static class PersistenceErrorCodes
{
    public const string None = "NONE";
    public const string AccessDenied = "SETTINGS_ACCESS_DENIED";
    public const string FileLocked = "SETTINGS_FILE_LOCKED";
    public const string FileNotFound = "SETTINGS_FILE_NOT_FOUND";
    public const string InvalidArgument = "SETTINGS_INVALID_ARGUMENT";
    public const string InvalidPath = "SETTINGS_INVALID_PATH";
    public const string NoResult = "SETTINGS_NO_RESULT";
    public const string ParseFailed = "SETTINGS_PARSE_FAILED";
    public const string ReadFailed = "SETTINGS_READ_FAILED";
    public const string SerializeFailed = "SETTINGS_SERIALIZE_FAILED";
    public const string SnapshotTooLarge = "SETTINGS_SNAPSHOT_TOO_LARGE";
    public const string WriteFailed = "SETTINGS_WRITE_FAILED";
    public const string Cancelled = "SETTINGS_CANCELLED";
    public const string Disposed = "SETTINGS_DISPOSED";
    public const string NotApplied = "SETTINGS_NOT_APPLIED";
    public const string DegradedLoad = "SETTINGS_DEGRADED_LOAD";
}

/// <summary>
/// Безопасные пользовательские формулировки для диалогов.
/// Не содержат текста исключения, полных путей и значений параметров.
/// </summary>
public static class PersistenceSummaries
{
    public const string WriteFailure = "Не удалось сохранить настройки на диск. Проверьте права доступа к папке данных приложения и повторите попытку.";
    public const string ReadFailure = "Файл настроек не читается: применены значения по умолчанию, исходный файл сохранён в резервную копию и не перезаписан.";
    public const string PendingSave = "Изменения применены в текущем сеансе, запись на диск отложена.";
    public const string Cancelled = "Запись настроек отменена: значения применены только в текущем сеансе.";
    public const string Disposed = "Менеджер настроек освобождён: значения применены только в текущем сеансе.";
    public const string NotApplied = "Параметр не применён: не задана группа или ключ настройки.";
    public const string NoResult = "Не удалось подтвердить результат сохранения: значения применены только в текущем сеансе.";
    public const string DegradedLoad = "Файл настроек был повреждён и сохранён в резервную копию. Значения по умолчанию применены только в текущем сеансе: выполните явный сброс настроек, чтобы записать их на диск.";
    public const string DegradedLoadBlocked = "Запись настроек заблокирована: файл настроек не удалось прочитать и он не перезаписывается. Выполните явный сброс настроек, чтобы сохранить значения.";
    public const string CorruptBackupCreated = "Повреждённый файл настроек сохранён в резервную копию рядом с ним. Исходные данные не перезаписаны значениями по умолчанию.";
    public const string NextLaunchOnly = "Новый каталог журналирования сохранён и будет использован со следующего запуска приложения.";
    public const string NextLaunchLost = "Каталог журналирования не сохранён: изменение действует только в текущем сеансе и будет потеряно при закрытии приложения.";
}

/// <summary>
/// Стабильные идентификаторы событий журнала persistence-слоя настроек.
/// </summary>
public static class SettingsEventIds
{
    public const string Source = "SettingsManager";
    public const string Changed = "settings.changed";
    public const string ChangedPending = "settings.changed.pending";
    public const string LoadCompleted = "settings.load.completed";
    public const string LoadDegraded = "settings.load.degraded";
    public const string CorruptBackup = "settings.load.corrupt_backup";
    public const string PendingDiscarded = "settings.pending.discarded";
    public const string PersistenceFailed = "settings.persistence.failed";
    public const string DefaultsPersisted = "settings.defaults.persisted";
    public const string DefaultsDeferred = "settings.defaults.deferred";
    public const string Normalized = "settings.normalized";
    public const string DirectoryConfigured = "settings.directory.configured";
    public const string CommitFallback = "settings.persistence.fallback_failed";
}

/// <summary>
/// Безопасный стабильный хэш значения параметра настроек.
/// В журнал попадает только хэш: исходное значение никогда не логируется.
/// </summary>
public static class PersistenceValueHash
{
    public const string Prefix = "sha256:";
    public const string EmptyHash = Prefix + "empty";
    private const int HexLength = 16;
    private const string HexDigits = "0123456789abcdef";

    /// <summary>
    /// Вычислить стабильный хэш значения параметра.
    /// </summary>
    /// <param name="value">Значение параметра (может быть null).</param>
    /// <returns>Строка вида sha256:&lt;16 hex-символов&gt;.</returns>
    public static string Compute(object? value)
    {
        try
        {
            return Prefix + Digest(Materialize(value));
        }
        catch (Exception)
        {
            return Prefix + Digest("unavailable");
        }
    }

    /// <summary>
    /// Вычислить стабильный агрегированный хэш пакета изменений.
    /// </summary>
    /// <param name="changes">Пары «композитный ключ» — «хэш значения», отсортированные вызывающим кодом.</param>
    /// <returns>Строка вида sha256:&lt;16 hex-символов&gt;.</returns>
    public static string Combine(IEnumerable<KeyValuePair<string, string>> changes)
    {
        StringBuilder builder = new(128);
        foreach (KeyValuePair<string, string> change in changes)
        {
            builder.Append(change.Key).Append('=').Append(change.Value).Append('\n');
        }

        if (builder.Length == 0)
        {
            return EmptyHash;
        }

        return Prefix + Digest(builder.ToString());
    }

    private static string Materialize(object? value)
    {
        if (value is null)
        {
            return "n:";
        }

        if (value is string text)
        {
            return "s:" + text;
        }

        if (value is System.Collections.IEnumerable sequence)
        {
            try
            {
                return "j:" + System.Text.Json.JsonSerializer.Serialize(sequence, value.GetType());
            }
            catch (Exception)
            {
                return "o:" + TypeToken(value) + ":" + SafeToString(value);
            }
        }

        if (value is IFormattable formattable)
        {
            try
            {
                return "f:" + TypeToken(value) + ":" + formattable.ToString(null, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return "o:" + TypeToken(value) + ":" + SafeToString(value);
            }
        }

        return "o:" + TypeToken(value) + ":" + SafeToString(value);
    }

    private static string TypeToken(object value)
    {
        try
        {
            return value.GetType().FullName ?? value.GetType().Name;
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string SafeToString(object value)
    {
        try
        {
            return value.ToString() ?? string.Empty;
        }
        catch (Exception)
        {
            return "unavailable";
        }
    }

    private static string Digest(string material)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(material);
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(bytes, hash);

        Span<char> hex = stackalloc char[HexLength];
        for (int index = 0; index < HexLength / 2; index++)
        {
            hex[index * 2] = HexDigits[hash[index] >> 4];
            hex[(index * 2) + 1] = HexDigits[hash[index] & 0x0F];
        }

        return new string(hex);
    }
}

/// <summary>
/// Типизированный результат операции сохранения настроек.
/// Никогда не выбрасывает наружу сырое исключение: наружу отдаётся только
/// безопасный <see cref="ExceptionInfo"/> и код ошибки.
/// </summary>
public sealed class PersistenceResult
{
    private static readonly IReadOnlyList<string> NoChangedKeys = Array.Empty<string>();

    private readonly Exception? _exception;

    private PersistenceResult(
        PersistenceStatus status,
        string errorCode,
        string userSummary,
        Exception? exception,
        ExceptionInfo? exceptionInfo,
        IReadOnlyList<string> changedKeys,
        PersistenceSource source,
        bool persisted,
        bool degraded,
        bool cancelled)
    {
        IReadOnlyList<string> keys = changedKeys ?? NoChangedKeys;
        _exception = exception;
        Status = status;
        ErrorCode = errorCode;
        UserSummary = userSummary;
        ExceptionInfo = exceptionInfo;
        ChangedKeys = keys;
        ChangedCount = keys.Count;
        Source = source;
        Persisted = persisted;
        IsDegraded = degraded;
        IsCancelled = cancelled;
    }

    /// <summary>
    /// Сырое исключение доступно только внутри сборки: единственный владелец
    /// исключения — вызывающий журнал, наружу отдаётся только <see cref="ExceptionInfo"/>.
    /// </summary>
    internal Exception? RawException => _exception;

    /// <summary>
    /// Типизированный статус операции.
    /// </summary>
    public PersistenceStatus Status { get; }

    /// <summary>
    /// Типизированный код ошибки (PersistenceErrorCodes.None при отсутствии ошибки).
    /// </summary>
    public string ErrorCode { get; }

    /// <summary>
    /// Безопасное представление исключения-владельца (без сырого Exception в API).
    /// </summary>
    public ExceptionInfo? ExceptionInfo { get; }

    /// <summary>
    /// Безопасная пользовательская формулировка без путей и текста исключения.
    /// </summary>
    public string UserSummary { get; }

    /// <summary>
    /// Список изменённых ключей в формате «группа/ключ».
    /// </summary>
    public IReadOnlyList<string> ChangedKeys { get; }

    /// <summary>
    /// Количество изменённых ключей.
    /// </summary>
    public int ChangedCount { get; }

    /// <summary>
    /// Источник состояния кэша настроек.
    /// </summary>
    public PersistenceSource Source { get; }

    /// <summary>
    /// Была ли подтверждена фактическая запись на диск.
    /// </summary>
    public bool Persisted { get; }

    /// <summary>
    /// Работа выполнена в деградированном режиме (например, чтение заменено кэшем по умолчанию).
    /// </summary>
    public bool IsDegraded { get; }

    /// <summary>
    /// Операция завершилась без ошибки.
    /// </summary>
    public bool IsSuccess => Status == PersistenceStatus.Succeeded;

    /// <summary>
    /// Операция отложена и ещё не записана на диск.
    /// </summary>
    public bool IsPending => Status == PersistenceStatus.Pending;

    /// <summary>
    /// Операция завершилась ошибкой.
    /// </summary>
    public bool IsFailure => Status == PersistenceStatus.Failed;

    /// <summary>
    /// Операция была отменена до фактической записи.
    /// </summary>
    public bool IsCancelled { get; }

    /// <summary>
    /// Есть ли подтверждённые изменения.
    /// </summary>
    public bool HasChanges => ChangedCount > 0;

    /// <summary>
    /// Успешный результат без ошибок.
    /// </summary>
    /// <param name="changedKeys">Изменённые ключи.</param>
    /// <param name="source">Источник состояния кэша.</param>
    /// <param name="persisted">Подтверждена ли запись на диск.</param>
    /// <returns>Готовый результат операции.</returns>
    public static PersistenceResult Succeeded(
        IReadOnlyList<string>? changedKeys = null,
        PersistenceSource source = PersistenceSource.Disk,
        bool persisted = true)
    {
        return new PersistenceResult(
            PersistenceStatus.Succeeded,
            PersistenceErrorCodes.None,
            string.Empty,
            null,
            null,
            changedKeys ?? NoChangedKeys,
            source,
            persisted,
            false,
            false);
    }

    /// <summary>
    /// Значение параметра не изменилось: операция не требовалась.
    /// </summary>
    /// <param name="group">Группа настройки.</param>
    /// <param name="key">Ключ настройки.</param>
    /// <returns>Результат без изменений.</returns>
    public static PersistenceResult Unchanged(string group, string key)
    {
        return new PersistenceResult(
            PersistenceStatus.Succeeded,
            PersistenceErrorCodes.None,
            string.Empty,
            null,
            null,
            NoChangedKeys,
            PersistenceSource.None,
            false,
            false,
            false);
    }

    /// <summary>
    /// Изменение применено в памяти, запись отложена.
    /// </summary>
    /// <param name="changedKeys">Изменённые ключи.</param>
    /// <returns>Отложенный результат операции.</returns>
    public static PersistenceResult Pending(IReadOnlyList<string>? changedKeys = null)
    {
        return new PersistenceResult(
            PersistenceStatus.Pending,
            PersistenceErrorCodes.None,
            PersistenceSummaries.PendingSave,
            null,
            null,
            changedKeys ?? NoChangedKeys,
            PersistenceSource.None,
            false,
            false,
            false);
    }

    /// <summary>
    /// Ошибка persistence без деградации.
    /// </summary>
    /// <param name="errorCode">Типизированный код ошибки.</param>
    /// <param name="userSummary">Безопасная пользовательская формулировка.</param>
    /// <param name="exception">Единственный владелец исключения.</param>
    /// <param name="changedKeys">Изменённые ключи.</param>
    /// <param name="degraded">Работа в деградированном режиме.</param>
    /// <param name="source">Источник состояния кэша.</param>
    /// <returns>Результат с ошибкой.</returns>
    public static PersistenceResult Failed(
        string errorCode,
        string userSummary,
        Exception? exception,
        IReadOnlyList<string>? changedKeys = null,
        bool degraded = false,
        PersistenceSource source = PersistenceSource.None)
    {
        ExceptionInfo? info = null;
        if (exception != null)
        {
            try
            {
                info = ExceptionInfo.FromException(exception);
            }
            catch (Exception)
            {
                info = null;
            }
        }

        return new PersistenceResult(
            PersistenceStatus.Failed,
            string.IsNullOrEmpty(errorCode) ? PersistenceErrorCodes.WriteFailed : errorCode,
            userSummary,
            exception,
            info,
            changedKeys ?? NoChangedKeys,
            source,
            false,
            degraded,
            string.Equals(errorCode, PersistenceErrorCodes.Cancelled, StringComparison.Ordinal));
    }

    /// <summary>
    /// Операция отменена до фактической записи на диск.
    /// </summary>
    /// <param name="userSummary">Безопасная пользовательская формулировка.</param>
    /// <param name="errorCode">Типизированный код ошибки.</param>
    /// <param name="changedKeys">Изменённые ключи.</param>
    /// <returns>Отменённый результат операции.</returns>
    public static PersistenceResult Cancelled(
        string userSummary,
        string errorCode = PersistenceErrorCodes.Cancelled,
        IReadOnlyList<string>? changedKeys = null)
    {
        return new PersistenceResult(
            PersistenceStatus.Failed,
            errorCode,
            userSummary,
            null,
            null,
            changedKeys ?? NoChangedKeys,
            PersistenceSource.None,
            false,
            false,
            true);
    }

    /// <summary>
    /// Заменить пустой результат (например, от loose-мока) на явную ошибку,
    /// чтобы ложный успех никогда не доходил до UI или журнала.
    /// </summary>
    /// <param name="candidate">Полученный результат (может быть null).</param>
    /// <returns>Гарантированно непустой результат.</returns>
    public static PersistenceResult Normalize(PersistenceResult? candidate)
    {
        return candidate ?? Failed(
            PersistenceErrorCodes.NoResult,
            PersistenceSummaries.NoResult,
            null);
    }

    /// <summary>
    /// Создать копию результата с фактическим списком изменённых ключей.
    /// </summary>
    /// <param name="changedKeys">Изменённые ключи.</param>
    /// <returns>Новый результат с обновлённым списком ключей.</returns>
    public PersistenceResult WithChangedKeys(IReadOnlyList<string> changedKeys)
    {
        return new PersistenceResult(
            Status,
            ErrorCode,
            UserSummary,
            _exception,
            ExceptionInfo,
            changedKeys ?? NoChangedKeys,
            Source,
            Persisted,
            IsDegraded,
            IsCancelled);
    }
}
