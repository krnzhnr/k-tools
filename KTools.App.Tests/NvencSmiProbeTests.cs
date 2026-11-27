// -*- coding: utf-8 -*-
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using KTools_App.Infrastructure;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests;

/// <summary>
/// Регрессия issue #112: опрос nvidia-smi в CheckNvencSupportAsync висел вечно,
/// если утилита не завершалась (нет таймаута) или заваливала stderr
/// (неперенаправленный буфер блокировал дочерний процесс).
/// </summary>
[TestClass]
public sealed class NvencSmiProbeTests
{
    private FFmpegRunner _runner = null!;
    private string _powerShellPath = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _runner = new FFmpegRunner(
            MockBuilders.CreateLogServiceMock().Object,
            MockBuilders.CreatePathManagerMock().Object);
        _powerShellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
    }

    [TestMethod]
    public async Task ProbeNvidiaSmi_HangingBinary_ReturnsFalseWithinTimeout()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(_powerShellPath))
        {
            Assert.Inconclusive("Тест требует Windows с powershell.exe для эмуляции зависшего бинарника.");
        }

        // Arrange: бинарник молчит 60 секунд — дольше любого разумного детекта железа.
        Task<bool> probeTask = _runner.ProbeNvidiaSmiAsync(
            _powerShellPath,
            "-NoProfile -NonInteractive -Command \"Start-Sleep 60\"",
            TimeSpan.FromSeconds(5));

        // Act: ждём с запасом поверх таймаута опроса, чтобы зависший код гарантированно уронить.
        var stopwatch = Stopwatch.StartNew();
        Task finished = await Task.WhenAny(probeTask, Task.Delay(TimeSpan.FromSeconds(25))).ConfigureAwait(false);
        stopwatch.Stop();

        // Assert: опрос завершился сам (таймаут + Kill), а не повис вместе с утилитой.
        Assert.AreEqual(probeTask, finished, "Опрос зависшего бинарника обязан завершиться по таймауту, а не висеть вечно.");
        bool hasNvidia = await probeTask.ConfigureAwait(false);
        hasNvidia.Should().BeFalse("зависшая утилита не подтверждает наличие видеокарты");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(25), "таймаут опроса — 5 секунд с запасом на нагрузку машины");
    }

    [TestMethod]
    public async Task ProbeNvidiaSmi_StderrFloodingBinary_DoesNotDeadlockAndDetectsGpu()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(_powerShellPath))
        {
            Assert.Inconclusive("Тест требует Windows с powershell.exe для эмуляции шумного бинарника.");
        }

        // Arrange: ~3 МБ в stderr (переполнение нечитаемого буфера) + метка GPU в stdout, код 0.
        string arguments = "-NoProfile -NonInteractive -Command \"$i = 0; "
            + "while ($i -lt 100000) { [Console]::Error.WriteLine('01234567890123456789012345678901'); $i++ }; "
            + "[Console]::Out.WriteLine('GPU 0: Test Adapter'); exit 0\"";
        Task<bool> probeTask = _runner.ProbeNvidiaSmiAsync(
            _powerShellPath,
            arguments,
            TimeSpan.FromSeconds(30));

        // Act.
        Task finished = await Task.WhenAny(probeTask, Task.Delay(TimeSpan.FromSeconds(45))).ConfigureAwait(false);

        // Assert: оба потока drained — дедлока нет, GPU распознана.
        Assert.AreEqual(probeTask, finished, "Опрос шумного бинарника обязан завершиться: stderr должен читаться параллельно с stdout.");
        bool hasNvidia = await probeTask.ConfigureAwait(false);
        hasNvidia.Should().BeTrue("корректный вывод с меткой GPU обязан распознаваться");
    }
}
