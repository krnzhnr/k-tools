// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;

namespace KTools_App.Services.Implementations;

/// <summary>
/// Одноразовая миграция пользовательских данных из каталога установки
/// в управляемый каталог %LocalAppData%\KTools (ишью 46).
///
/// Правила безопасности (почему не бывает «пропал bin» и «вкладка врёт»):
/// - фаза A только ДОПИСЫВАЕТ: settings.json копируется (никогда не перемещается
///   и не затирает существующий файл назначения), bin копируется в фоне.
///   Фаза A не удаляет ни одного файла из каталога установки — буквально;
/// - settings.json проверяется побайтово сразу после копии;
/// - каждый файл bin проверяется хэшем после копии; расхождения фиксируются
///   как конфликты и никогда не затираются молча;
/// - оригиналы удаляет ТОЛЬКО фаза B и ТОЛЬКО после живого опроса версий
///   по новым путям при полном успехе; каждое удаление защищено повторной
///   хэш-проверкой, конфликтующие файлы остаются на месте;
/// - любой сбой фазы A включает legacy-режим: приложение стартует «по-старому»,
///   повторная попытка — при следующем запуске. Кирпичей нет по построению.
/// </summary>
public sealed class UserDataMigrator : IUserDataMigrator
{
    private const string SourceName = nameof(UserDataMigrator);
    private const string ReceiptFileName = ".migration.json";
    private const string SettingsFileName = "settings.json";
    private const string BinFolderName = "bin";
    private const string MutexName = "KTools_UserDataMigration";

    private readonly ILogService _logService;
    private readonly IPathManager _pathManager;
    private readonly object _copyLock = new();
    private Task<BinCopyResult>? _backgroundCopy;

    /// <summary>
    /// Инициализирует мигратор. Зависимости — только журнал и пути:
    /// фаза A выполняется до создания SettingsManager и DependencyManager.
    /// </summary>
    public UserDataMigrator(ILogService logService, IPathManager pathManager)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _pathManager = pathManager ?? throw new ArgumentNullException(nameof(pathManager));
    }

    /// <inheritdoc/>
    public void EnsureMigrated()
    {
        try
        {
            string legacyRoot = _pathManager.GetBaseDirectory();

            // Чистка следов старых проб — всегда, даже в portable-режиме:
            // маркеры лежат в общем каталоге данных и не принадлежат установке.
            SweepStaleProbeMarkers(legacyRoot);

            if (_pathManager.IsPortableMode())
            {
                WriteEvent(
                    AppEventIds.MigrationSkipped,
                    LogLevel.Info,
                    LogStatus.Skipped,
                    "Миграция данных пропущена: portable-режим, данные остаются рядом с исполняемым файлом",
                    null,
                    LogProps.Create("Mode", "portable"));
                return;
            }

            string dataRoot = _pathManager.GetSettingsDirectory();
            string dataBin = _pathManager.GetBinDirectory();

            if (PathsEqual(legacyRoot, dataRoot))
            {
                return;
            }

            MigrationReceipt receipt = MigrationReceipt.Load(ReceiptPath(dataRoot), legacyRoot);
            if (receipt.IsVerifiedCleanFor(legacyRoot))
            {
                return;
            }

            using (System.Threading.Mutex? migrationMutex = TryAcquireMutex())
            {
                if (migrationMutex is null)
                {
                    WriteEvent(
                        AppEventIds.MigrationDeferred,
                        LogLevel.Info,
                        LogStatus.Skipped,
                        "Миграция данных отложена: перенос уже выполняется другим процессом",
                        null,
                        LogProps.Create("Retryable", true));
                    return;
                }

                WriteEvent(
                    AppEventIds.MigrationStarted,
                    LogLevel.Info,
                    LogStatus.Running,
                    "Начат перенос пользовательских данных из каталога установки в LocalAppData",
                    null,
                    LogProps.Create("Version", GetCurrentAppVersion()));

                string settingsState = MigrateSettingsFile(legacyRoot, dataRoot);
                // Не даём повторному запуску «понизить» состояние: если источник уже
                // исчез после удачной релокации, запись о ней важнее свежего отсутствия.
                if (settingsState == "absent-source" && receipt.Settings == "copied")
                {
                    settingsState = receipt.Settings;
                }

                receipt.Settings = settingsState;
                if (settingsState.StartsWith("failed", StringComparison.Ordinal))
                {
                    receipt.Bin = "not-attempted";
                    receipt.Save(ReceiptPath(dataRoot));
                    EnterFallback("настройки не скопированы");
                    return;
                }

                string binState = RelocateBin(legacyRoot, dataBin);
                if (binState == "absent-source" && BinNeedsVerification(receipt.Bin))
                {
                    binState = receipt.Bin;
                }

                receipt.Bin = binState;
                receipt.MigratedAtUtc = DateTime.UtcNow;
                receipt.AppVersion = GetCurrentAppVersion();
                receipt.CleanupPending = binState.EndsWith("pending", StringComparison.Ordinal)
                    || settingsState.EndsWith("pending", StringComparison.Ordinal);

                if (binState.StartsWith("failed", StringComparison.Ordinal))
                {
                    // Откат нашей свежей копии настроек: в fallback-режиме приложение
                    // пишет в legacy-файл, а протухшая копия в назначении затёрла бы
                    // эти правки при исцелении bin. Копия из чужого прогона
                    // (skipped-dest-exists) не наша — её не трогаем.
                    if (settingsState == "copied")
                    {
                        TryDeleteFile(Path.Combine(dataRoot, SettingsFileName));
                        receipt.Settings = "rolled-back";
                    }

                    receipt.Save(ReceiptPath(dataRoot));
                    EnterFallback("bin не перенесён");
                    return;
                }

                receipt.Save(ReceiptPath(dataRoot));
            }
        }
        catch (Exception ex)
        {
            EnterFallback("непредвиденный сбой");
            WriteEvent(
                AppEventIds.MigrationFailed,
                LogLevel.Error,
                LogStatus.Failed,
                "Сбой переноса пользовательских данных, включён режим работы из каталога установки",
                ex,
                LogProps.Create("ErrorCode", AppErrorCodes.MigrationFailed));
        }
    }

    /// <inheritdoc/>
    public bool NeedsVerification()
    {
        try
        {
            if (_pathManager.IsPortableMode())
            {
                return false;
            }

            string legacyRoot = _pathManager.GetBaseDirectory();
            string dataRoot = _pathManager.GetSettingsDirectory();
            if (PathsEqual(legacyRoot, dataRoot))
            {
                return false;
            }

            if (!File.Exists(ReceiptPath(dataRoot)))
            {
                return false;
            }

            MigrationReceipt receipt = MigrationReceipt.Load(ReceiptPath(dataRoot), legacyRoot);
            if (!receipt.MatchesSource(legacyRoot)
                || (receipt.Verified && !receipt.CleanupPending)
                || (!BinNeedsVerification(receipt.Bin) && receipt.Settings != "copied"))
            {
                return false;
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task VerifyAndCleanupAsync(IReadOnlyList<BinaryProbeResult> probes, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_pathManager.IsPortableMode())
            {
                return;
            }

            string legacyRoot = _pathManager.GetBaseDirectory();
            string dataRoot = _pathManager.GetSettingsDirectory();
            string dataBin = _pathManager.GetBinDirectory();

            if (PathsEqual(legacyRoot, dataRoot))
            {
                return;
            }

            MigrationReceipt receipt = MigrationReceipt.Load(ReceiptPath(dataRoot), legacyRoot);
            if (!receipt.MatchesSource(legacyRoot) || (receipt.Verified && !receipt.CleanupPending))
            {
                return;
            }

            Task<BinCopyResult>? copy;
            lock (_copyLock)
            {
                copy = _backgroundCopy;
                _backgroundCopy = null;
            }

            if (copy != null)
            {
                // Отмена ожидания безопасна: задание только дописывает назначение
                // и никого не удаляет, недописанное доберёт следующий запуск.
                BinCopyResult copyResult = await copy.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (!copyResult.Completed)
                {
                    receipt.Bin = "failed:" + copyResult.ErrorCode;
                    receipt.CleanupPending = true;
                    receipt.Save(ReceiptPath(dataRoot));
                    WriteEvent(
                        AppEventIds.MigrationFailed,
                        LogLevel.Warning,
                        LogStatus.Failed,
                        "Фоновое копирование bin не завершено, оригиналы сохранены, повтор при следующем запуске",
                        null,
                        LogProps
                            .Create("ErrorCode", copyResult.ErrorCode)
                            .With("Retryable", true));
                    return;
                }

                receipt.Bin = copyResult.Conflicts > 0 ? "copied-pending" : "copied-clean";
                receipt.Save(ReceiptPath(dataRoot));
            }

            if (BinNeedsVerification(receipt.Bin))
            {
                // Пустой отчёт проб — не «все хорошо», а отсутствие проверки:
                // чистить оригиналы без единой живой версии запрещено.
                if (probes is null || probes.Count == 0)
                {
                    receipt.Verified = false;
                    receipt.CleanupPending = true;
                    receipt.Note = "probe-empty";
                    receipt.Save(ReceiptPath(dataRoot));
                    WriteEvent(
                        AppEventIds.MigrationProbeFailed,
                        LogLevel.Warning,
                        LogStatus.Failed,
                        "Нет данных опроса версий по новым путям, оригиналы сохранены, повтор при следующем запуске",
                        null,
                        LogProps
                            .Create("ErrorCode", AppErrorCodes.MigrationProbeFailed)
                            .With("Retryable", true));
                    return;
                }

                List<string> failedKeys = EvaluateProbes(probes, Path.Combine(legacyRoot, BinFolderName));
                if (failedKeys.Count > 0)
                {
                    receipt.Verified = false;
                    receipt.CleanupPending = true;
                    receipt.Note = "probe-failed:" + string.Join(",", failedKeys);
                    receipt.Save(ReceiptPath(dataRoot));
                    WriteEvent(
                        AppEventIds.MigrationProbeFailed,
                        LogLevel.Warning,
                        LogStatus.Failed,
                        "Проверка версий по новым путям не пройдена для: " + string.Join(", ", failedKeys) + ". Оригиналы сохранены, повтор при следующем запуске",
                        null,
                        LogProps
                            .Create("ErrorCode", AppErrorCodes.MigrationProbeFailed)
                            .With("Count", failedKeys.Count)
                            .With("Retryable", true));
                    return;
                }
            }

            CleanupLegacyFiles(legacyRoot, dataRoot, dataBin, cancellationToken, receipt);
            receipt.Verified = true;
            receipt.Save(ReceiptPath(dataRoot));

            WriteEvent(
                AppEventIds.MigrationVerified,
                LogLevel.Info,
                LogStatus.Succeeded,
                "Перенос данных проверен по новым путям, оригиналы из каталога установки удалены",
                null,
                LogProps
                    .Create("Verified", true)
                    .With("Version", GetCurrentAppVersion()));
        }
        catch (OperationCanceledException)
        {
            // Отмена проверки — штатно: повтор при следующем запуске, оригиналы целы.
        }
        catch (Exception ex)
        {
            WriteEvent(
                AppEventIds.MigrationFailed,
                LogLevel.Error,
                LogStatus.Failed,
                "Сбой проверки переноса данных, оригиналы из каталога установки сохранены",
                ex,
                LogProps.Create("ErrorCode", AppErrorCodes.MigrationFailed));
        }
    }

    /// <summary>
    /// Захватывает межпроцессный мьютекс миграции. Сначала пробуем глобальное
    /// пространство имён (видно из всех сессий), при нехватке прав (обычный
    /// пользователь без SeCreateGlobalPrivilege) откатываемся на локальное.
    /// Даже при параллельном переносе из двух сессий повреждения исключены:
    /// фаза A только дописывает назначение, удаление — только в фазе B
    /// после хэш-проверок, обе операции идемпотентны и сходятся к одному состоянию.
    /// </summary>
    private System.Threading.Mutex? TryAcquireMutex()
    {
        foreach (string name in new[] { @"Global\" + MutexName, @"Local\" + MutexName })
        {
            System.Threading.Mutex? mutex = null;
            try
            {
                mutex = new System.Threading.Mutex(false, name);
                bool acquired;
                try
                {
                    acquired = mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }

                if (acquired)
                {
                    return mutex;
                }

                mutex.Dispose();
                return null;
            }
            catch (Exception)
            {
                try
                {
                    mutex?.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }

        return null;
    }

    private void EnterFallback(string reason)
    {
        try
        {
            _pathManager.EnterLegacyFallbackMode();
        }
        catch (Exception)
        {
            // Режим отката необязателен для старта: пути уже указывают на управляемый каталог.
        }

        WriteEvent(
            AppEventIds.MigrationFailed,
            LogLevel.Warning,
            LogStatus.Failed,
            "Перенос данных не выполнен (" + reason + "), приложение работает из каталога установки",
            null,
            LogProps
                .Create("ErrorCode", AppErrorCodes.MigrationFailed)
                .With("Retryable", true));
    }

    private string MigrateSettingsFile(string legacyRoot, string dataRoot)
    {
        string source = Path.Combine(legacyRoot, SettingsFileName);
        string dest = Path.Combine(dataRoot, SettingsFileName);

        try
        {
            if (!File.Exists(source))
            {
                return "absent-source";
            }

            if (File.Exists(dest))
            {
                return "skipped-dest-exists";
            }

            File.Copy(source, dest, overwrite: false);
            try
            {
                File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(source));
            }
            catch (Exception)
            {
                // Метка времени необязательна: влияет только на свежесть кэшей.
            }

            string? sourceHash = ComputeSha256(source);
            string? destHash = ComputeSha256(dest);
            if (string.IsNullOrEmpty(sourceHash) || !string.Equals(sourceHash, destHash, StringComparison.Ordinal))
            {
                TryDeleteFile(dest);
                return "failed:hash-mismatch";
            }

            WriteEvent(
                AppEventIds.MigrationSettingsCopied,
                LogLevel.Info,
                LogStatus.Succeeded,
                "Настройки перенесены в LocalAppData и проверены побайтово",
                null,
                LogProps
                    .Create("FileName", SettingsFileName)
                    .With("Verified", true));
            return "copied";
        }
        catch (Exception ex)
        {
            WriteEvent(
                AppEventIds.MigrationFailed,
                LogLevel.Warning,
                LogStatus.Failed,
                "Не удалось скопировать настройки, повтор при следующем запуске",
                ex,
                LogProps
                    .Create("ErrorCode", AppErrorCodes.MigrationSettingsCopyFailed)
                    .With("FileName", SettingsFileName)
                    .With("Retryable", true));
            return "failed:copy-error";
        }
    }

    private string RelocateBin(string legacyRoot, string dataBin)
    {
        string sourceBin = Path.Combine(legacyRoot, BinFolderName);

        try
        {
            // Фаза A не удаляет ничего: пустой источник — нечего переносить,
            // иначе запускаем фоновую копию с хэш-проверкой каждого файла.
            // Удаление оригиналов — только в фазе B после живых проб версий.
            if (!Directory.Exists(sourceBin) || IsDirectoryEmpty(sourceBin))
            {
                return "absent-source";
            }

            if (!Directory.Exists(dataBin))
            {
                Directory.CreateDirectory(dataBin);
            }

            StartBackgroundCopy(sourceBin, dataBin);
            return "copy-pending";
        }
        catch (Exception ex)
        {
            WriteEvent(
                AppEventIds.MigrationFailed,
                LogLevel.Warning,
                LogStatus.Failed,
                "Не удалось перенести каталог bin, повтор при следующем запуске",
                ex,
                LogProps
                    .Create("ErrorCode", AppErrorCodes.MigrationBinRelocateFailed)
                    .With("Retryable", true));
            return "failed:relocate-error";
        }
    }

    private void StartBackgroundCopy(string sourceBin, string dataBin)
    {
        lock (_copyLock)
        {
            if (_backgroundCopy != null)
            {
                return;
            }

            // Задание строго аддитивно: дописывает назначение и проверяет хэши,
            // источники не трогает. Обрыв и отмена безопасны: недописанное
            // добирается следующим запуском, удаление — только в фазе B.
            _backgroundCopy = Task.Run(() => CopyDirectoryWithVerify(sourceBin, dataBin));
        }

        WriteEvent(
            AppEventIds.MigrationBinRelocated,
            LogLevel.Info,
            LogStatus.Running,
            "Каталог bin копируется в LocalAppData в фоне, проверка и чистка — после завершения",
            null,
            null);
    }

    /// <summary>
    /// Фоновая побайтовая копия bin с хэш-проверкой каждого файла.
    /// Строго аддитивна: источники не удаляются никогда, удаление — только
    /// в фазе B после живых проб версий. Поэтому обрыв или отмена задания
    /// безопасны: недописанное добирается следующим запуском.
    /// Никогда не бросает исключений: сбой выражается объектом, а не throw.
    /// </summary>
    private BinCopyResult CopyDirectoryWithVerify(string sourceBin, string dataBin)
    {
        int copied = 0;
        int skipped = 0;
        List<string> conflicts = new();

        try
        {
            long totalBytes = 0;
            try
            {
                foreach (string f in EnumerateFilesSafe(sourceBin))
                {
                    totalBytes += new FileInfo(f).Length;
                }
            }
            catch (Exception)
            {
                totalBytes = -1;
            }

            if (totalBytes > 0 && !HasFreeSpaceFor(dataBin, totalBytes))
            {
                return BinCopyResult.Failed(AppErrorCodes.MigrationNoSpace);
            }

            foreach (string sourceFile in EnumerateFilesSafe(sourceBin))
            {
                string relative = Path.GetRelativePath(sourceBin, sourceFile);
                string destFile = Path.Combine(dataBin, relative);

                try
                {
                    string? destDir = Path.GetDirectoryName(destFile);
                    if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                    }

                    if (File.Exists(destFile) && FilesContentEqual(sourceFile, destFile))
                    {
                        // Дубликат уже на месте (повторный прогон): копировать нечего.
                        skipped++;
                        continue;
                    }

                    if (File.Exists(destFile))
                    {
                        // Содержимое различается: назначение не затираем, исходник не трогаем.
                        conflicts.Add(relative);
                        continue;
                    }

                    File.Copy(sourceFile, destFile, overwrite: false);
                    try
                    {
                        File.SetLastWriteTimeUtc(destFile, File.GetLastWriteTimeUtc(sourceFile));
                    }
                    catch (Exception)
                    {
                    }

                    if (!FilesHashEqual(sourceFile, destFile))
                    {
                        TryDeleteFile(destFile);
                        return BinCopyResult.Failed(AppErrorCodes.MigrationBinRelocateFailed);
                    }

                    copied++;
                }
                catch (Exception)
                {
                    conflicts.Add(relative);
                }
            }

            return conflicts.Count > 0 ? BinCopyResult.Partial(conflicts) : BinCopyResult.Ok();
        }
        catch (Exception)
        {
            return BinCopyResult.Failed(AppErrorCodes.MigrationBinRelocateFailed);
        }
    }

    private void CleanupLegacyFiles(
        string legacyRoot,
        string dataRoot,
        string dataBin,
        CancellationToken cancellationToken,
        MigrationReceipt receipt)
    {
        int deleted = 0;
        int kept = 0;

        // Настройки: удаляем исходник только при побайтовом совпадении с назначением.
        // Расхождение означает, что старая версия писала в каталог установки после
        // миграции (откат версии), — такой файл оставляем нетронутым.
        string legacySettings = Path.Combine(legacyRoot, SettingsFileName);
        string newSettings = Path.Combine(dataRoot, SettingsFileName);
        if (File.Exists(legacySettings))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(newSettings) && (FilesHashEqual(legacySettings, newSettings) || IsUntouchedSinceMigration(legacySettings, receipt.MigratedAtUtc)))
            {
                if (TryDeleteFile(legacySettings))
                {
                    deleted++;
                }
                else
                {
                    kept++;
                }
            }
            else
            {
                kept++;
            }
        }

        // Bin: удаляем только файлы, побайтово совпадающие с назначением.
        // Проверка версий по новым путям уже пройдена выше — это вторая линия обороны.
        string legacyBin = Path.Combine(legacyRoot, BinFolderName);
        if (Directory.Exists(legacyBin))
        {
            foreach (string sourceFile in EnumerateFilesSafe(legacyBin))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(legacyBin, sourceFile);
                string destFile = Path.Combine(dataBin, relative);

                if (File.Exists(destFile) && FilesContentEqual(sourceFile, destFile))
                {
                    if (TryDeleteFile(sourceFile))
                    {
                        deleted++;
                    }
                    else
                    {
                        kept++;
                    }
                }
                else
                {
                    kept++;
                }
            }

            DeleteEmptyDirectoriesBottomUp(legacyBin);
            TryDeleteDirectoryIfEmpty(legacyBin);
        }

        // Старые логи и кэш железа — не пользовательские данные, новая версия туда не пишет
        // (логи всегда ведутся через LogFilePolicy в LocalAppData, кэш пересоздаётся пробой NVENC),
        // поэтому удаляем целиком без хэш-проверок.
        DeleteDirectoryTreeBestEffort(Path.Combine(legacyRoot, LogFilePolicy.LogsFolderName));
        TryDeleteFile(Path.Combine(legacyRoot, "nvenc-capabilities.json"));

        receipt.CleanupPending = kept > 0;
        receipt.Note = kept > 0 ? "kept:" + kept : string.Empty;
        receipt.Save(ReceiptPath(dataRoot));

        WriteEvent(
            AppEventIds.MigrationCleanupCompleted,
            kept > 0 ? LogLevel.Warning : LogLevel.Info,
            kept > 0 ? LogStatus.PartiallySucceeded : LogStatus.Succeeded,
            kept > 0
                ? "Проверка пройдена, удалено файлов: " + deleted + ", оставлено отличающихся: " + kept
                : "Проверка пройдена, оригиналы из каталога установки удалены полностью",
            null,
            LogProps
                .Create("Count", deleted)
                .With("Skipped", kept));
    }

    /// <summary>
    /// Заметает следы старых проб записи: маркеры write-probe-*.json в общем
    /// каталоге данных, остатки проб в каталоге установки и остатки проб
    /// журнала в каталоге логов. Вызывается всегда, даже в portable-режиме.
    /// </summary>
    private void SweepStaleProbeMarkers(string legacyRoot)
    {
        string managedRoot;
        try
        {
            managedRoot = PathManager.GetManagedDataRoot();
        }
        catch (Exception)
        {
            managedRoot = string.Empty;
        }

        if (!string.IsNullOrEmpty(managedRoot))
        {
            TryDeleteByPattern(managedRoot, "write-probe-*.json");
            TryDeleteByPattern(Path.Combine(managedRoot, "logs"), ".ktools_write_probe*");
        }

        TryDeleteByPattern(legacyRoot, ".write_test_probe*");
    }

    private void TryDeleteByPattern(string directory, string pattern)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (string file in Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly))
            {
                TryDeleteFile(file);
            }
        }
        catch (Exception)
        {
        }
    }

    private void WriteEvent(
        string eventId,
        LogLevel level,
        LogStatus status,
        string message,
        Exception? exception,
        Dictionary<string, object?>? properties)
    {
        try
        {
            _logService.Write(eventId, level, status, message, exception, SourceName, properties: properties);
        }
        catch (Exception)
        {
            // Журнал недоступен — миграция продолжается без записи.
        }
    }

    private string ReceiptPath(string dataRoot) => Path.Combine(dataRoot, ReceiptFileName);

    private static bool BinNeedsVerification(string binState)
    {
        return binState.StartsWith("copy-pending", StringComparison.Ordinal)
            || binState.StartsWith("copied", StringComparison.Ordinal);
    }

    private static List<string> EvaluateProbes(IReadOnlyList<BinaryProbeResult> probes, string legacyBin)
    {
        List<string> failed = new();
        if (probes is null)
        {
            return failed;
        }

        foreach (BinaryProbeResult probe in probes)
        {
            if (probe.IsSkipped)
            {
                continue;
            }

            bool versionOk = IsRealVersion(probe.Version) || !probe.VersionExpected;
            if (probe.Exists && probe.IsUnderBinDirectory && versionOk)
            {
                continue;
            }

            // Файл отсутствует и в новом месте: проверяем, был ли он в старом.
            // Отсутствует везде — инструмент не устанавливали, это не сбой.
            if (!probe.Exists && !ExistsInLegacy(probe, legacyBin))
            {
                continue;
            }

            failed.Add(probe.Key);
        }

        return failed;
    }

    private static bool ExistsInLegacy(BinaryProbeResult probe, string legacyBin)
    {
        // Разрешённый путь нового места неизвестен (файл не найден) — проверяем
        // канонические legacy-кандидаты по данным реестра из пробы.
        try
        {
            if (string.IsNullOrEmpty(legacyBin) || !Directory.Exists(legacyBin))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(probe.Subfolder) && !string.IsNullOrEmpty(probe.VerifyBinary))
            {
                if (File.Exists(Path.Combine(legacyBin, probe.Subfolder, probe.VerifyBinary)))
                {
                    return true;
                }
            }

            if (!string.IsNullOrEmpty(probe.VerifyBinary) && File.Exists(Path.Combine(legacyBin, probe.VerifyBinary)))
            {
                return true;
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsRealVersion(string version)
    {
        return !string.IsNullOrWhiteSpace(version)
            && !string.Equals(version, BinaryProbeResult.VersionUnknownMarker, StringComparison.Ordinal);
    }

    private static IEnumerable<string> EnumerateFilesSafe(string directory)
    {
        List<string> files = new();
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                files.Add(file);
            }
        }
        catch (Exception)
        {
            // Частичный перечень лучше отсутствия: необойдённые файлы останутся на следующий запуск.
        }

        return files;
    }

    private static bool IsDirectoryEmpty(string directory)
    {
        try
        {
            using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
            return !entries.MoveNext();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void TryDeleteDirectoryIfEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && IsDirectoryEmpty(directory))
            {
                Directory.Delete(directory, recursive: false);
            }
        }
        catch (Exception)
        {
        }
    }

    private static void DeleteEmptyDirectoriesBottomUp(string root)
    {
        try
        {
            List<string> dirs = new(Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories));
            dirs.Sort((a, b) => b.Length.CompareTo(a.Length));
            foreach (string dir in dirs)
            {
                TryDeleteDirectoryIfEmpty(dir);
            }
        }
        catch (Exception)
        {
        }
    }

    private static void DeleteDirectoryTreeBestEffort(string directory)
    {
        try
        {
            foreach (string file in EnumerateFilesSafe(directory))
            {
                TryDeleteFile(file);
            }

            DeleteEmptyDirectoriesBottomUp(directory);
            TryDeleteDirectoryIfEmpty(directory);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Проверяет побайтовое равенство файлов. Совпадение размера и метки времени
    /// доказательством НЕ считается: файлы разного содержимого, записанные в один
    /// тик системных часов, имеют одинаковые метки — удалять по ним запрещено.
    /// Быстрая проверка используется только в одну сторону: разный размер означает
    /// гарантированное различие без чтения содержимого.
    /// </summary>
    /// <returns>True только при совпадении SHA256.</returns>
    private static bool FilesContentEqual(string first, string second)
    {
        try
        {
            var a = new FileInfo(first);
            var b = new FileInfo(second);
            if (!a.Exists || !b.Exists || a.Length != b.Length)
            {
                return false;
            }

            return FilesHashEqual(first, second);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Проверяет, что файл не менялся с момента переноса: такой исходник — протухший бэкап (приложение уже пишет в назначение), удаление безопасно. Файл, изменённый позже (например, записанный откаченной старой версией), трогать запрещено. Допуск 5 секунд — на гранулярность меток.
    /// </summary>
    private static bool IsUntouchedSinceMigration(string path, DateTime migratedAtUtc)
    {
        try { return new FileInfo(path).LastWriteTimeUtc <= migratedAtUtc.AddSeconds(5); }
        catch (Exception) { return false; }
    }

    private static bool FilesHashEqual(string first, string second)
    {
        string? a = ComputeSha256(first);
        string? b = ComputeSha256(second);
        return !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.Ordinal);
    }

    private static string? ComputeSha256(string path)
    {
        try
        {
            using var hasher = SHA256.Create();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);
            return Convert.ToHexString(hasher.ComputeHash(stream));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool HasFreeSpaceFor(string directory, long bytesNeeded)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrEmpty(root))
            {
                return false;
            }

            long free = new DriveInfo(root).AvailableFreeSpace;
            return free > (long)(bytesNeeded * 1.2);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string GetCurrentAppVersion()
    {
        try
        {
            var assembly = typeof(UserDataMigrator).Assembly;
            string? informational = null;
            foreach (var attribute in assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false))
            {
                informational = ((System.Reflection.AssemblyInformationalVersionAttribute)attribute).InformationalVersion;
                break;
            }

            if (!string.IsNullOrWhiteSpace(informational))
            {
                int plus = informational.IndexOf('+');
                string version = plus > 0 ? informational[..plus] : informational;
                if (!string.IsNullOrWhiteSpace(version))
                {
                    return version.Trim();
                }
            }

            return assembly.GetName().Version?.ToString() ?? "0.0.0";
        }
        catch (Exception)
        {
            return "0.0.0";
        }
    }

    /// <summary>
    /// Результат фонового копирования bin между томами.
    /// Никогда не бросает исключений: сбой выражается объектом, а не throw.
    /// </summary>
    private sealed class BinCopyResult
    {
        public bool Completed { get; private set; }

        public string ErrorCode { get; private set; } = string.Empty;

        public int Conflicts { get; private set; }

        public static BinCopyResult Ok() => new() { Completed = true };

        public static BinCopyResult Partial(List<string> conflicts) => new() { Completed = true, Conflicts = conflicts.Count };

        public static BinCopyResult Failed(string errorCode) => new() { Completed = false, ErrorCode = errorCode };
    }

    /// <summary>
    /// Квитанция миграции: делает повторные запуски идемпотентными.
    /// Любая порча файла трактуется как его отсутствие (миграция повторяется безопасно).
    /// </summary>
    private sealed class MigrationReceipt
    {
        public string? SourceDir { get; set; }

        public string? AppVersion { get; set; }

        public DateTime MigratedAtUtc { get; set; }

        public string Settings { get; set; } = "none";

        public string Bin { get; set; } = "none";

        public bool Verified { get; set; }

        public bool CleanupPending { get; set; }

        public string Note { get; set; } = string.Empty;

        public bool MatchesSource(string legacyRoot)
        {
            if (string.IsNullOrWhiteSpace(SourceDir))
            {
                return false;
            }

            try
            {
                return string.Equals(
                    Path.GetFullPath(SourceDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(legacyRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool IsVerifiedCleanFor(string legacyRoot) => MatchesSource(legacyRoot) && Verified && !CleanupPending;

        public void Save(string path)
        {
            try
            {
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string json = JsonSerializer.Serialize(this);
                string temp = path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception)
            {
                // Квитанция необязательна: без неё миграция просто повторится идемпотентно.
            }
        }

        public static MigrationReceipt Load(string path, string legacyRoot)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return NewFor(legacyRoot);
                }

                string json = File.ReadAllText(path);
                MigrationReceipt? receipt = JsonSerializer.Deserialize<MigrationReceipt>(json);
                if (receipt is null || !receipt.MatchesSource(legacyRoot))
                {
                    return NewFor(legacyRoot);
                }

                return receipt;
            }
            catch (Exception)
            {
                return NewFor(legacyRoot);
            }
        }

        private static MigrationReceipt NewFor(string legacyRoot)
        {
            return new MigrationReceipt { SourceDir = legacyRoot };
        }
    }
}
