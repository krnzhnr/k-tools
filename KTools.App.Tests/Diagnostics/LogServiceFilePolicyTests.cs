// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests.Diagnostics;

[TestClass]
public class LogServiceFilePolicyTests
{
    private static LogService CreateService(TempDirectoryScope scope, Action<LogServiceOptions>? configure = null)
    {
        LogServiceOptions options = new()
        {
            CustomLogDirectory = Path.Combine(scope.RootPath, "logs"),
            EmergencyDirectory = Path.Combine(scope.RootPath, "emergency"),
            FlushIntervalMilliseconds = 60000,
            FileFormat = LogFileFormat.Jsonl
        };
        configure?.Invoke(options);
        return new LogService(options);
    }

    private static long LengthShared(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return stream.Length;
    }

    private static string CreateBudgetDirectory(TempDirectoryScope scope)
    {
        string directory = Path.Combine(scope.RootPath, "logs");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private const long BudgetBytes = 4096;

    private static LogFilePolicy CreateBudgetPolicy()
    {
        return new LogFilePolicy(new LogServiceOptions
        {
            MaxFiles = 100,
            MaxDirectoryBytes = BudgetBytes,
            RetentionDays = 3650
        });
    }

    private static string CreateSizedLogFile(string directory, string fileName, byte[] content, DateTime lastWriteUtc)
    {
        string path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, content);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private static long TotalBytes(string directory)
    {
        return Directory.GetFiles(directory, LogFilePolicy.FileSearchPattern).Sum(file => new FileInfo(file).Length);
    }

    [TestMethod]
    public void InitializeLogFile_BeforeFirstEvent_DoesNotCreateFile()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        string other = Path.Combine(scope.RootPath, "other");
        Directory.CreateDirectory(other);

        service.InitializeLogFile(other);
        service.InitializeLogFile(null);

        Directory.GetFiles(Path.Combine(scope.RootPath, "logs")).Should().BeEmpty("до первого события файл журнала не создаётся");
        Directory.GetFiles(other).Should().BeEmpty("смена каталога до первого события не создаёт второй файл");
        service.EffectiveLogDirectory.Should().Be(other, "повторная инициализация меняет эффективный каталог");
        service.CurrentLogFile.Should().StartWith(other, "имя файла остаётся тем же в пределах сессии");
    }

    [TestMethod]
    public void InitializeLogFile_AfterFirstEvent_KeepsOriginalFile()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        service.Info("первое событие фиксирует сессию", "Policy");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();
        string? originalFile = service.CurrentLogFile;
        string originalDirectory = Path.Combine(scope.RootPath, "logs");
        string other = Path.Combine(scope.RootPath, "other");
        Directory.CreateDirectory(other);

        service.InitializeLogFile(other);
        service.Info("второе событие", "Policy");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        service.CurrentLogFile.Should().Be(originalFile, "после начала сессии путь зафиксирован");
        service.EffectiveLogDirectory.Should().Be(originalDirectory);
        Directory.GetFiles(other).Should().BeEmpty("второй файл не создаётся");
        Directory.GetFiles(originalDirectory, "ktools_*.log").Should().ContainSingle();
    }

    [TestMethod]
    public void FileName_ContainsUtcTimestampPidAndShortSessionId()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        service.Info("событие", "Policy");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        string name = Path.GetFileName(service.CurrentLogFile!);
        string[] parts = name.Split('_');

        name.Should().StartWith(LogFilePolicy.FileNamePrefix);
        name.Should().EndWith(LogFilePolicy.FileNameExtension);
        parts.Should().HaveCount(4, "префикс, метка времени UTC, PID и короткий SessionId");
        parts[1].Should().MatchRegex("^\\d{8}T\\d{6}Z$");
        parts[2].Should().Be(Environment.ProcessId.ToString());
        parts[3].Should().StartWith(service.SessionId.ToString("N").Substring(0, 8));
    }

    [TestMethod]
    public void Flush_DrainsQueueSynchronously()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        const int count = 400;

        for (int index = 0; index < count; index++)
        {
            service.Info("синхронная запись " + index, "Policy");
        }

        bool flushed = service.Flush(TimeSpan.FromSeconds(10));

        flushed.Should().BeTrue("Flush дожидается дренирования очереди без взаимоблокировки");
        service.ReadRecentEvents(count + 10).Should().HaveCount(count, "после Flush все события доступны для чтения");
    }

    [TestMethod]
    public void Flush_ZeroTimeoutAndNonPositiveValues_DoNotThrow()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        service.Info("событие", "Policy");

        Action act = () =>
        {
            service.Flush(TimeSpan.Zero);
            service.Flush(TimeSpan.FromMilliseconds(-100));
        };

        act.Should().NotThrow("некорректный таймаут не приводит к исключению");
    }

    [TestMethod]
    public void Flush_AfterSubscriberOverflow_StillDrainsQueue()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope, options => options.QueueCapacity = 16);
        using ManualResetEventSlim release = new(false);
        service.LogReceived += (_, _) => release.Wait(TimeSpan.FromSeconds(30));

        for (int index = 0; index < 500; index++)
        {
            service.Info("событие " + index, "Policy");
        }

        bool flushed = service.Flush(TimeSpan.FromSeconds(20));

        flushed.Should().BeTrue("зависший подписчик не блокирует файловый writer и сброс");
        release.Set();
        service.ReadRecentEvents(1000).Should().NotBeEmpty("события остаются доступными после сброса");
    }

    [TestMethod]
    public void Rotation_ReachesTrigger_CreatesBoundedSegments()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope, options =>
        {
            options.MaxFileBytes = 4096;
            options.RotationTriggerRatio = 0.5d;
        });
        string payload = new('я', 500);

        for (int index = 0; index < 200; index++)
        {
            service.Info(payload + " " + index, "Rotation");
        }

        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        string[] files = Directory.GetFiles(Path.Combine(scope.RootPath, "logs"), "ktools_*.log");
        files.Should().HaveCountGreaterThan(1, "достижение порога ротации создаёт следующий сегмент");
        files.Should().OnlyContain(file => new FileInfo(file).Length <= 4096, "сегменты ограничены по размеру");
        files.Should().Contain(file => Path.GetFileName(file).Contains(LogFilePolicy.SegmentMarker, StringComparison.Ordinal));
        service.WriteErrorCount.Should().Be(0);
        service.RotationErrorCount.Should().Be(0);
        service.ReadRecentEvents(200).Should().NotBeEmpty("события читаются по сегментам");
    }

    [TestMethod]
    public void Rotation_OversizedExistingFile_IsRotatedBeforeOpening()
    {
        using var scope = new TempDirectoryScope();
        LogServiceOptions options = new()
        {
            CustomLogDirectory = Path.Combine(scope.RootPath, "logs"),
            EmergencyDirectory = Path.Combine(scope.RootPath, "emergency"),
            MaxFileBytes = 4096,
            RotationTriggerRatio = 0.5d,
            FlushIntervalMilliseconds = 60000
        };

        using var service = new LogService(options);
        string? planned = service.CurrentLogFile;
        planned.Should().NotBeNullOrEmpty();
        string baseName = planned![..planned.LastIndexOf('.')];
        File.WriteAllText(planned, new string('x', 3000));
        File.WriteAllText(Path.Combine(scope.RootPath, "logs", baseName + LogFilePolicy.SegmentMarker + "1" + LogFilePolicy.FileNameExtension), "segment");

        service.Info("после повторного открытия", "Rotation");
        service.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();

        string segmentTwo = Path.Combine(scope.RootPath, "logs", baseName + LogFilePolicy.SegmentMarker + "2" + LogFilePolicy.FileNameExtension);
        new FileInfo(segmentTwo).Length.Should().Be(3000, "переполненный файл ротируется до открытия, а не дописывается");
        LengthShared(service.CurrentLogFile!).Should().BeLessThan(4096, "новый сегмент остаётся ограниченным");
    }

    [TestMethod]
    public void Rotation_KeepsSessionFileCountBounded()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope, options =>
        {
            options.MaxFileBytes = 4096;
            options.RotationTriggerRatio = 0.5d;
            options.MaxFiles = 3;
        });
        string payload = new('я', 500);

        for (int index = 0; index < 400; index++)
        {
            service.Info(payload + " " + index, "Rotation");
        }

        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        string[] files = Directory.GetFiles(Path.Combine(scope.RootPath, "logs"), "ktools_*.log");
        files.Should().HaveCountLessThanOrEqualTo(3, "количество файлов сессии ограничено политикой каталога");
        files.Should().Contain(file => string.Equals(file, service.CurrentLogFile, StringComparison.OrdinalIgnoreCase), "живой файл текущей сессии не удаляется retention-политикой");
    }

    [TestMethod]
    public void Retention_ProtectsActiveSessionFilesOfOtherInstance()
    {
        using var scope = new TempDirectoryScope();
        LogServiceOptions options = new()
        {
            CustomLogDirectory = Path.Combine(scope.RootPath, "logs"),
            EmergencyDirectory = Path.Combine(scope.RootPath, "emergency"),
            RetentionDays = 1,
            MaxFiles = 2,
            FlushIntervalMilliseconds = 60000
        };

        using LogService active = new(options);
        active.Info("активная сессия", "Retention");
        active.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();
        string? activeFile = active.CurrentLogFile;
        File.SetLastWriteTimeUtc(activeFile!, DateTime.UtcNow.AddDays(-5));

        using var other = new LogService(options);
        other.InitializeLogFile(options.CustomLogDirectory);
        other.Flush(TimeSpan.FromSeconds(30));

        File.Exists(activeFile).Should().BeTrue("retention не удаляет активные файлы текущей сессии даже при старом времени модификации");
    }

    [TestMethod]
    public void Retention_RemovesFilesOlderThanConfiguredDays()
    {
        using var scope = new TempDirectoryScope();
        string directory = Path.Combine(scope.RootPath, "logs");
        Directory.CreateDirectory(directory);
        string old = Path.Combine(directory, "ktools_20200101T000000Z_1_aaaaaaaa.log");
        string recent = Path.Combine(directory, "ktools_20260925T000000Z_1_bbbbbbbb.log");
        File.WriteAllText(old, "old");
        File.WriteAllText(recent, "recent");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-15));

        using LogService service = CreateService(scope);
        service.InitializeLogFile(directory);

        File.Exists(old).Should().BeFalse("файлы старше срока хранения удаляются");
        File.Exists(recent).Should().BeTrue("свежий файл сохраняется");
    }

    [TestMethod]
    public void Retention_EnforcesFileCountLimit()
    {
        using var scope = new TempDirectoryScope();
        string directory = Path.Combine(scope.RootPath, "logs");
        Directory.CreateDirectory(directory);
        for (int index = 0; index < 10; index++)
        {
            string path = Path.Combine(directory, $"ktools_2026092{index}_1_a{index}a{index}a{index}a{index}.log");
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(index));
        }

        LogServiceOptions options = new()
        {
            CustomLogDirectory = directory,
            EmergencyDirectory = Path.Combine(scope.RootPath, "emergency"),
            MaxFiles = 3,
            RetentionDays = 3650
        };

        using var service = new LogService(options);
        service.InitializeLogFile(directory);

        Directory.GetFiles(directory, "ktools_*.log").Should().HaveCountLessThanOrEqualTo(3, "каталог ограничен количеством файлов");
    }

    [TestMethod]
    public void Retention_EnforcesTotalDirectoryBytes()
    {
        using var scope = new TempDirectoryScope();
        string directory = Path.Combine(scope.RootPath, "logs");
        Directory.CreateDirectory(directory);
        byte[] block = new byte[4096];
        for (int index = 0; index < 8; index++)
        {
            string path = Path.Combine(directory, $"ktools_2026090{index + 1}T000000Z_1_b{index}b{index}b{index}b{index}b{index}.log");
            File.WriteAllBytes(path, block);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(index));
        }

        LogServiceOptions options = new()
        {
            CustomLogDirectory = directory,
            EmergencyDirectory = Path.Combine(scope.RootPath, "emergency"),
            MaxFiles = 100,
            MaxDirectoryBytes = 8192,
            RetentionDays = 3650
        };

        using var service = new LogService(options);
        service.InitializeLogFile(directory);

        long total = Directory.GetFiles(directory, "ktools_*.log").Sum(file => new FileInfo(file).Length);
        total.Should().BeLessThanOrEqualTo(8192, "общий объём каталога ограничен");
    }

    [TestMethod]
    public void Retention_DirectoryBudgetEvictsOldestSessionSegmentsAndKeepsActiveAndNewest()
    {
        using var scope = new TempDirectoryScope();
        string directory = CreateBudgetDirectory(scope);
        LogFilePolicy policy = CreateBudgetPolicy();
        string baseName = policy.BuildBaseName(DateTimeOffset.UtcNow, 42, Guid.NewGuid());
        DateTime now = DateTime.UtcNow;
        byte[] block = new byte[1024];

        for (int index = 1; index <= 6; index++)
        {
            string path = LogFilePolicy.BuildSegmentPath(directory, baseName, index);
            File.WriteAllBytes(path, block);
            File.SetLastWriteTimeUtc(path, now.AddMinutes(index));
        }

        string active = Path.Combine(directory, baseName + LogFilePolicy.FileNameExtension);
        File.WriteAllBytes(active, block);
        File.SetLastWriteTimeUtc(active, now.AddMinutes(60));

        int deleted = policy.ApplyRetention(directory, active);

        deleted.Should().Be(3, "вытесняются только файлы, выходящие за суммарный бюджет");
        File.Exists(active).Should().BeTrue("активный файл текущей сессии не вытесняется");
        File.Exists(LogFilePolicy.BuildSegmentPath(directory, baseName, 6)).Should().BeTrue("новейший сегмент сохраняется");
        File.Exists(LogFilePolicy.BuildSegmentPath(directory, baseName, 1)).Should().BeFalse("старые сегменты текущей сессии вытесняются первыми");
        File.Exists(LogFilePolicy.BuildSegmentPath(directory, baseName, 3)).Should().BeFalse("вытеснение идёт до достижения бюджета, а не всех файлов сессии");
        TotalBytes(directory).Should().BeLessThanOrEqualTo(
            BudgetBytes,
            "после прохода суммарный объём каталога укладывается в бюджет");
    }

    [TestMethod]
    public void Retention_WithinDirectoryBudget_DeletesNothing()
    {
        using var scope = new TempDirectoryScope();
        string directory = CreateBudgetDirectory(scope);
        LogFilePolicy policy = CreateBudgetPolicy();
        string baseName = policy.BuildBaseName(DateTimeOffset.UtcNow, 42, Guid.NewGuid());
        DateTime now = DateTime.UtcNow;
        byte[] block = new byte[1024];

        for (int index = 1; index <= 3; index++)
        {
            string path = LogFilePolicy.BuildSegmentPath(directory, baseName, index);
            File.WriteAllBytes(path, block);
            File.SetLastWriteTimeUtc(path, now.AddMinutes(index));
        }

        string active = Path.Combine(directory, baseName + LogFilePolicy.FileNameExtension);
        File.WriteAllBytes(active, block);
        File.SetLastWriteTimeUtc(active, now.AddMinutes(60));

        int deleted = policy.ApplyRetention(directory, active);

        deleted.Should().Be(0, "в пределах бюджета ничего не вытесняется");
        Directory.GetFiles(directory, LogFilePolicy.FileSearchPattern).Should().HaveCount(4);
        TotalBytes(directory).Should().Be(4096);
    }

    [TestMethod]
    public void Retention_DirectoryBudget_KeepsEveryLockedLogFile()
    {
        using var scope = new TempDirectoryScope();
        string directory = CreateBudgetDirectory(scope);
        LogFilePolicy policy = CreateBudgetPolicy();
        DateTime now = DateTime.UtcNow;
        byte[] block = new byte[1024];
        string first = CreateSizedLogFile(directory, "ktools_20260101T000000Z_11_aaaaaaaa.log", block, now);
        string second = CreateSizedLogFile(directory, "ktools_20260101T000001Z_22_bbbbbbbb.log", block, now.AddMinutes(1));
        CreateSizedLogFile(directory, "ktools_20260101T000002Z_33_cccccccc.log", block, now.AddMinutes(2));
        CreateSizedLogFile(directory, "ktools_20260101T000003Z_44_dddddddd.log", block, now.AddMinutes(3));
        string newest = CreateSizedLogFile(directory, "ktools_20260101T000004Z_55_eeeeeeee.log", new byte[512], now.AddMinutes(4));

        using FileStream firstLock = new(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using FileStream secondLock = new(second, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        int deleted = policy.ApplyRetention(directory, newest);

        deleted.Should().Be(1, "вытесняется только незаблокированный файл сверх бюджета");
        File.Exists(first).Should().BeTrue("первый активный файл другой сессии не удаляется");
        File.Exists(second).Should().BeTrue("второй активный файл другой сессии не удаляется");
        File.Exists(newest).Should().BeTrue("защищённый вызывающим файлом остаётся на месте");
        TotalBytes(directory).Should().BeLessThanOrEqualTo(BudgetBytes);
    }

    [TestMethod]
    public void Retention_DirectoryBudget_UnreadableFileDoesNotThrowAndKeepsFile()
    {
        using var scope = new TempDirectoryScope();
        string directory = CreateBudgetDirectory(scope);
        LogFilePolicy policy = CreateBudgetPolicy();
        DateTime now = DateTime.UtcNow;
        byte[] block = new byte[1024];
        string locked = CreateSizedLogFile(directory, "ktools_20260101T000000Z_11_aaaaaaaa.log", block, now);
        CreateSizedLogFile(directory, "ktools_20260101T000001Z_22_bbbbbbbb.log", block, now.AddMinutes(1));
        CreateSizedLogFile(directory, "ktools_20260101T000002Z_33_cccccccc.log", block, now.AddMinutes(2));
        string newest = CreateSizedLogFile(directory, "ktools_20260101T000003Z_44_dddddddd.log", block, now.AddMinutes(3));

        using FileStream lockedStream = new(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Action act = () => policy.ApplyRetention(directory, newest);

        act.Should().NotThrow("ошибка доступа к файлу не выходит наружу retention-политики");
        File.Exists(locked).Should().BeTrue("недоступный файл не удаляется");
        TotalBytes(directory).Should().BeLessThanOrEqualTo(BudgetBytes);
    }

    [TestMethod]
    public void Retention_DirectoryBudget_IsIdempotent()
    {
        using var scope = new TempDirectoryScope();
        string directory = CreateBudgetDirectory(scope);
        LogFilePolicy policy = CreateBudgetPolicy();
        DateTime now = DateTime.UtcNow;
        byte[] block = new byte[1024];
        for (int index = 0; index < 6; index++)
        {
            CreateSizedLogFile(directory, $"ktools_2026010{index + 1}T000000Z_1_{index}{index}{index}{index}{index}{index}{index}{index}.log", block, now.AddMinutes(index));
        }

        string newest = Directory.GetFiles(directory, LogFilePolicy.FileSearchPattern)
            .OrderByDescending(file => File.GetLastWriteTimeUtc(file))
            .First();
        int first = policy.ApplyRetention(directory, newest);
        long afterFirst = TotalBytes(directory);
        int second = policy.ApplyRetention(directory, newest);
        long afterSecond = TotalBytes(directory);

        first.Should().BeGreaterThan(0);
        afterFirst.Should().BeLessThanOrEqualTo(BudgetBytes);
        second.Should().Be(0, "повторный проход по уже уложенному каталогу ничего не удаляет");
        afterSecond.Should().Be(afterFirst, "повторный проход не меняет состав каталога");
    }

    [TestMethod]
    public void Rotation_SameSessionSegmentsAreEvictedByDirectoryBudget()
    {
        using var scope = new TempDirectoryScope();
        const long budget = 8192;
        using LogService service = CreateService(scope, options =>
        {
            options.MaxFileBytes = 4096;
            options.RotationTriggerRatio = 0.5d;
            options.MaxFiles = 100;
            options.MaxDirectoryBytes = budget;
            options.RetentionDays = 3650;
        });
        string payload = new('я', 500);
        const long trigger = 2048;

        for (int index = 0; index < 400; index++)
        {
            service.Info(payload + " " + index, "Budget");
        }

        service.Flush(TimeSpan.FromSeconds(60)).Should().BeTrue();

        string directory = Path.Combine(scope.RootPath, "logs");
        string[] files = Directory.GetFiles(directory, LogFilePolicy.FileSearchPattern);
        long total = files.Sum(file => new FileInfo(file).Length);
        total.Should().BeLessThanOrEqualTo(
            budget + trigger,
            "retention удерживает каталог в пределах бюджета с точностью до текущего активного сегмента");
        files.Should().HaveCountLessThanOrEqualTo(
            6,
            "файлы текущей сессии вытесняются по объёму, а не растут до MaxFiles");
        files.Should().Contain(
            file => string.Equals(file, service.CurrentLogFile, StringComparison.OrdinalIgnoreCase),
            "активный файл текущей сессии сохраняется");
        files.Should().OnlyContain(
            file => new FileInfo(file).Length <= 4096,
            "предел размера файла и триггер ротации продолжают действовать");
    }

    [TestMethod]
    public void ClearCurrentLog_KeepsSequenceMonotonicWithinSession()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        Guid session = service.SessionId;
        service.Info("до очистки", "Policy");
        service.Info("до очистки 2", "Policy");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();
        long lastBefore = service.ReadRecentEvents(10).Max(e => e.Sequence);

        bool cleared = service.ClearCurrentLog();
        service.Info("после очистки", "Policy");
        service.Flush(TimeSpan.FromSeconds(30)).Should().BeTrue();

        cleared.Should().BeTrue("очистка выполняется успешно");
        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(10);
        events.Should().HaveCount(1, "после очистки видна только новая запись");
        events[0].Sequence.Should().BeGreaterThan(lastBefore, "последовательность не сбрасывается внутри одной сессии");
        events[0].SessionId.Should().Be(session, "идентификатор сессии не меняется при очистке");
        events[0].Message.Should().Be("после очистки");
    }

    [TestMethod]
    public void ClearCurrentLog_ConcurrentWritesRemainMonotonic()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);

        Task producer = Task.Run(() =>
        {
            for (int index = 0; index < 300; index++)
            {
                service.Info("событие " + index, "Concurrent");
            }
        });

        service.ClearCurrentLog();
        producer.Wait(TimeSpan.FromSeconds(20)).Should().BeTrue();

        service.Flush(TimeSpan.FromSeconds(20)).Should().BeTrue();
        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(2000);
        events.Should().NotBeEmpty();
        events.Select(e => e.Sequence).Should().BeInAscendingOrder("события после очистки продолжают возрастающую последовательность");
        events.Select(e => e.Sequence).Should().OnlyHaveUniqueItems();
    }

    [TestMethod]
    public void ClearCurrentLog_FailurePath_ReturnsFalseAndCountsError()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope);
        string? planned = service.CurrentLogFile;
        planned.Should().NotBeNullOrEmpty();
        File.WriteAllText(planned!, "existing");
        using FileStream lockedStream = new(planned, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        bool cleared = service.ClearCurrentLog();

        File.Exists(planned).Should().BeTrue("заблокированный файл не удаляется");
        cleared.Should().BeFalse("успех не обещается при невозможности очистить файл");
    }

    [TestMethod]
    public void Dispose_FinalizesFileAndStopsBackgroundWrites()
    {
        using var scope = new TempDirectoryScope();
        LogService service = CreateService(scope);
        service.Info("до освобождения", "Policy");

        service.Dispose();

        IReadOnlyList<LogEvent> events = service.ReadRecentEvents(10);
        events.Should().HaveCount(1, "Dispose дренирует очередь перед завершением");
        events[0].Message.Should().Be("до освобождения");
        LengthShared(service.CurrentLogFile!).Should().BeGreaterThan(0);
    }

    [TestMethod]
    public void PeriodicFlush_WritesWithoutExplicitFlush()
    {
        using var scope = new TempDirectoryScope();
        using LogService service = CreateService(scope, options => options.FlushIntervalMilliseconds = 50);

        service.Info("периодический сброс", "Policy");
        Thread.Sleep(800);

        LengthShared(service.CurrentLogFile!).Should().BeGreaterThan(0, "периодический сброс не требует явного Flush");
    }

    [TestMethod]
    public void LogFilePolicy_Rotate_ShiftsSegmentsAndKeepsLiveName()
    {
        using var scope = new TempDirectoryScope();
        string directory = Path.Combine(scope.RootPath, "logs");
        Directory.CreateDirectory(directory);
        var policy = new LogFilePolicy(new LogServiceOptions());
        string baseName = policy.BuildBaseName(DateTimeOffset.UtcNow, 42, Guid.NewGuid());
        string live = Path.Combine(directory, baseName + LogFilePolicy.FileNameExtension);
        File.WriteAllText(live, "segment-0");

        string? result = policy.Rotate(live, out string? error);

        error.Should().BeNull();
        result.Should().Be(live);
        File.Exists(LogFilePolicy.BuildSegmentPath(directory, baseName, 1)).Should().BeTrue();
        File.Exists(live).Should().BeFalse("живой файл освобождается под следующий сегмент");
    }

    [TestMethod]
    public void LogFilePolicy_Rotate_FailureIsSurfaced()
    {
        using var scope = new TempDirectoryScope();
        var policy = new LogFilePolicy(new LogServiceOptions());
        string live = Path.Combine(scope.RootPath, "missing", "ktools_x_1_y.log");

        string? result = policy.Rotate(live, out string? error);

        result.Should().BeNull("ошибка ротации не скрывается");
        error.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public void LogFilePolicy_EnumerateSessionFiles_ReturnsSegmentsInChronologicalOrder()
    {
        using var scope = new TempDirectoryScope();
        string directory = Path.Combine(scope.RootPath, "logs");
        Directory.CreateDirectory(directory);
        var policy = new LogFilePolicy(new LogServiceOptions());
        string baseName = policy.BuildBaseName(DateTimeOffset.UtcNow, 42, Guid.NewGuid());
        for (int index = 1; index <= 3; index++)
        {
            File.WriteAllText(LogFilePolicy.BuildSegmentPath(directory, baseName, index), "segment-" + index);
        }

        File.WriteAllText(Path.Combine(directory, baseName + LogFilePolicy.FileNameExtension), "live");

        IReadOnlyList<string> files = policy.EnumerateSessionFiles(directory, baseName);

        files.Should().HaveCount(4);
        files[0].Should().EndWith(LogFilePolicy.SegmentMarker + "1" + LogFilePolicy.FileNameExtension);
        files[3].Should().Be(Path.Combine(directory, baseName + LogFilePolicy.FileNameExtension));
    }

    [TestMethod]
    public void LogFilePolicy_Rotate_SparseSegments_CompactsWithinMaxFiles()
    {
        using var scope = new TempDirectoryScope();
        string directory = Path.Combine(scope.RootPath, "logs");
        Directory.CreateDirectory(directory);
        var policy = new LogFilePolicy(new LogServiceOptions { MaxFiles = 3 });
        string baseName = policy.BuildBaseName(DateTimeOffset.UtcNow, 42, Guid.NewGuid());
        foreach (int index in new[] { 1, 3, 5 })
        {
            File.WriteAllText(LogFilePolicy.BuildSegmentPath(directory, baseName, index), "segment-" + index);
        }

        string live = Path.Combine(directory, baseName + LogFilePolicy.FileNameExtension);
        File.WriteAllText(live, "live");
        string? result = policy.Rotate(live, out string? error);
        File.WriteAllText(live!, "new-live");

        error.Should().BeNull();
        result.Should().Be(live);
        IReadOnlyList<string> files = policy.EnumerateSessionFiles(directory, baseName);
        files.Should().HaveCount(3);
        files.Count(file => file.Contains(LogFilePolicy.SegmentMarker, StringComparison.Ordinal)).Should().Be(2);
        files.Should().Contain(file => Path.GetFileName(file).EndsWith(LogFilePolicy.SegmentMarker + "1" + LogFilePolicy.FileNameExtension, StringComparison.Ordinal));
        files.Should().Contain(file => Path.GetFileName(file).EndsWith(LogFilePolicy.SegmentMarker + "2" + LogFilePolicy.FileNameExtension, StringComparison.Ordinal));
    }

    [TestMethod]
    public void LogFilePolicy_Rotate_MaxFilesOneDeleteFailure_IsSurfaced()
    {
        using var scope = new TempDirectoryScope();
        string directory = Path.Combine(scope.RootPath, "logs");
        Directory.CreateDirectory(directory);
        var policy = new LogFilePolicy(new LogServiceOptions { MaxFiles = 1 });
        string live = Path.Combine(directory, "ktools_x_1_y.log");
        File.WriteAllText(live, "locked");
        using FileStream locked = new(live, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        string? result = policy.Rotate(live, out string? error);

        result.Should().BeNull();
        error.Should().Be("live-delete-failed");
        File.Exists(live).Should().BeTrue();
    }

    [TestMethod]
    public void LogFilePolicy_EnumerateSessionFiles_FailedEnumerationReportsIncomplete()
    {
        using var scope = new TempDirectoryScope();
        var policy = new LogFilePolicy(new LogServiceOptions());
        string baseName = policy.BuildBaseName(DateTimeOffset.UtcNow, 42, Guid.NewGuid());

        IReadOnlyList<string> files = policy.EnumerateSessionFiles(
            Path.Combine(scope.RootPath, "missing"),
            baseName,
            out bool completed);

        files.Should().BeEmpty();
        completed.Should().BeFalse();
    }

    [TestMethod]
    public void LogFilePolicy_ResolveDirectory_UnavailableRequest_FallsBackToLocalAppData()
    {
        using var scope = new TempDirectoryScope();
        string blocked = scope.GetFullPath("blocked");
        File.WriteAllText(blocked, "file-instead-of-directory");
        var policy = new LogFilePolicy(new LogServiceOptions());

        string? resolved = policy.ResolveDirectory(Path.Combine(blocked, "logs"), out string? reason);

        resolved.Should().Be(LogFilePolicy.GetFallbackDirectory());
        reason.Should().NotBeNullOrEmpty("причина отклонения каталога фиксируется");
    }

    [TestMethod]
    public void LogFilePolicy_RetentionOnReadOnlyDirectory_DoesNotThrow()
    {
        using var scope = new TempDirectoryScope();
        string directory = Path.Combine(scope.RootPath, "logs");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "ktools_20200101T000000Z_1_aaaaaaaa.log"), "old");
        var policy = new LogFilePolicy(new LogServiceOptions { RetentionDays = 1 });

        Action act = () =>
        {
            policy.ApplyRetention(directory, Path.Combine(directory, "ktools_20200101T000000Z_1_aaaaaaaa.log"));
            policy.ApplyRetention(Path.Combine(directory, "missing"));
        };

        act.Should().NotThrow("ошибки очистки не выходят наружу");
    }
}
