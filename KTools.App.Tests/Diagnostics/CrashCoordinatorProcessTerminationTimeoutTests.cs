// -*- coding: utf-8 -*-
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Implementations;
using KTools_App.Tests.TestHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KTools_App.Tests.Diagnostics;

// Проверки issue #114: стадия process_termination не должна резаться
// общим 2-секундным таймаутом (2-3 висящих ffmpeg требуют 6-9 с).
[TestClass]
public sealed class CrashCoordinatorProcessTerminationTimeoutTests
{
    [TestMethod]
    public async Task RunControlledShutdownStagesAsync_SlowProcessTerminationStage_CompletesWithoutCancellation()
    {
        using var scope = new TempDirectoryScope();
        using var coordinator = new CrashCoordinator(CreateSink(scope), new RecordingLogService());

        CrashShutdownResult result = await coordinator.RunControlledShutdownStagesAsync(
            "window.closed",
            stopOperations: _ => Task.FromResult(true),
            stopWatcher: _ => Task.FromResult(true),
            terminateProcesses: async _ =>
            {
                // Стаб долгой остановки: 5 с дольше общего лимита 2 с,
                // но внутри отдельного бюджета стадии завершения процессов.
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                return new ProcessTerminationSummary(2, 2, 0, 0);
            },
            flush: _ => Task.FromResult(true),
            disposeServices: () => Task.FromResult(true),
            requestExit: () => Task.FromResult(true),
            cancellationToken: CancellationToken.None,
            persistSettings: _ => Task.FromResult(SettingsPersistenceResult.Success),
            writeTerminalEvent: null);

        result.ProcessesVerified.Should().BeTrue("пятисекундная остановка процессов укладывается в отдельный бюджет стадии");
        result.ProcessSummary.Should().NotBeNull();
        result.ProcessSummary!.AllVerified.Should().BeTrue();
        result.ProcessSummary.ErrorCode.Should().BeNull("успешное завершение не несёт ErrorCode");
        result.Succeeded.Should().BeTrue();
    }

    private static EmergencyLogSink CreateSink(TempDirectoryScope scope)
    {
        LogServiceOptions options = new()
        {
            EmergencyDirectory = Path.Combine(scope.RootPath, "emergency"),
            MaxEmergencyFileBytes = 4096
        };
        Directory.CreateDirectory(options.EmergencyDirectory);
        return new EmergencyLogSink(options.EmergencyDirectory, options);
    }
}
