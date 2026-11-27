// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KTools_App.Tests.Core;

/// <summary>
/// Регрессионные тесты issue #86: защита от перезаписи внеочередных файлов,
/// безопасный fallback в catch и санитизация шаблонов PowerRename.
/// Все комментарии выполнены на русском языке.
/// </summary>
[TestClass]
public class GetSafeOutputPathIssue86Tests
{
    /// <summary>Тестовый скрипт, открывающий защищённый метод.</summary>
    private sealed class TestableScript : AbstractScript
    {
        public TestableScript(ILogService log, ISettingsManager settings, IPathManager paths)
            : base(log, settings, paths)
        {
        }

        public override string Name => "Тестовый скрипт issue86";
        public override string Description => "Проверка безопасного пути";
        public override string Category => "Тесты";
        public override string IconName => "Test";
        public override string[] FileExtensions => new[] { ".mkv" };

        public override Task<KTools_App.Models.ExecutionResult> ExecuteSingleAsync(
            string filePath,
            Dictionary<string, object> settings,
            string? outputPath,
            ScriptProgressCallback progressCallback,
            int fileIndex,
            int totalCount,
            KTools_App.Models.ExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(KTools_App.Models.ExecutionResult.Succeeded(context, "Готово"));
        }

        public string CallGetSafeOutputPath(string input, string output, Dictionary<string, object>? settings = null)
        {
            return GetSafeOutputPath(input, output, settings);
        }
    }

    private static TestableScript CreateScript(out Moq.Mock<ISettingsManager> settingsMock)
    {
        settingsMock = MockBuilders.CreateSettingsManagerMock();
        return new TestableScript(
            MockBuilders.CreateLogServiceMock().Object,
            settingsMock.Object,
            MockBuilders.CreatePathManagerMock().Object);
    }

    private static Dictionary<string, object> LocalSettings(string search, string replace)
    {
        return new Dictionary<string, object>
        {
            { "LocalRenameOverride", true },
            { "LocalRenameSearch", search },
            { "LocalRenameReplace", replace },
            { "LocalRenameUseRegex", true },
            { "LocalRenameCaseSensitive", false }
        };
    }

    /// <summary>
    /// PowerRename, указывающий на существующий внеочередной файл, обязан дать суффикс, а не перезапись.
    /// </summary>
    [TestMethod]
    public void GetSafeOutputPath_RenameToExistingDiskFile_AppendsNumericSuffix()
    {
        using var tempDir = new TempDirectoryScope();
        string input = tempDir.CreateFile("a.mkv", "data");
        string victim = tempDir.CreateFile("b.mkv", "чужое содержимое");
        var script = CreateScript(out var settingsMock);
        settingsMock.Object.OverwriteExisting = true;
        script.PrepareBatch(new[] { input });
        var settings = LocalSettings("a", "b");

        string result = script.CallGetSafeOutputPath(input, input, settings);

        result.Should().NotBe(victim, "существующий внеочередной файл нельзя молча перезаписывать");
        File.Exists(result).Should().BeFalse("результирующий путь обязан указывать на несуществующий файл");
        Path.GetDirectoryName(Path.GetFullPath(result)).Should().Be(tempDir.RootPath);
        Path.GetFileNameWithoutExtension(result).Should().StartWith("b_");
    }

    /// <summary>
    /// Шаблон PowerRename с разделителями и точками не должен оставлять их в имени.
    /// </summary>
    [TestMethod]
    [DataRow("../evil")]
    [DataRow("..\\evil")]
    [DataRow("C:\\evil")]
    [DataRow("/abs/evil")]
    [DataRow("sub/dir")]
    public void ApplyPowerRename_TraversalReplacement_RemovesSeparatorsAndDotDot(string replacement)
    {
        var script = CreateScript(out _);
        var settings = LocalSettings(".*", replacement);

        string result = script.ApplyPowerRename("video", 1, settings);

        result.Should().NotContain("/").And.NotContain("\\").And.NotContain(":");
        result.Should().NotContain("..");
        Path.IsPathRooted(result).Should().BeFalse("результат обязан быть чистым именем файла");
    }

    /// <summary>
    /// Итоговый путь после traversal-шаблона обязан остаться внутри целевой папки.
    /// </summary>
    [TestMethod]
    [DataRow("../evil")]
    [DataRow("..\\..\\evil")]
    [DataRow("C:\\evil")]
    public void GetSafeOutputPath_TraversalReplacement_StaysInsideTargetDirectory(string replacement)
    {
        using var tempDir = new TempDirectoryScope();
        string input = tempDir.CreateFile("video.mkv", "data");
        string output = tempDir.GetFullPath("video.mkv");
        var script = CreateScript(out _);
        script.PrepareBatch(new[] { input });
        var settings = LocalSettings(".*", replacement);

        string result = script.CallGetSafeOutputPath(input, output, settings);

        string full = Path.GetFullPath(result);
        full.Should().StartWith(tempDir.RootPath + Path.DirectorySeparatorChar, "запись не должна уходить из целевой папки");
        Path.GetFileName(full).Should().NotContain("..");
    }

    /// <summary>
    /// Исключение внутри не должно возвращать голый входной путь: ожидается fallback с суффиксом.
    /// </summary>
    [TestMethod]
    public void GetSafeOutputPath_NullOutputPath_ReturnsProcessedFallback()
    {
        using var tempDir = new TempDirectoryScope();
        string input = tempDir.CreateFile("movie.mkv", "data");
        var script = CreateScript(out _);

        string result = script.CallGetSafeOutputPath(input, null!, null);

        result.Should().NotBeNull("fallback обязан вернуть реальный путь, а не голый вход");
        result.Should().NotBe(input, "возврат входного пути привёл бы к записи поверх исходника");
        result.Should().EndWith("_processed.mkv");
        Path.GetDirectoryName(Path.GetFullPath(result)).Should().Be(tempDir.RootPath);
    }
}
