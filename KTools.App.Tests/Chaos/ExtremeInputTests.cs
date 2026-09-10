// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using KTools_App;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests.Chaos;

/// <summary>
/// Наследник AbstractScript, открывающий защищённые методы
/// GetSafeOutputPath и GetSettingValue для прямого тестирования.
/// </summary>
public sealed class ExposedScript : AbstractScript
{
    /// <summary>Инициализирует тестовый скрипт с открытыми защищёнными методами.</summary>
    public ExposedScript(
        ILogService logService,
        ISettingsManager settingsManager,
        IPathManager pathManager)
        : base(logService, settingsManager, pathManager)
    {
    }

    /// <summary>Отображаемое имя скрипта.</summary>
    public override string Name => "Раскрытый скрипт";

    /// <summary>Описание скрипта.</summary>
    public override string Description => "Тестирование защищённых методов";

    /// <summary>Категория скрипта.</summary>
    public override string Category => "Хаос";

    /// <summary>Имя иконки.</summary>
    public override string IconName => "Test";

    /// <summary>Поддерживаемые расширения.</summary>
    public override string[] FileExtensions => new[] { ".mkv" };

    /// <summary>
    /// Публичный доступ к защищённому GetSafeOutputPath.
    /// </summary>
    public string CallGetSafeOutputPath(
        string inputPath,
        string outputPath,
        Dictionary<string, object>? settings = null)
    {
        return GetSafeOutputPath(inputPath, outputPath, settings);
    }

    /// <summary>
    /// Публичный доступ к защищённому GetSettingValue.
    /// </summary>
    public T CallGetSettingValue<T>(
        Dictionary<string, object> settings,
        string key,
        T defaultValue)
    {
        return GetSettingValue(settings, key, defaultValue);
    }

    /// <summary>
    /// Публичный доступ к защищённому DeleteSourceAsync.
    /// </summary>
    public Task CallDeleteSourceAsync(string filePath, List<string> results)
    {
        return DeleteSourceAsync(filePath, results);
    }

    /// <summary>
    /// Публичный доступ к защищённому ReplaceSourceWithResultAsync.
    /// </summary>
    public Task<bool> CallReplaceSourceWithResultAsync(
        string sourcePath,
        string resultPath,
        List<string> results)
    {
        return ReplaceSourceWithResultAsync(sourcePath, resultPath, results);
    }

    /// <summary>Заглушка асинхронного выполнения.</summary>
    public override Task<List<string>> ExecuteSingleAsync(
        string filePath,
        Dictionary<string, object> settings,
        string? outputPath,
        ScriptProgressCallback progressCallback,
        int fileIndex,
        int totalCount)
    {
        return Task.FromResult(new List<string>());
    }
}

/// <summary>
/// Хаос-тесты экстремальных пользовательских вводов: сверхдлинные строки,
/// эмодзи, резервные имена устройств Windows, битые регулярные выражения,
/// null/пустые значения, числовые переполнения.
/// </summary>
[TestClass]
public sealed class ExtremeInputTests
{
    /// <summary>
    /// Хаос: FileQueueItem с экстремальными путями не должен бросать
    /// необработанных исключений в конструкторе.
    /// </summary>
    [TestMethod]
    [DataRow("C:\\videos\\фильм с пробелами и кириллицей.mkv")]
    [DataRow("C:\\videos\\🎬🌕🎥 emoji movie name.mkv")]
    [DataRow("COM1")]
    [DataRow("NUL")]
    [DataRow("con\\in.mkv")]
    [DataRow("\\\\localhost\\c$\\share\\file.mkv")]
    [DataRow("   ")]
    [DataRow("")]
    [DataRow("C:\\aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.mkv")]
    public void FileQueueItem_ExtremePaths_DoesNotThrow(string path)
    {
        // Arrange / Act
        var item = new FileQueueItem(path);

        // Assert
        item.FilePath.Should().Be(path);
        item.FileName.Should().NotBeNull();
        item.FileSizeStr.Should().NotBeNullOrEmpty(
            "размер файла всегда должен иметь отображаемое значение");
    }

    /// <summary>
    /// Хаос: null-путь в конструкторе FileQueueItem. Path.GetFileName(null)
    /// по контракту BCL возвращает null — конструктор проходит без исключения,
    /// FileName остаётся null. Фиксируем поведение: отображаемые свойства
    /// не защищены от null-пути.
    /// </summary>
    [TestMethod]
    public void FileQueueItem_NullPath_Characterization()
    {
        // Arrange / Act
        var item = new FileQueueItem(null!);

        // Assert
        item.FileName.Should().BeNull(
            "Path.GetFileName(null) возвращает null — конструктор не бросает, имя файла остаётся null");
    }

    /// <summary>
    /// Хаос: сверхдлинная командная строка (10 тыс. символов) корректно
    /// разделяется SplitCommandLine.
    /// </summary>
    [TestMethod]
    public void SplitCommandLine_10kCharacterArgument_Parsed()
    {
        // Arrange
        string longArg = "\"" + new string('x', 10_000) + "\"";
        string commandLine = $"--script test {longArg}";

        // Act
        string[] args = App.SplitCommandLine(commandLine);

        // Assert
        args.Should().HaveCount(3);
        args[0].Should().Be("--script");
        args[1].Should().Be("test");
        args[2].Should().HaveLength(10_000);
    }

    /// <summary>
    /// Хаос: пустые пары кавычек в SplitCommandLine схлопываются
    /// (кавычки не являются частью содержимого токена).
    /// </summary>
    [TestMethod]
    public void SplitCommandLine_EmptyQuotes_TokenDropped()
    {
        // Arrange
        string commandLine = "a \"\" b";

        // Act
        string[] args = App.SplitCommandLine(commandLine);

        // Assert — пустой токен между кавычками отбрасывается
        args.Should().BeEquivalentTo(new[] { "a", "b" },
            "пустая пара кавычек не создаёт пустой токен");
    }

    /// <summary>
    /// Хаос: последовательность из многих кавычек инвертирует состояние
    /// inQuotes чётное/нечётное число раз — парсер должен обрабатывать
    /// без исключений и зависаний.
    /// </summary>
    [TestMethod]
    public void SplitCommandLine_ManyQuotes_NoException()
    {
        // Arrange
        string commandLine = new string('"', 50) + " middle " + new string('"', 51);

        // Act
        string[] args = App.SplitCommandLine(commandLine);

        // Assert
        args.Should().NotBeNull();
        args.Should().HaveCount(1,
            "при нечётном общем количестве кавычек всё содержимое до конца строки образует один токен");
    }

    /// <summary>
    /// Хаос: ParseCommandLineArray с точками и пустыми сегментами путей.
    /// "." и ".." — существующие директории — принимаются как файл-аргументы;
    /// пустые строки отфильтровываются.
    /// </summary>
    [TestMethod]
    public void ParseCommandLineArray_DotsAndEmptySegments_FilteredCorrectly()
    {
        // Arrange / Act
        var (script, files) = App.ParseCommandLineArray(
            new[] { "--script", string.Empty, ".", "..", string.Empty });

        // Assert
        script.Should().BeEmpty("пустое значение после --script не задаёт имя скрипта");
        files.Should().Contain(".", "текущая директория существует и принимается как аргумент");
        files.Should().Contain("..", "родительская директория существует и принимается как аргумент");
        files.Should().NotContain(string.Empty, "пустые строки должны отфильтровываться");
    }

    /// <summary>
    /// Хаос: VersionComparer с экстремальными строками версий —
    /// сверхдлинные числа, битые форматы, null.
    /// </summary>
    [TestMethod]
    public void CompareVersions_ExtremeInputs_Characterization()
    {
        // Arrange / Act / Assert
        // Сверхдлинное число не парсится в int и молча трактуется как 0:
        // гигантская версия считается МЕНЬШЕ малой — скрытая деградация,
        // задокументирована как находка аудита (VersionComparer.cs:57).
        VersionComparer.CompareVersions("999999999999999999", "1").Should().Be(-1);
        VersionComparer.CompareVersions("v", "v").Should().Be(0);
        VersionComparer.CompareVersions("1.", ".1").Should().Be(1,
            "сегмент '1' больше пустого сегмента — битые форматы сравниваются по числовым частям");
        VersionComparer.CompareVersions("1.2.3.4.5.6.7", "1.2.3.4.5.6.7").Should().Be(0);
        VersionComparer.CompareVersions("abc1.2.3xyz", "abc1.2.3xyz").Should().Be(0);

        // null-безопасность
        VersionComparer.CompareVersions(null!, null!).Should().Be(0);
        VersionComparer.CompareVersions(null!, "1.0").Should().Be(-1);
        VersionComparer.CompareVersions("1.0", null!).Should().Be(1);
        VersionComparer.CompareVersions(string.Empty, string.Empty).Should().Be(0);
    }

    /// <summary>
    /// Хаос: GetSafeOutputPath с битым регулярным выражением в настройках
    /// переименования не должен приводить к исключению — возвращается
    /// путь без применения переименования.
    /// </summary>
    [TestMethod]
    public void GetSafeOutputPath_BrokenRegexPattern_FallsBackToUnrenamedPath()
    {
        // Arrange
        using var tempDir = new TempDirectoryScope();
        string input = tempDir.CreateFile("source.mkv", "data");
        string output = tempDir.GetFullPath("result.mkv");
        var logMock = MockBuilders.CreateLogServiceMock();
        var settingsMock = MockBuilders.CreateSettingsManagerMock();
        settingsMock.Object.RenameEnableRegex = true;
        settingsMock.Object.RenameUseRegex = true;
        settingsMock.Object.RenameRegexSearch = "[";
        settingsMock.Object.RenameRegexReplace = "x";
        var script = new ExposedScript(
            logMock.Object, settingsMock.Object, MockBuilders.CreatePathManagerMock().Object);

        // Act
        string result = script.CallGetSafeOutputPath(input, output);

        // Assert — путь возвращён без исключения (битый regex пойман catch)
        result.Should().NotBeNullOrEmpty();
        result.Should().EndWith(".mkv");
        logMock.Verify(l => l.Exception(
            It.IsAny<Exception>(),
            It.IsAny<string>(),
            It.IsAny<string>()), Times.AtLeastOnce,
            "ошибка битого regex должна журналироваться");
    }

    /// <summary>
    /// Хаос: GetSafeOutputPath с пустым входным путём возвращает
    /// выходной путь как есть (catch-путь).
    /// </summary>
    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void GetSafeOutputPath_EmptyInputPath_ReturnsOutputAsIs(string inputPath)
    {
        // Arrange
        var logMock = MockBuilders.CreateLogServiceMock();
        var settingsMock = MockBuilders.CreateSettingsManagerMock();
        var script = new ExposedScript(
            logMock.Object, settingsMock.Object, MockBuilders.CreatePathManagerMock().Object);

        // Act
        string result = script.CallGetSafeOutputPath(inputPath, "C:\\out\\result.mkv");

        // Assert
        result.Should().Be("C:\\out\\result.mkv",
            "Path.GetFullPath(пусто) бросает — catch возвращает исходный выходной путь");
    }

    /// <summary>
    /// Хаос: защита исходного файла — совпадающие вход и выход
    /// получают суффикс _processed.
    /// </summary>
    [TestMethod]
    public void GetSafeOutputPath_OutputEqualsInput_AppendsProcessedSuffix()
    {
        // Arrange
        using var tempDir = new TempDirectoryScope();
        string file = tempDir.CreateFile("movie.mkv", "data");
        var script = new ExposedScript(
            MockBuilders.CreateLogServiceMock().Object,
            MockBuilders.CreateSettingsManagerMock().Object,
            MockBuilders.CreatePathManagerMock().Object);

        // Act
        string result = script.CallGetSafeOutputPath(file, file);

        // Assert
        result.Should().EndWith("_processed.mkv",
            "выход, совпадающий с исходником, обязан получить суффикс защиты");
        result.Should().NotBe(file, "исходный файл не должен быть перезаписан");
    }

    /// <summary>
    /// Хаос: коллизия имён в пакете — два одинаковых выхода получают
    /// уникальные числовые суффиксы.
    /// </summary>
    [TestMethod]
    public void GetSafeOutputPath_BatchCollision_AppendsNumericSuffixes()
    {
        // Arrange
        using var tempDir = new TempDirectoryScope();
        string first = tempDir.CreateFile("one.mkv", "data");
        string second = tempDir.CreateFile("two.mkv", "data");
        string sameOutput = tempDir.GetFullPath("same_name.mkv");
        var script = new ExposedScript(
            MockBuilders.CreateLogServiceMock().Object,
            MockBuilders.CreateSettingsManagerMock().Object,
            MockBuilders.CreatePathManagerMock().Object);
        script.PrepareBatch(new[] { first, second });

        // Act
        string out1 = script.CallGetSafeOutputPath(first, sameOutput);
        string out2 = script.CallGetSafeOutputPath(second, sameOutput);

        // Assert
        out1.Should().NotBe(out2,
            "два файла с одинаковым выходом не могут перезаписывать друг друга");
        Path.GetFileName(out2).Should().StartWith("same_name_",
            "коллизия в пакете разрешается числовым суффиксом");
    }

    /// <summary>
    /// Хаос: эмодзи и кириллица в именах файлов проходят через
    /// GetSafeOutputPath без искажений.
    /// </summary>
    [TestMethod]
    public void GetSafeOutputPath_EmojiAndCyrillic_Preserved()
    {
        // Arrange
        using var tempDir = new TempDirectoryScope();
        string input = tempDir.CreateFile("🎬 РОЛИК №1.mkv", "data");
        string output = tempDir.GetFullPath("Результат 🎞️.mkv");
        var script = new ExposedScript(
            MockBuilders.CreateLogServiceMock().Object,
            MockBuilders.CreateSettingsManagerMock().Object,
            MockBuilders.CreatePathManagerMock().Object);

        // Act
        string result = script.CallGetSafeOutputPath(input, output);

        // Assert
        Path.GetFileName(result).Should().Contain("🎞️",
            "эмодзи в имени выходного файла не должны искажаться");
    }

    /// <summary>
    /// Хаос: автоматическая подпапка результатов создаётся на диске,
    /// когда включена настройка UseAutoSubfolder.
    /// </summary>
    [TestMethod]
    public void GetSafeOutputPath_AutoSubfolderEnabled_CreatesSubfolderOnDisk()
    {
        // Arrange
        using var tempDir = new TempDirectoryScope();
        string input = tempDir.CreateFile("source.mkv", "data");
        string output = tempDir.GetFullPath("source.mkv"); // та же папка, что и исходник
        var settingsMock = MockBuilders.CreateSettingsManagerMock();
        settingsMock.Object.UseAutoSubfolder = true;
        settingsMock.Object.DefaultOutputSubfolder = "Out";
        var script = new ExposedScript(
            MockBuilders.CreateLogServiceMock().Object,
            settingsMock.Object,
            MockBuilders.CreatePathManagerMock().Object);

        // Act
        string result = script.CallGetSafeOutputPath(input, output);

        // Assert
        result.Should().Contain("Out",
            "выход должен быть перенаправлен в подпапку результатов");
        string expectedDir = Path.Combine(tempDir.RootPath, "Out");
        Directory.Exists(expectedDir).Should().BeTrue(
            "подпапка результатов обязана создаваться на диске автоматически");
    }

    /// <summary>
    /// Хаос: переименование с переменными PowerRename —
    /// нумерация с паддингом ${num:3} и подстановка года ${YYYY}.
    /// </summary>
    [TestMethod]
    public void GetSafeOutputPath_RegexWithNumAndYearVariables_Applied()
    {
        // Arrange
        using var tempDir = new TempDirectoryScope();
        string input = tempDir.CreateFile("Episode 05 [1080p].mkv", "data");
        string output = tempDir.GetFullPath("Episode 05 [1080p].mkv");
        var settingsMock = MockBuilders.CreateSettingsManagerMock();
        settingsMock.Object.RenameEnableRegex = true;
        settingsMock.Object.RenameUseRegex = true;
        settingsMock.Object.RenameRegexSearch = @"Episode (\d+)";
        settingsMock.Object.RenameRegexReplace = "Серия ${num:3} ${YYYY}";
        var script = new ExposedScript(
            MockBuilders.CreateLogServiceMock().Object,
            settingsMock.Object,
            MockBuilders.CreatePathManagerMock().Object);
        script.PrepareBatch(new[] { input });

        // Act
        string result = script.CallGetSafeOutputPath(input, output);

        // Assert
        Path.GetFileNameWithoutExtension(result).Should().StartWith(
            $"Серия 001 {DateTime.Now:yyyy}",
            "нумерация с паддингом и год должны подставляться в замену");
    }

    /// <summary>
    /// Хаос: GetSettingValue с JsonElement-значениями всех родов
    /// конвертируется корректно, невалидные — возвращают default.
    /// </summary>
    [TestMethod]
    public void GetSettingValue_JsonElementAndBoxingVariants_ConvertedOrDefault()
    {
        // Arrange
        var script = new ExposedScript(
            MockBuilders.CreateLogServiceMock().Object,
            MockBuilders.CreateSettingsManagerMock().Object,
            MockBuilders.CreatePathManagerMock().Object);

        // JsonElement bool
        var boolSettings = new Dictionary<string, object>
        {
            ["k"] = System.Text.Json.JsonSerializer.SerializeToElement(true),
        };
        script.CallGetSettingValue(boolSettings, "k", false).Should().BeTrue(
            "JsonElement true обязан конвертироваться в bool");

        // JsonElement число
        var intSettings = new Dictionary<string, object>
        {
            ["k"] = System.Text.Json.JsonSerializer.SerializeToElement(42),
        };
        script.CallGetSettingValue(intSettings, "k", 0).Should().Be(42,
            "JsonElement число обязан конвертироваться в int");

        // JsonElement строка
        var strSettings = new Dictionary<string, object>
        {
            ["k"] = System.Text.Json.JsonSerializer.SerializeToElement("значение"),
        };
        script.CallGetSettingValue(strSettings, "k", string.Empty).Should().Be("значение");

        // object-boxing int → string через Convert.ChangeType
        var boxedSettings = new Dictionary<string, object> { ["k"] = 7 };
        script.CallGetSettingValue(boxedSettings, "k", "0").Should().Be("7",
            "число в object-boxing должно конвертироваться в string");

        // невалидная конверсия — default
        var invalidSettings = new Dictionary<string, object> { ["k"] = new object() };
        script.CallGetSettingValue(invalidSettings, "k", "fallback").Should().Be("fallback",
            "невалидное значение обязано возвращать default без исключения");

        // отсутствующий ключ — default
        script.CallGetSettingValue(new Dictionary<string, object>(), "missing", "fallback")
            .Should().Be("fallback");

        // null-словарь — default (проверка null-гуардии)
        script.CallGetSettingValue(null!, "k", "fallback").Should().Be("fallback");
    }
}
