// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.Services.Implementations;
using KTools_App.Tests.TestHelpers;
using Moq;

namespace KTools_App.Tests;

/// <summary>
/// Проверки одноразовой миграции данных в LocalAppData (ишью 46):
/// копия настроек с верификацией, фоновое копирование bin, опрос версий по новым путям
/// и удаление оригиналов только при полном успехе. Все тесты герметичны:
/// IPathManager замокан на временные каталоги.
/// </summary>
/// <remarks>
/// Класс помечен DoNotParallelize: тесты делят именованный мьютекс миграции
/// операционной системы, параллельный запуск давал бы ложные «отложенные» прогоны.
/// </remarks>
[TestClass]
[DoNotParallelize]
public class UserDataMigrationTests
{
    private readonly List<IDisposable> _scopes = new();

    [TestCleanup]
    public void Cleanup()
    {
        foreach (IDisposable scope in _scopes)
        {
            try
            {
                scope.Dispose();
            }
            catch (Exception)
            {
            }
        }

        _scopes.Clear();
    }

    [TestMethod]
    public void EnsureMigrated_PortableMode_DoesNothing()
    {
        // Arrange — portable-режим: данные остаются рядом с exe.
        var context = CreateContext(portable: true);
        context.Legacy.CreateFile("settings.json", "{\"theme\":\"dark\"}");

        // Act
        context.Migrator.EnsureMigrated();

        // Assert — в назначении ничего не появилось, квитанции нет, отката нет.
        File.Exists(context.Data.GetFullPath("settings.json")).Should().BeFalse();
        File.Exists(context.Data.GetFullPath(".migration.json")).Should().BeFalse();
        context.PathMock.Verify(p => p.EnterLegacyFallbackMode(), Times.Never);
    }

    [TestMethod]
    public void EnsureMigrated_SettingsOnly_CopiesAndVerifies()
    {
        // Arrange — только настройки, bin отсутствует.
        var context = CreateContext();
        const string content = "{\"theme\":\"dark\",\"language\":\"ru\"}";
        context.Legacy.CreateFile("settings.json", content);

        // Act
        context.Migrator.EnsureMigrated();

        // Assert — копия побайтово совпадает, исходник цел (чистка — только в фазе B).
        string dest = context.Data.GetFullPath("settings.json");
        File.Exists(dest).Should().BeTrue();
        File.ReadAllText(dest).Should().Be(File.ReadAllText(context.Legacy.GetFullPath("settings.json")));
        File.Exists(context.Legacy.GetFullPath("settings.json")).Should().BeTrue("фаза A никогда не удаляет настройки");
        File.Exists(context.Data.GetFullPath(".migration.json")).Should().BeTrue("квитанция обязана сохраняться");
    }

    [TestMethod]
    public void EnsureMigrated_SettingsDestExists_DoesNotOverwrite()
    {
        // Arrange — в назначении уже есть (более новые) настройки.
        var context = CreateContext();
        context.Legacy.CreateFile("settings.json", "{\"theme\":\"old\"}");
        context.Data.CreateFile("settings.json", "{\"theme\":\"new\"}");

        // Act
        context.Migrator.EnsureMigrated();

        // Assert — назначение не затирается, исходник на месте.
        File.ReadAllText(context.Data.GetFullPath("settings.json")).Should().Be("{\"theme\":\"new\"}");
        File.Exists(context.Legacy.GetFullPath("settings.json")).Should().BeTrue();
        context.PathMock.Verify(p => p.EnterLegacyFallbackMode(), Times.Never);
    }

    [TestMethod]
    public void EnsureMigrated_BinCopy_PhaseAKeepsSources()
    {
        // Arrange — старый bin с подпапкой, назначение пустое.
        var context = CreateContext();
        string legacyBin = context.Legacy.GetFullPath("bin");
        Directory.CreateDirectory(context.Data.GetFullPath("bin"));
        context.Legacy.CreateFile(Path.Combine("bin", "ffmpeg", "kt-ffmpeg.exe"), "binary-bytes");
        context.Legacy.CreateFile(Path.Combine("bin", "readme.txt"), "notes");

        // Act — фаза A только дописывает назначение, ничего не удаляет.
        context.Migrator.EnsureMigrated();

        // Assert — исходники целы, проверка ещё предстоит.
        File.Exists(context.Legacy.GetFullPath(Path.Combine("bin", "ffmpeg", "kt-ffmpeg.exe"))).Should().BeTrue("фаза A не удаляет оригиналы");
        File.Exists(context.Legacy.GetFullPath(Path.Combine("bin", "readme.txt"))).Should().BeTrue("фаза A не удаляет оригиналы");
        Directory.Exists(legacyBin).Should().BeTrue();
        context.Migrator.NeedsVerification().Should().BeTrue("скопированный bin обязан дождаться опроса версий");
        context.PathMock.Verify(p => p.EnterLegacyFallbackMode(), Times.Never);
    }

    [TestMethod]
    public void EnsureMigrated_BinCopy_ConflictKeepsBothVersions()
    {
        // Arrange — один файл различается, второй есть только в источнике.
        var context = CreateContext();
        Directory.CreateDirectory(context.Data.GetFullPath("bin"));
        context.Data.CreateFile(Path.Combine("bin", "tool.exe"), "NEW");
        context.Legacy.CreateFile(Path.Combine("bin", "tool.exe"), "OLD");
        context.Legacy.CreateFile(Path.Combine("bin", "only-old.txt"), "legacy-only");

        // Act — фоновая копия никогда не затирает и не удаляет.
        context.Migrator.EnsureMigrated();

        // Assert — конфликт не затирается, исходник на месте.
        File.ReadAllText(context.Data.GetFullPath(Path.Combine("bin", "tool.exe"))).Should().Be("NEW");
        File.Exists(context.Legacy.GetFullPath(Path.Combine("bin", "tool.exe"))).Should().BeTrue("расходящийся исходник оставляем");
    }

    [TestMethod]
    public void EnsureMigrated_SettingsCopyLocked_EngagesFallback()
    {
        // Arrange — исходные настройки заблокированы другим процессом.
        var context = CreateContext();
        string source = context.Legacy.CreateFile("settings.json", "{\"theme\":\"dark\"}");
        using var locker = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // Act
        context.Migrator.EnsureMigrated();

        // Assert — приложение стартует «по-старому», повтор при следующем запуске.
        context.PathMock.Verify(p => p.EnterLegacyFallbackMode(), Times.Once);
        File.Exists(context.Data.GetFullPath("settings.json")).Should().BeFalse();
    }

    [TestMethod]
    public void EnsureMigrated_SecondRun_PreservesRelocationState()
    {
        // Arrange — фаза A уже запустила фоновую копию, квитанция не верифицирована.
        var context = CreateContext();
        context.Legacy.CreateFile(Path.Combine("bin", "tool.exe"), "bytes");
        Directory.CreateDirectory(context.Data.GetFullPath("bin"));
        context.Migrator.EnsureMigrated();
        MigrationTestExtensions.ExistFile(context.Data.GetFullPath(".migration.json"), "первый прогон обязан сохранить квитанцию");

        // Act — повторный запуск до фазы B: состояние копирования не понижается.
        context.Migrator.EnsureMigrated();

        // Assert — проверка всё ещё требуется, исходники на месте.
        File.Exists(context.Legacy.GetFullPath(Path.Combine("bin", "tool.exe"))).Should().BeTrue("фаза A не удаляет оригиналы");
        context.Migrator.NeedsVerification().Should().BeTrue("скопированный bin обязан дождаться опроса версий");
    }

    [TestMethod]
    public void EnsureMigrated_BinHardFailure_RollsBackSettingsCopy()
    {
        // Arrange — назначение bin несоздаваемо (родитель — файл): жёсткий сбой.
        var context = CreateContext();
        string blocker = context.Data.CreateFile("blocker", "not-a-dir");
        context.PathMock.Setup(p => p.GetBinDirectory()).Returns(Path.Combine(blocker, "bin"));
        context.Legacy.CreateFile("settings.json", "{\"theme\":\"dark\"}");
        context.Legacy.CreateFile(Path.Combine("bin", "tool.exe"), "bytes");

        // Act
        context.Migrator.EnsureMigrated();

        // Assert — fallback, свежая копия настроек отозвана, исходники целы.
        context.PathMock.Verify(p => p.EnterLegacyFallbackMode(), Times.Once);
        File.Exists(context.Data.GetFullPath("settings.json")).Should().BeFalse("откат обязан убрать протухающую копию");
        File.Exists(context.Legacy.GetFullPath("settings.json")).Should().BeTrue();
        File.Exists(context.Legacy.GetFullPath(Path.Combine("bin", "tool.exe"))).Should().BeTrue();
        context.Migrator.NeedsVerification().Should().BeFalse("откаченная миграция перепроверяется фазой A, а не B");
    }

    [TestMethod]
    public async Task VerifyAndCleanupAsync_AllGreen_DeletesSources()
    {
        // Arrange — перенесённые настройки и bin, все пробы по новым путям успешны.
        var context = CreateContext();
        context.Legacy.CreateFile("settings.json", "{\"theme\":\"dark\"}");
        context.Legacy.CreateFile(Path.Combine("bin", "sub", "tool.exe"), "bytes");
        context.Legacy.CreateFile(Path.Combine("logs", "old.log"), "stale-log");
        context.Legacy.CreateFile("nvenc-capabilities.json", "{}");
        Directory.CreateDirectory(context.Data.GetFullPath("bin"));
        context.Migrator.EnsureMigrated();
        context.Migrator.NeedsVerification().Should().BeTrue();

        // Фаза A ничего не удаляет: удаление — только после живых проб.
        File.Exists(context.Legacy.GetFullPath("settings.json")).Should().BeTrue();
        File.Exists(context.Legacy.GetFullPath(Path.Combine("bin", "sub", "tool.exe"))).Should().BeTrue();

        string resolvedTool = context.Data.GetFullPath(Path.Combine("bin", "sub", "tool.exe"));
        var probes = new List<BinaryProbeResult>
        {
            new("tool", "sub", "tool.exe", resolvedTool, true, "1.0", true, true, string.Empty),
            BinaryProbeResult.Skipped("eac3to_decoders", "системный компонент")
        };

        // Act — фаза B дожидается копии, опрашивает и только потом чистит.
        await context.Migrator.VerifyAndCleanupAsync(probes, CancellationToken.None);

        // Assert — оригиналы удалены только после живых версий по новым путям.
        File.Exists(resolvedTool).Should().BeTrue("проверенный файл назначения остаётся");
        File.Exists(context.Legacy.GetFullPath("settings.json")).Should().BeFalse();
        File.Exists(context.Data.GetFullPath("settings.json.migrated-backup.bak")).Should().BeFalse("hash-путь не должен создавать бэкапы");
        Directory.Exists(context.Legacy.GetFullPath("bin")).Should().BeFalse();
        File.Exists(context.Legacy.GetFullPath(Path.Combine("logs", "old.log"))).Should().BeFalse("старые логи — отработанная история, новая версия туда не пишет");
        File.Exists(context.Legacy.GetFullPath("nvenc-capabilities.json")).Should().BeFalse("кэш железа пересоздаётся пробой");
        Directory.Exists(context.Legacy.GetFullPath("logs")).Should().BeFalse("пустой каталог логов убирается");
        context.Migrator.NeedsVerification().Should().BeFalse("верифицированная миграция не повторяется");
    }

    [TestMethod]
    public async Task VerifyAndCleanupAsync_ProbeFails_KeepsEverything()
    {
        // Arrange — конфликт: новый путь не опрашивается, старый исходник на месте.
        var context = CreateContext();
        context.Legacy.CreateFile("settings.json", "{\"theme\":\"dark\"}");
        context.Legacy.CreateFile(Path.Combine("bin", "sub", "tool.exe"), "OLD-BYTES");
        context.Data.CreateFile(Path.Combine("bin", "sub", "tool.exe"), "NEW-BYTES");
        Directory.CreateDirectory(context.Data.GetFullPath("bin"));
        context.Migrator.EnsureMigrated();
        context.Migrator.NeedsVerification().Should().BeTrue("конфликт требует повторной проверки");

        string missingPath = context.Data.GetFullPath(Path.Combine("bin", "sub", "tool.exe.absent"));
        var probes = new List<BinaryProbeResult>
        {
            new("tool", "sub", "tool.exe", missingPath, false, string.Empty, true, false, string.Empty)
        };

        // Act
        await context.Migrator.VerifyAndCleanupAsync(probes, CancellationToken.None);

        // Assert — провал проверки: ничего не удалено, повтор при следующем запуске.
        File.Exists(context.Legacy.GetFullPath("settings.json")).Should().BeTrue();
        File.ReadAllText(context.Legacy.GetFullPath(Path.Combine("bin", "sub", "tool.exe"))).Should().Be("OLD-BYTES");
        File.ReadAllText(context.Data.GetFullPath(Path.Combine("bin", "sub", "tool.exe"))).Should().Be("NEW-BYTES");
        context.Migrator.NeedsVerification().Should().BeTrue();
    }

    [TestMethod]
    public async Task VerifyAndCleanupAsync_AbsentEverywhere_Passes()
    {
        // Arrange — инструмент не устанавливали ни там, ни там.
        var context = CreateContext();
        context.Legacy.CreateFile("settings.json", "{\"theme\":\"dark\"}");
        Directory.CreateDirectory(context.Data.GetFullPath("bin"));
        context.Migrator.EnsureMigrated();

        var probes = new List<BinaryProbeResult>
        {
            new("ghost", "sub", "ghost.exe", context.Data.GetFullPath(Path.Combine("bin", "sub", "ghost.exe")), false, string.Empty, true, false, string.Empty)
        };

        // Act
        await context.Migrator.VerifyAndCleanupAsync(probes, CancellationToken.None);

        // Assert — отсутствие везде не считается сбоем, настройки почищены.
        File.Exists(context.Legacy.GetFullPath("settings.json")).Should().BeFalse();
        context.Migrator.NeedsVerification().Should().BeFalse();
    }

    [TestMethod]
    public async Task VerifyAndCleanupAsync_Conflict_KeepsDifferingFile()
    {
        // Arrange — расходящийся файл после слияния.
        var context = CreateContext();
        context.Legacy.CreateFile("settings.json", "{\"theme\":\"dark\"}");
        context.Data.CreateFile(Path.Combine("bin", "tool.exe"), "NEW");
        context.Legacy.CreateFile(Path.Combine("bin", "tool.exe"), "OLD");
        Directory.CreateDirectory(context.Data.GetFullPath("bin"));
        context.Migrator.EnsureMigrated();

        string resolvedTool = context.Data.GetFullPath(Path.Combine("bin", "tool.exe"));
        var probes = new List<BinaryProbeResult>
        {
            new("tool", string.Empty, "tool.exe", resolvedTool, true, "2.0", true, true, string.Empty)
        };

        // Act
        await context.Migrator.VerifyAndCleanupAsync(probes, CancellationToken.None);

        // Assert — отличающийся исходник оставлен, воевать данными запрещено.
        File.ReadAllText(context.Legacy.GetFullPath(Path.Combine("bin", "tool.exe"))).Should().Be("OLD");
        File.ReadAllText(resolvedTool).Should().Be("NEW");
        context.Migrator.NeedsVerification().Should().BeTrue("отложенная чистка повторяется");
    }

    [TestMethod]
    public async Task VerifyAndCleanupAsync_EmptyProbes_KeepsEverything()
    {
        // Arrange — отчёт проб пуст: это отсутствие проверки, а не успех.
        var context = CreateContext();
        context.Legacy.CreateFile("settings.json", "{\"theme\":\"dark\"}");
        context.Legacy.CreateFile(Path.Combine("bin", "sub", "tool.exe"), "bytes");
        Directory.CreateDirectory(context.Data.GetFullPath("bin"));
        context.Migrator.EnsureMigrated();
        context.Migrator.NeedsVerification().Should().BeTrue();

        // Act — пустой список проб обязан запрещать чистку.
        await context.Migrator.VerifyAndCleanupAsync(new List<BinaryProbeResult>(), CancellationToken.None);

        // Assert — ничего не удалено, повтор при следующем запуске.
        File.Exists(context.Legacy.GetFullPath("settings.json")).Should().BeTrue();
        File.Exists(context.Legacy.GetFullPath(Path.Combine("bin", "sub", "tool.exe"))).Should().BeTrue();
        context.Migrator.NeedsVerification().Should().BeTrue();
    }

    [TestMethod]
    public async Task VerifyAndCleanupAsync_StaleSettingsBackup_MovesAsideToBackup()
    {
        // Arrange — протухший бэкап: исходник не менялся с момента переноса, назначение ушло вперёд штатными записями.
        var context = CreateContext();
        string legacySettings = context.Legacy.CreateFile("settings.json", "{\"theme\":\"dark\"}");
        File.SetLastWriteTimeUtc(legacySettings, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        context.Migrator.EnsureMigrated();
        context.Migrator.NeedsVerification().Should().BeTrue();
        // Эмуляция штатных записей приложения в новый файл после миграции.
        File.WriteAllText(context.Data.GetFullPath("settings.json"), "{\"theme\":\"dark\",\"extra\":1}");

        // Act
        await context.Migrator.VerifyAndCleanupAsync(new List<BinaryProbeResult>(), CancellationToken.None);

        // Assert — исходник перемещён в бэкап в каталоге данных, повторная проверка не требуется.
        File.Exists(context.Legacy.GetFullPath("settings.json")).Should().BeFalse();
        string backup = context.Data.GetFullPath("settings.json.migrated-backup.bak");
        File.Exists(backup).Should().BeTrue();
        File.ReadAllText(backup).Should().Be("{\"theme\":\"dark\"}");
        context.Migrator.NeedsVerification().Should().BeFalse();
    }

    [TestMethod]
    public async Task VerifyAndCleanupAsync_RecentlyModifiedSettings_KeepsSource()
    {
        // Arrange — откаченная старая версия писала в исходник после миграции.
        var context = CreateContext();
        string legacySettings = context.Legacy.CreateFile("settings.json", "{\"theme\":\"dark\"}");
        context.Migrator.EnsureMigrated();
        context.Migrator.NeedsVerification().Should().BeTrue();
        // Эмуляция штатных записей в назначение и поздней записи откаченной версии в исходник.
        File.WriteAllText(context.Data.GetFullPath("settings.json"), "{\"theme\":\"dark\",\"extra\":1}");
        File.WriteAllText(legacySettings, "{\"theme\":\"dark\",\"oldkey\":\"oldval\"}");
        File.SetLastWriteTimeUtc(legacySettings, DateTime.UtcNow.AddHours(1));

        // Act
        await context.Migrator.VerifyAndCleanupAsync(new List<BinaryProbeResult>(), CancellationToken.None);

        // Assert — изменённый позже исходник трогать запрещено, проверка повторяется.
        File.Exists(context.Legacy.GetFullPath("settings.json")).Should().BeTrue();
        File.ReadAllText(context.Legacy.GetFullPath("settings.json")).Should().Contain("oldkey");
        File.Exists(context.Data.GetFullPath("settings.json.migrated-backup.bak")).Should().BeFalse();
        context.Migrator.NeedsVerification().Should().BeTrue();
    }

    [TestMethod]
    public async Task NeedsVerification_LeftoverAfterVerifiedClean_RechecksAndKeepsNewer()
    {
        // Arrange — доводим миграцию до чистой квитанции: файл удалён по хэшу.
        var context = CreateContext();
        context.Legacy.CreateFile("settings.json", "{\"theme\":\"dark\"}");
        Directory.CreateDirectory(context.Data.GetFullPath("bin"));
        context.Migrator.EnsureMigrated();
        await context.Migrator.VerifyAndCleanupAsync(new List<BinaryProbeResult>(), CancellationToken.None);
        context.Migrator.NeedsVerification().Should().BeFalse("чистая миграция не повторяется");

        // Эмуляция рассинхрона: файл вернулся с содержимым новее назначения
        // (откаченная версия писала в каталог установки после чистки).
        string legacySettings = context.Legacy.CreateFile("settings.json", "{\"theme\":\"light\"}");
        File.SetLastWriteTimeUtc(legacySettings, DateTime.UtcNow.AddHours(1));

        // Act — квитанция чистая, но наследие на диске: перепроверка обязана сработать.
        context.Migrator.NeedsVerification().Should().BeTrue("рассинхрон квитанции и диска требует перепроверки");
        await context.Migrator.VerifyAndCleanupAsync(new List<BinaryProbeResult>(), CancellationToken.None);

        // Assert — свежий чужой файл трогать запрещено, проверка повторяется.
        File.Exists(context.Legacy.GetFullPath("settings.json")).Should().BeTrue();
        context.Migrator.NeedsVerification().Should().BeTrue("защита от даунгрейда держит pending");
    }

    [TestMethod]
    public async Task NeedsVerification_LeftoverAfterVerifiedClean_ArchivesStale()
    {
        // Arrange — доводим миграцию до чистой квитанции.
        var context = CreateContext();
        context.Legacy.CreateFile("settings.json", "{\"theme\":\"dark\"}");
        Directory.CreateDirectory(context.Data.GetFullPath("bin"));
        context.Migrator.EnsureMigrated();
        await context.Migrator.VerifyAndCleanupAsync(new List<BinaryProbeResult>(), CancellationToken.None);
        context.Migrator.NeedsVerification().Should().BeFalse("чистая миграция не повторяется");

        // Эмуляция рассинхрона: вернулся протухший файл с содержимым,
        // отличающимся от назначения.
        string legacySettings = context.Legacy.CreateFile("settings.json", "{\"theme\":\"stale\"}");
        File.SetLastWriteTimeUtc(legacySettings, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        // Act — перепроверка по факту остатков на диске.
        context.Migrator.NeedsVerification().Should().BeTrue("рассинхрон квитанции и диска требует перепроверки");
        await context.Migrator.VerifyAndCleanupAsync(new List<BinaryProbeResult>(), CancellationToken.None);

        // Assert — протухший файл убран в бэкап, повторная проверка не требуется.
        File.Exists(context.Legacy.GetFullPath("settings.json")).Should().BeFalse();
        File.Exists(context.Data.GetFullPath("settings.json.migrated-backup.bak")).Should().BeTrue();
        context.Migrator.NeedsVerification().Should().BeFalse();
    }

    [TestMethod]
    public void NeedsVerification_NoReceipt_ReturnsFalse()
    {
        // Arrange — миграция не выполнялась.
        var context = CreateContext();

        // Act / Assert
        context.Migrator.NeedsVerification().Should().BeFalse();
    }

    private sealed class MigrationContext
    {
        public TempDirectoryScope Legacy { get; init; } = null!;

        public TempDirectoryScope Data { get; init; } = null!;

        public Mock<IPathManager> PathMock { get; init; } = null!;

        public UserDataMigrator Migrator { get; init; } = null!;
    }

    private MigrationContext CreateContext(bool portable = false)
    {
        var legacy = new TempDirectoryScope();
        var data = new TempDirectoryScope();
        _scopes.Add(legacy);
        _scopes.Add(data);

        var logMock = MockBuilders.CreateLogServiceMock();
        var pathMock = new Mock<IPathManager>();
        pathMock.Setup(p => p.GetBaseDirectory()).Returns(legacy.RootPath);
        pathMock.Setup(p => p.GetSettingsDirectory()).Returns(data.RootPath);
        pathMock.Setup(p => p.GetBinDirectory()).Returns(data.GetFullPath("bin"));
        pathMock.Setup(p => p.IsPortableMode()).Returns(portable);

        return new MigrationContext
        {
            Legacy = legacy,
            Data = data,
            PathMock = pathMock,
            Migrator = new UserDataMigrator(logMock.Object, pathMock.Object)
        };
    }
}

/// <summary>
/// Проверки файловых предусловий.
/// </summary>
internal static class MigrationTestExtensions
{
    internal static void ExistFile(string path, string because)
    {
        File.Exists(path).Should().BeTrue(because);
    }
}
