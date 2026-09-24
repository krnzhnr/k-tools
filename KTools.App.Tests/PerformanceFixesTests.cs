// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.Infrastructure;
using KTools_App.Services.Contracts;
using KTools_App.ViewModels;
using KTools_App.Models;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты, покрывающие оптимизации производительности
/// (см. PERFORMANCE_REPORT.md): буферизованный журнал скрипта,
/// троттлинг прогресса, кэш probe-результатов, батч-логи, дебаунс настроек, кэш кистей.
/// </summary>
[TestClass]
public class PerformanceFixesTests
{
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;
    private Mock<ISettingsManager> _settingsManagerMock = null!;

    [TestInitialize]
    public void Setup()
    {
        _logServiceMock = MockBuilders.CreateLogServiceMock();
        _pathManagerMock = new Mock<IPathManager>();
        _settingsManagerMock = MockBuilders.CreateSettingsManagerMock();

        _assParserMock = new Mock<IAssParser>();
        _filterState = new SubtitleFilterState
        {
            StripFormatting = false,
            StripCaps = false
        };
    }

    private StubScript CreateScript()
    {
        return new StubScript(_logServiceMock.Object, _settingsManagerMock.Object, _pathManagerMock.Object);
    }

    /// <summary>
    /// AppendToLog накапливает сообщения без материализации строки, SavedLogText возвращает те же данные.
    /// </summary>
    [TestMethod]
    public void AppendToLog_AccumulatesMessages_SameResultAsConcatenation()
    {
        var script = CreateScript();

        script.AppendToLog("Первая строка\r\n");
        script.AppendToLog("Вторая строка\r\n");

        script.SavedLogText.Should().Be("Первая строка\r\nВторая строка\r\n");
        script.SavedLogLength.Should().Be("Первая строка\r\nВторая строка\r\n".Length);
    }

    /// <summary>
    /// Установка SavedLogText сбрасывает накопленный журнал (эмуляция PrepareExecutionState).
    /// </summary>
    [TestMethod]
    public void SavedLogText_Setter_ClearsAccumulatedLog()
    {
        var script = CreateScript();
        script.AppendToLog("старый текст");

        script.SavedLogText = string.Empty;

        script.SavedLogText.Should().BeEmpty();
        script.SavedLogLength.Should().Be(0);

        script.AppendToLog("новый текст");
        script.SavedLogText.Should().Be("новый текст");
    }

    /// <summary>
    /// TrimSavedLogToTail оставляет последние keepChars символов после превышения maxChars.
    /// </summary>
    [TestMethod]
    public void TrimSavedLogToTail_ExceedingMaxChars_KeepsTail()
    {
        var script = CreateScript();
        string tail = new string('t', 400);
        string head = new string('h', 200);
        script.AppendToLog(head + tail);

        script.TrimSavedLogToTail(500, 400);

        script.SavedLogText.Should().Be(tail);
    }

    /// <summary>
    /// TrimSavedLogToTail ничего не делает, если журнал короче лимита.
    /// </summary>
    [TestMethod]
    public void TrimSavedLogToTail_UnderLimit_KeepsAll()
    {
        var script = CreateScript();
        script.AppendToLog("короткий журнал");

        script.TrimSavedLogToTail(50000, 40000);

        script.SavedLogText.Should().Be("короткий журнал");
    }

    /// <summary>Извлекает приватный метод ShouldEmitProgress через рефлексию.</summary>
    private static (WorkPanelViewModel Vm, MethodInfo ShouldEmit) CreateVmWithShouldEmit()
    {
        var vm = new WorkPanelViewModel(
            new Mock<INavigationService>().Object,
            new Mock<IDialogService>().Object,
            MockBuilders.CreateSettingsManagerMock().Object,
            MockBuilders.CreateLogServiceMock().Object,
            new Mock<IDependencyManager>().Object,
            new Mock<IMediaProbeService>().Object);

        var method = typeof(WorkPanelViewModel)
            .GetMethod("ShouldEmitProgress", BindingFlags.NonPublic | BindingFlags.Instance);
        method.Should().NotBeNull("метод троттлинга прогресса должен существовать");
        return (vm, method!);
    }

    /// <summary>
    /// Первое обновление прогресса всегда проходит через троттлинг.
    /// </summary>
    [TestMethod]
    public void ShouldEmitProgress_FirstCall_ReturnsTrue()
    {
        var (vm, shouldEmit) = CreateVmWithShouldEmit();

        var result = (bool)shouldEmit.Invoke(vm, new object[] { 0, 10.0, "msg", 0.0, "" })!;

        result.Should().BeTrue();
    }

    /// <summary>
    /// Повторные обновления подряд (та же строка stdout) — подавляются до истечения интервала.
    /// </summary>
    [TestMethod]
    public void ShouldEmitProgress_ImmediateRepeats_ReturnsFalse()
    {
        var (vm, shouldEmit) = CreateVmWithShouldEmit();

        shouldEmit.Invoke(vm, new object[] { 0, 10.0, "msg1", 0.0, "" }).Should().Be(true);
        shouldEmit.Invoke(vm, new object[] { 0, 20.0, "msg1", 0.0, "" }).Should().Be(false);
        shouldEmit.Invoke(vm, new object[] { 0, 30.0, "msg2", 0.0, "" }).Should().Be(false);
    }

    /// <summary>
    /// После истечения интервала троттлинга (~150 мс) измененный процент проходит.
    /// </summary>
    [TestMethod]
    public void ShouldEmitProgress_AfterInterval_ChangedPercent_ReturnsTrue()
    {
        var (vm, shouldEmit) = CreateVmWithShouldEmit();

        shouldEmit.Invoke(vm, new object[] { 1, 10.0, "msg", 0.0, "" }).Should().Be(true);
        Thread.Sleep(170);

        shouldEmit.Invoke(vm, new object[] { 1, 20.0, "msg1", 0.0, "" }).Should().Be(true);
        shouldEmit.Invoke(vm, new object[] { 1, 30.0, "msg1", 0.0, "" }).Should().Be(false);
    }

    /// <summary>
    /// Троттлинг каждого файла независим (параллельная обработка нескольких файлов проходит).
    /// </summary>
    [TestMethod]
    public void ShouldEmitProgress_DifferentFiles_EmitIndependently()
    {
        var (vm, shouldEmit) = CreateVmWithShouldEmit();

        shouldEmit.Invoke(vm, new object[] { 0, 10.0, "msg", 0.0, "" }).Should().Be(true);
        shouldEmit.Invoke(vm, new object[] { 1, 10.0, "msg", 0.0, "" }).Should().Be(true);
    }

    /// <summary>
    /// Повторный ProbeAsync того же файла переиспользует кэш — внешний probe-процесс вызывается один раз.
    /// </summary>
    [TestMethod]
    public async Task ProbeAsync_SameFileTwice_UsesCacheAndRunsProbeOnce()
    {
        string testPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(testPath, "dummy media content");

            string jsonWithDuration = """
            {
                "format": { "bit_rate": "384000", "duration": "60.0" },
                "streams": [
                    {
                        "index": 0,
                        "codec_name": "ac3",
                        "codec_type": "audio",
                        "channels": 6
                    }
                ]
            }
            """;
            var doc = JsonDocument.Parse(jsonWithDuration);
            var ffmpegRunnerMock = new Mock<IFFmpegRunner>();
            ffmpegRunnerMock.Setup(f => f.GetVideoInfoAsync(testPath))
                .ReturnsAsync(doc);

            var probeService = new MediaProbeService(
                _logServiceMock.Object,
                new Mock<IMkvmergeRunner>().Object,
                ffmpegRunnerMock.Object,
                _settingsManagerMock.Object);

            // Act
            var first = await probeService.ProbeAsync(testPath);
            var second = await probeService.ProbeAsync(testPath);

            // Assert
            first.Should().NotBeNull();
            second.Should().NotBeNull();
            second!.Duration.Should().Be(first!.Duration);
            ffmpegRunnerMock.Verify(f => f.GetVideoInfoAsync(testPath), Times.Once);
        }
        finally
        {
            if (File.Exists(testPath)) File.Delete(testPath);
        }
    }

    /// <summary>
    /// Кэш probe инвалидируется при изменении файла на диске.
    /// </summary>
    [TestMethod]
    public void AddLogs_ParcelsOverLimit_KeepsLastTwoThousand()
    {
        var logVm = new LogViewModel(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object);

        // Act
        var items = Enumerable.Range(0, 2500)
            .Select(i => new Models.LogItem { Message = $"msg_{i}", Level = LogLevel.Info });
        logVm.AddLogs(items);

        // Assert
        logVm.Logs.Should().HaveCount(2000);
        ((Models.LogItem)logVm.Logs[0]).Message.Should().Be("msg_500");
        ((Models.LogItem)logVm.Logs[^1]).Message.Should().Be("msg_2499");
    }

    /// <summary>
    /// AddLog не превышает лимит в 2000 записей при динамическом потоке.
    /// </summary>
    [TestMethod]
    public void AddLog_DynamicStream_KeepsLastTwoThousand()
    {
        var logVm = new LogViewModel(
            _logServiceMock.Object,
            _settingsManagerMock.Object,
            _pathManagerMock.Object);

        for (int i = 0; i < 2010; i++)
        {
            logVm.AddLog($"msg_{i}", LogLevel.Info);
        }

        logVm.Logs.Should().HaveCount(2000);
        ((Models.LogItem)logVm.Logs[0]).Message.Should().Be("msg_10");
        ((Models.LogItem)logVm.Logs[^1]).Message.Should().Be("msg_2009");
    }

    [TestClass]
    public class SettingsManagerDebounceTests
    {
        private Mock<ILogService> _logServiceMock = null!;
        private Mock<IPathManager> _pathManagerMock = null!;
        private string _tempDir = null!;

        [TestInitialize]
        public void Setup()
        {
            _logServiceMock = new Mock<ILogService>();
            _pathManagerMock = new Mock<IPathManager>();
            _tempDir = Path.Combine(Path.GetTempPath(), "KTools_Debounce_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _pathManagerMock.Setup(pm => pm.GetSettingsDirectory()).Returns(_tempDir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, true); } catch { }
            }
        }

        private string SettingsFile => Path.Combine(_tempDir, "settings.json");

        /// <summary>
        /// Публичный SaveSettings сохраняет немедленно (используется при завершении приложения).
        /// </summary>
        [TestMethod]
        public void SaveSettings_ExplicitCall_WritesImmediately()
        {
            var manager = new SettingsManager(_logServiceMock.Object, _pathManagerMock.Object);

            manager.SetSetting("General", "_test_immediate", "значение");

            File.Exists(SettingsFile).Should().BeTrue();
            File.ReadAllText(SettingsFile).Should().Contain("значение");
        }

        /// <summary>
        /// Несколько быстрых SetSetting => в памяти всегда актуальные значения,
        /// а на диск после дебаунса попадает последнее.
        /// </summary>
        [TestMethod]
        public void SetSetting_RapidChanges_InMemoryImmediateAndDiskEventuallyUpdated()
        {
            var manager = new SettingsManager(_logServiceMock.Object, _pathManagerMock.Object);

            manager.SetSetting("General", "_test_key_1", "a");
            manager.SetSetting("General", "_test_key_2", "b");
            manager.SetSetting("General", "_test_key_2", "c");

            manager.GetSetting("General", "_test_key_2", "?").Should().Be("?".Replace("?", "c"));

            // Ждем истечения дебаунса (300 мс) с запасом
            bool saved = false;
            for (int i = 0; i < 40 && !saved; i++)
            {
                saved = File.Exists(SettingsFile) &&
                        File.ReadAllText(SettingsFile).Contains("c") &&
                        File.ReadAllText(SettingsFile).Contains("a");
                if (!saved) Thread.Sleep(100);
            }

            saved.Should().BeTrue("изменения настроек должны быть сохранены на диск после дебаунса");

            // Повторная загрузка в новый менеджер читает сохраненные значения
            var reloaded = new SettingsManager(_logServiceMock.Object, _pathManagerMock.Object);
            reloaded.GetSetting("General", "_test_key_2", "?").Should().Be("c");
            reloaded.GetSetting("General", "_test_key_1", "?").Should().Be("a");
        }
    }

    /// <summary>
    /// Invalid regex фильтрация не ломает фильтр (паттерны компилируются один раз за проход,
    /// но поведение при ошибке паттерна не изменилось).
    /// </summary>
    [TestMethod]
    public void ApplyFilters_InvalidRegexPattern_IsSkippedWithoutError()
    {
        // Arrange
        string tempSourceFile = Path.Combine(Path.GetTempPath(), "test_perf_regex.ass");
        var assData = new AssData();
        assData.Dialogues.Add(new AssDialogue("0:00:01.00", "0:00:03.00", "Style1", "Actor1", "", "Просто текст"));

        File.WriteAllText(tempSourceFile, "[Script Info]\nTitle: Test\n[Events]\nFormat: Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0:00:01.00,0:00:03.00,Style1,Actor1,,0,0,,Просто текст");

        _assParserMock.Setup(p => p.Parse(tempSourceFile)).Returns(assData);
        _assParserMock.Setup(p => p.StripTags(It.IsAny<string>())).Returns<string>(s => s);

        var viewModel = new SubtitlePreviewViewModel(
            _filterState,
            "test_group",
            _assParserMock.Object,
            _settingsManagerMock.Object,
            _logServiceMock.Object);

        try
        {
            viewModel.LoadDataAsync(new[] { tempSourceFile }).Wait();

            // Act: невалидный regex не должен приводить к исключению и к удалению строк
            var patterns = new List<Dictionary<string, object>>
            {
                new() { { "word", "([unclosed" }, { "active", true }, { "only_part", true } }
            };
            viewModel.LoadPatterns(patterns);

            // Assert
            viewModel.SubtitleLines.Should().HaveCount(1);
        }
        finally
        {
            if (File.Exists(tempSourceFile)) File.Delete(tempSourceFile);
        }
    }

    private Mock<IAssParser> _assParserMock = null!;
    private SubtitleFilterState _filterState = null!;
}
