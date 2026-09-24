// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using KTools_App.Core;
using KTools_App.Infrastructure;
using KTools_App.Models;
using KTools_App.Services.Contracts;
using KTools_App.ViewModels;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests;

/// <summary>
/// Регрессионные тесты-охранники производительности и целостности данных.
/// Задача: не дать будущим изменениям вернуть медленные или разрушающие паттерны,
/// которые уже были устранены (см. PERFORMANCE_REPORT.md).
///
/// Проверки построены на ДЕТЕРМИНИРОВАННЫХ величинах (число событий, число вызовов
/// внешних инструментов, состав коллекций), а не на замерах времени, поэтому
/// не являются флакующими на медленных машинах.
/// </summary>
[TestClass]
public class PerformanceGuardTests
{
    // ------------------------------------------------------------------
    // 1. Троттлинг прогресса: поток stdout не должен превращаться в поток UI-событий
    // ------------------------------------------------------------------

    /// <summary>
    /// 2000 обновлений прогресса от внешней утилиты не должны порождать тысячи
    /// обновлений состояния скрипта (ранее было обновление на каждую строку вывода).
    /// </summary>
    [TestMethod]
    public async Task ProgressFlood_TwoThousandUpdates_ProducesBoundedStateChanges()
    {
        // Arrange
        var logMock = MockBuilders.CreateLogServiceMock();
        var settingsMock = MockBuilders.CreateSettingsManagerMock();
        var pathMock = MockBuilders.CreatePathManagerMock();

        var script = new StubScript(logMock.Object, settingsMock.Object, pathMock.Object)
        {
            ProgressHandler = (callback, index, total) =>
            {
                for (int i = 0; i <= 1000; i++)
                {
                    callback(index, total, $"Обработка {i}", i % 100);
                }
            }
        };

        int stateChanges = 0;
        script.StateChanged += (_, _) => Interlocked.Increment(ref stateChanges);

        var vm = new WorkPanelViewModel(
            MockBuilders.CreateNavigationMock().Object,
            MockBuilders.CreateDialogMock().Object,
            settingsMock.Object,
            logMock.Object,
            MockBuilders.CreateDependencyManagerMock().Object,
            MockBuilders.CreateMediaProbeMock().Object);

        var files = new ObservableCollection<FileQueueItem>
        {
            new FileQueueItem(@"C:\media\movie.mkv")
        };

        vm.Initialize(script, files);

        // Act
        await vm.StartExecutionCommand.ExecuteAsync(new Dictionary<string, object>());

        // Assert: 1001 callback на один файл, но обновлений состояния — единицы
        stateChanges.Should().BeLessThan(60,
            "обновления прогресса должны троттлиться, а не обновляться на каждую строку вывода");
    }

    // ------------------------------------------------------------------
    // 2. Инкрементальный расчёт общего прогресса (защита от «накопления» ошибки)
    // ------------------------------------------------------------------

    /// <summary>
    /// Проверяет корректность инкрементальной суммы прогресса по нескольким файлам:
    /// регрессия к накоплению/сбросу суммы сразу проявится в итоговом значении.
    /// </summary>
    [TestMethod]
    public void UpdateProgressState_IncrementalSum_MatchesExpectedOverallProgress()
    {
        // Arrange
        var script = new StubScript(
            MockBuilders.CreateLogServiceMock().Object,
            MockBuilders.CreateSettingsManagerMock().Object,
            MockBuilders.CreatePathManagerMock().Object);

        var vm = new WorkPanelViewModel(
            MockBuilders.CreateNavigationMock().Object,
            MockBuilders.CreateDialogMock().Object,
            MockBuilders.CreateSettingsManagerMock().Object,
            MockBuilders.CreateLogServiceMock().Object,
            MockBuilders.CreateDependencyManagerMock().Object,
            MockBuilders.CreateMediaProbeMock().Object);

        vm.Initialize(script, new ObservableCollection<FileQueueItem>
        {
            new FileQueueItem(@"C:\media\a.mkv"),
            new FileQueueItem(@"C:\media\b.mkv")
        });

        var method = typeof(WorkPanelViewModel).GetMethod(
            "UpdateProgressState",
            BindingFlags.NonPublic | BindingFlags.Instance);
        method.Should().NotBeNull();

        // Act: два файла по 25% и 75% => средний прогресс очереди 50%
        method!.Invoke(vm, new object[] { 0, 2, "тест", 25.0, 0.0, "", false });
        method.Invoke(vm, new object[] { 1, 2, "тест", 75.0, 0.0, "", false });

        // Assert
        script.SavedGlobalProgress.Should().BeApproximately(50.0, 0.001);

        // Act: обновление значения одного файла не должно «размножать» сумму
        method.Invoke(vm, new object[] { 0, 2, "тест", 40.0, 0.0, "", false });

        // Assert: (40 + 75) / 2 = 57.5
        script.SavedGlobalProgress.Should().BeApproximately(57.5, 0.001);
    }

    // ------------------------------------------------------------------
    // 3. Атомарность записи строк журнала при параллельной обработке
    // ------------------------------------------------------------------

    /// <summary>
    /// Параллельная запись строк в журнал не должна перемешивать строки:
    /// ранее построчная конкатенация давала «хвост одной строки + начало другой».
    /// </summary>
    [TestMethod]
    public async Task AppendLinesToLog_ParallelWriters_KeepsEveryLineIntact()
    {
        // Arrange
        var script = new StubScript(
            MockBuilders.CreateLogServiceMock().Object,
            MockBuilders.CreateSettingsManagerMock().Object,
            MockBuilders.CreatePathManagerMock().Object);

        const int writers = 8;
        const int linesPerWriter = 250;

        // Act
        var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            script.AppendLinesToLog(
                Enumerable.Range(0, linesPerWriter).Select(i => $"writer{w}-line{i}"));
        })).ToArray();

        await Task.WhenAll(tasks);

        // Assert
        string[] lines = script.SavedLogText
            .Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);

        lines.Should().HaveCount(writers * linesPerWriter);
        lines.Should().OnlyHaveUniqueItems();
        lines.Should().Contain($"writer0-line0");
        lines.Should().Contain($"writer{writers - 1}-line{linesPerWriter - 1}");
    }

    // ------------------------------------------------------------------
    // 4. Пакетные операции коллекций: одно уведомление вместо тысяч
    // ------------------------------------------------------------------

    /// <summary>
    /// Усечение списка логов должно выполняться одной операцией с одним уведомлением,
    /// а не удалением элементов по одному (иначе UI получает тысячи событий).
    /// </summary>
    [TestMethod]
    public void AddLogs_Overflow_TrimsWithSingleCollectionNotification()
    {
        // Arrange
        var vm = new LogViewModel(
            MockBuilders.CreateLogServiceMock().Object,
            MockBuilders.CreateSettingsManagerMock().Object,
            MockBuilders.CreatePathManagerMock().Object);

        int notifications = 0;
        vm.Logs.CollectionChanged += (object? s, NotifyCollectionChangedEventArgs e) =>
            notifications++;

        // Act
        vm.AddLogs(Enumerable.Range(0, 2500)
            .Select(i => new LogItem { Message = $"msg_{i}", Level = LogLevel.Info }));

        // Assert
        vm.Logs.Should().HaveCount(2000);
        ((LogItem)vm.Logs[0]).Message.Should().Be("msg_500");
        notifications.Should().BeLessThanOrEqualTo(2,
            "добавление и усечение должны выполняться пакетно (максимум два уведомления)");
    }

    /// <summary>
    /// RemoveRangeFront удаляет элементы с начала коллекции одним уведомлением Reset.
    /// </summary>
    [TestMethod]
    public void RemoveRangeFront_RemovesPrefixWithSingleReset()
    {
        // Arrange
        var collection = new ObservableRangeCollection<int>(Enumerable.Range(0, 10));
        int notifications = 0;
        var actions = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (s, e) =>
        {
            notifications++;
            actions.Add(e.Action);
        };

        // Act
        collection.RemoveRangeFront(3);

        // Assert
        collection.Should().HaveCount(7);
        collection[0].Should().Be(3);
        notifications.Should().Be(1);
        actions.Should().ContainSingle().Which.Should().Be(NotifyCollectionChangedAction.Reset);

        // Проверка граничных случаев: нулевое значение не удаляет ничего,
        // а запрос больше длины коллекции очищает ее полностью
        collection.RemoveRangeFront(0);
        collection.Should().HaveCount(7);
        notifications.Should().Be(1);

        collection.RemoveRangeFront(100);
        collection.Should().BeEmpty();
        notifications.Should().Be(2);
    }

    // ------------------------------------------------------------------
    // 5. Журнал: буферизованная запись не теряет данные
    // ------------------------------------------------------------------

    /// <summary>
    /// Все записи журнала должны попадать в файл: проверяется работа буферизованного
    /// writer'а (раньше запись была синхронной на каждую строку, теперь Flush обязателен).
    /// </summary>
    [TestMethod]
    public void LogService_BufferedWriter_FlushKeepsAllLinesOnDisk()
    {
        using var temp = new TempDirectoryScope();
        var service = new LogService();
        service.InitializeLogFile(temp.RootPath);

        const int lines = 500;
        for (int i = 0; i < lines; i++)
        {
            service.Info($"Запись номер {i}", "GuardTest");
        }

        // До Flush часть данных может быть в буфере — после Flush обязана быть в файле
        service.Flush();

        string content = service.ReadCurrentLog();
        for (int i = 0; i < lines; i++)
        {
            content.Should().Contain($"Запись номер {i}");
        }
    }

    // ------------------------------------------------------------------
    // 6. Кэш анализа медиа: инвалидация при изменении файла
    // ------------------------------------------------------------------

    /// <summary>
    /// Кэш probe-результата обязан инвалидироваться при изменении файла на диске,
    /// иначе после повторного кодирования будут показаны устаревшие дорожки.
    /// </summary>
    [TestMethod]
    public async Task MediaProbeService_Cache_InvalidatedWhenFileChanges()
    {
        using var temp = new TempDirectoryScope();
        // Расширение не .mkv/.mka: для них сервис сначала использует mkvmerge,
        // а тест проверяет кэш именно по вызову ffprobe.
        string filePath = Path.Combine(temp.RootPath, "movie.mp4");
        await File.WriteAllTextAsync(filePath, "first version");

        var ffmpegMock = new Mock<IFFmpegRunner>();
        var calls = 0;
        ffmpegMock.Setup(f => f.GetVideoInfoAsync(filePath))
            .Returns(() =>
            {
                calls++;
                string json = $$"""
                {
                    "format": { "duration": "{{calls * 60}}.0" },
                    "streams": [ { "index": 0, "codec_name": "h264", "codec_type": "video" } ]
                }
                """;
                System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
                return Task.FromResult<System.Text.Json.JsonDocument?>(doc);
            });

        var service = new MediaProbeService(
            MockBuilders.CreateLogServiceMock().Object,
            new Mock<IMkvmergeRunner>().Object,
            ffmpegMock.Object,
            MockBuilders.CreateSettingsManagerMock().Object);

        var first = await service.ProbeAsync(filePath);
        var cached = await service.ProbeAsync(filePath);
        calls.Should().Be(1, "повторный probe того же файла должен браться из кэша");
        cached!.Duration.Should().Be(first!.Duration);

        // Изменяем файл и сдвигаем время модификации
        await File.WriteAllTextAsync(filePath, "second version, longer content");
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddSeconds(5));

        var afterChange = await service.ProbeAsync(filePath);
        calls.Should().Be(2, "изменение файла должно инвалидировать кэш");
        afterChange!.Duration.Should().Be(120.0);
    }

    // ------------------------------------------------------------------
    // 7. Разбор субтитров: BOM не должен попадать в результат
    // ------------------------------------------------------------------

    /// <summary>
    /// Файл субтитров в UTF-8 с BOM не должен приносить символ U+FEFF в заголовок и реплики.
    /// </summary>
    [TestMethod]
    public void AssParser_Utf8WithBom_ProducesTextWithoutBomCharacter()
    {
        using var temp = new TempDirectoryScope();
        string assPath = Path.Combine(temp.RootPath, "subs.ass");

        string content = "[Script Info]\nTitle: Test\n\n[Events]\n" +
            "Format: Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n" +
            "Dialogue: 0:00:01.00,0:00:03.00,Default,,0,0,0,,Привет";

        File.WriteAllText(assPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var parser = new AssParser();
        AssData data = parser.Parse(assPath);

        data.Should().NotBeNull();
        string header = data.Header;
        header.Should().NotContain("\uFEFF");
        data.Dialogues.Should().HaveCount(1);
        data.Dialogues[0].Text.Should().NotContain("\uFEFF");
        data.Dialogues[0].Text.Should().Contain("Привет");
    }

    // ------------------------------------------------------------------
    // 8. Извлечение вложений: аргументы ffmpeg должны давать корректный код возврата
    // ------------------------------------------------------------------

    /// <summary>
    /// Аргументы пакетного извлечения вложений обязаны содержать пустой вывод "-f null -":
    /// без него ffmpeg завершается с кодом 1, и извлечение ошибочно считается неудачным.
    /// </summary>
    [TestMethod]
    public void BuildAttachmentDumpArguments_ContainsNullOutputAndAllAttachments()
    {
        var attachments = new List<(int StreamIndex, string OutputPath)>
        {
            (2, @"C:\temp\fonts\a.ttf"),
            (3, @"C:\temp\fonts\b.ttf")
        };

        var method = typeof(FFmpegRunner).GetMethod(
            "BuildAttachmentDumpArguments",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull("метод формирования аргументов должен существовать");

        var args = (List<string>)method!.Invoke(null, new object[] { @"C:\media\movie.mkv", attachments })!;
        string joined = string.Join(" ", args);

        joined.Should().Contain("-dump_attachment:2");
        joined.Should().Contain("-dump_attachment:3");
        joined.Should().Contain("C:\\temp\\fonts\\a.ttf");
        joined.Should().EndWith("-f null -");
    }

    /// <summary>
    /// Одиночное извлечение вложения также должно завершаться корректным кодом:
    /// проверяем текст команды в исходнике (извлечение выполняется внешним процессом).
    /// </summary>
    [TestMethod]
    public void ExtractAttachmentAsync_UsesNullOutputSoFfmpegReturnsSuccess()
    {
        string source = File.ReadAllText(GetRepositoryFile("KTools.App", "Infrastructure", "FFmpegRunner.cs"));

        int index = source.IndexOf("public async Task<bool> ExtractAttachmentAsync", StringComparison.Ordinal);
        index.Should().BeGreaterThan(0);

        string methodBody = source.Substring(index, Math.Min(1600, source.Length - index));
        methodBody.Should().Contain("-dump_attachment:{streamIndex}");
        methodBody.Should().Contain("-f null -",
            "без пустого выхода ffmpeg возвращает код 1 и извлечение считается неудачным");
    }

    // ------------------------------------------------------------------
    // 9. QAAC: очистка временной папки не должна трогать установленную
    // ------------------------------------------------------------------

    /// <summary>
    /// Очистка временного окружения QAAC обязана удалять только созданные приложением
    /// папки внутри системного Temp. Папка установки (например, в Program Files или
    /// каталоге приложения) не должна удаляться никогда.
    /// </summary>
    [TestMethod]
    public void QaacRunner_CleanupTempDir_DeletesOnlyOwnTempFolders()
    {
        // Каталог установки имитируем в корне репозитория (как bin рядом с приложением):
        // он находится вне системного Temp и не должен удаляться никогда.
        string? repoRoot = TryGetRepositoryRoot();
        if (repoRoot == null)
        {
            Assert.Inconclusive("Корень репозитория недоступен: тест пропускается.");
        }

        string installRoot = Path.Combine(repoRoot!, "qtools_guard_" + Guid.NewGuid().ToString("N"));
        string protectedDir = Path.Combine(installRoot, "KTools_Qaac_installed");
        Directory.CreateDirectory(protectedDir);
        File.WriteAllText(Path.Combine(protectedDir, "qaac64.exe"), "binary");

        // Настоящая временная папка приложения внутри %TEMP% — должна быть удалена
        string ownTempDir = Path.Combine(Path.GetTempPath(), "KTools_Qaac_guard_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ownTempDir);
        File.WriteAllText(Path.Combine(ownTempDir, "qaac64.exe"), "binary");

        try
        {
            var runner = new QaacRunner(
                MockBuilders.CreateLogServiceMock().Object,
                MockBuilders.CreatePathManagerMock().Object);

            var method = typeof(QaacRunner).GetMethod(
                "CleanupTempDir",
                BindingFlags.NonPublic | BindingFlags.Instance);
            method.Should().NotBeNull();

            method!.Invoke(runner, new object[] { protectedDir });
            Directory.Exists(protectedDir).Should().BeTrue(
                "установленная папка QAAC не должна удаляться при очистке временного окружения");
            File.Exists(Path.Combine(protectedDir, "qaac64.exe")).Should().BeTrue();

            method.Invoke(runner, new object[] { ownTempDir });
            Directory.Exists(ownTempDir).Should().BeFalse(
                "собственная временная папка должна удаляться");
        }
        finally
        {
            try { Directory.Delete(installRoot, true); } catch { }
        }
    }

    // ------------------------------------------------------------------
    // 10. Сторожевые проверки исходников: запрет возврата опасных паттернов
    // ------------------------------------------------------------------

    /// <summary>
    /// Запрещает появление блокирующих вызовов в асинхронных слоях обработки медиа:
    /// Thread.Sleep и GetAwaiter().GetResult() ранее приводили к зависаниям интерфейса.
    /// </summary>
    [TestMethod]
    public void SourceGuard_ScriptsAndInfrastructure_ContainNoBlockingWaits()
    {
        var files = GetSourceFiles("KTools.App", "Scripts")
            .Concat(GetSourceFiles("KTools.App", "Infrastructure"))
            .ToList();

        if (files.Count == 0)
        {
            Assert.Inconclusive("Исходники проекта недоступны: тест сканирования пропускается.");
        }

        var offenders = new List<string>();
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            if (text.Contains("Thread.Sleep", StringComparison.Ordinal) ||
                text.Contains("GetAwaiter().GetResult", StringComparison.Ordinal) ||
                text.Contains(".Result;", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        offenders.Should().BeEmpty(
            "в Scripts/Infrastructure нельзя использовать блокирующие ожидания: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// Запрещает возврат посимвольной синхронной записи в сервис логирования:
    /// это главный источник тормозов интерфейса при потоковом логировании.
    /// </summary>
    [TestMethod]
    public void SourceGuard_LogService_DoesNotWriteEachLineSynchronously()
    {
        string source = File.ReadAllText(GetRepositoryFile("KTools.App", "Services", "Implementations", "LogService.cs"));

        source.Should().NotContain("File.AppendAllText(",
            "запись журнала должна идти через буферизованный StreamWriter, а не открывать файл на каждую строку");
    }

    /// <summary>
    /// Проверяет, что троттлинг прогресса в WorkPanelViewModel остается на месте:
    /// без него каждый тик внешней утилиты вызывал полное обновление интерфейса.
    /// </summary>
    [TestMethod]
    public void SourceGuard_WorkPanelViewModel_KeepsProgressThrottle()
    {
        string source = File.ReadAllText(GetRepositoryFile("KTools.App", "ViewModels", "WorkPanelViewModel.cs"));

        source.Should().Contain("ShouldEmitProgress");
        source.Should().Contain("_lastProgressEmit");
    }

    /// <summary>
    /// Проверяет, что панель настроек скрипта строится лениво, а не целиком при каждом
    /// переходе на скрипт (сотни элементов управления на каждую навигацию).
    /// </summary>
    [TestMethod]
    public void SourceGuard_ScriptSettingsControl_KeepsLazyBuild()
    {
        string source = File.ReadAllText(GetRepositoryFile("KTools.App", "UI", "Controls", "ScriptSettingsControl.xaml.cs"));

        source.Should().Contain("EnsureSettingsUI");
        source.Should().Contain("GenerateSettingsUIIfPending");
    }

    // ------------------------------------------------------------------
    // Вспомогательные методы
    // ------------------------------------------------------------------

    private static string? TryGetRepositoryRoot()
    {
        // Ищем корень от каталога сборки и от рабочего каталога: это позволяет
        // запускать тесты и из альтернативного расположения выходных файлов.
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "KTools.sln")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }
        }

        return null;
    }

    private static string GetRepositoryRoot()
    {
        return TryGetRepositoryRoot()
            ?? throw new DirectoryNotFoundException("Не удалось найти корень репозитория с файлом KTools.sln.");
    }

    private static string GetRepositoryFile(params string[] relativeParts)
    {
        string? root = TryGetRepositoryRoot();
        if (root == null)
        {
            Assert.Inconclusive(
                "Корень репозитория недоступен: тест сканирования исходников пропускается.");
        }

        string path = Path.Combine(new[] { root! }.Concat(relativeParts).ToArray());
        if (!File.Exists(path))
        {
            Assert.Inconclusive($"Файл не найден: {path}");
        }

        return path;
    }

    private static IEnumerable<string> GetSourceFiles(params string[] relativeParts)
    {
        string? root = TryGetRepositoryRoot();
        if (root == null)
        {
            return Array.Empty<string>();
        }

        string dir = Path.Combine(new[] { root }.Concat(relativeParts).ToArray());
        if (!Directory.Exists(dir))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFiles(dir, "*.cs", SearchOption.TopDirectoryOnly);
    }
}
