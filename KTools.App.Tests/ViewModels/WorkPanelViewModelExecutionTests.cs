// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;
using KTools_App.ViewModels;
using CommunityToolkit.Mvvm.Messaging;

namespace KTools_App.Tests.ViewModels;

/// <summary>
/// Глубокие юнит-тесты выполнения скриптов в WorkPanelViewModel.
/// Покрывают StartExecutionAsync (пустая очередь, успех, ошибка, исключение,
/// отмена, параллельный режим), RestoreState/SaveState round-trip и цепочки
/// IsStartButtonEnabled/предупреждений о зависимостях.
///
/// ОГРАНИЧЕНИЕ HEADLESS-СРЕДЫ (задокументировано в задании):
/// WorkPanelViewModel.UpdateFileStatus/FinalizeExecution/OnScriptStateChanged
/// обновляют UI-состояния через App.CurrentMainWindow?.DispatcherQueue?.TryEnqueue(...)
/// с null-conditional. В юнит-тестах App.CurrentMainWindow == null, поэтому
/// лямбды TryEnqueue (обновление State/Status файлов, vm.StatusText) НЕ выполняются —
/// это безопасный пропуск. Все проверки выполнены на Saved*-состояниях скрипта
/// (SavedLogText/SavedStatusText/SavedGlobalProgress), которые обновляются напрямую,
/// и на счётчиках вызовов handler'ов StubScript.
/// Все комментарии выполнены на русском языке.
/// </summary>
[TestClass]
public class WorkPanelViewModelExecutionTests : IsolatedMessengerTestBase
{
    private Mock<INavigationService> _navigationMock = null!;
    private Mock<IDialogService> _dialogMock = null!;
    private Mock<ISettingsManager> _settingsMock = null!;
    private Mock<ILogService> _logMock = null!;
    private Mock<IDependencyManager> _dependencyMock = null!;
    private Mock<IMediaProbeService> _probeMock = null!;

    [TestInitialize]
    public void Setup()
    {
        _navigationMock = MockBuilders.CreateNavigationMock();
        _dialogMock = MockBuilders.CreateDialogMock();
        _settingsMock = MockBuilders.CreateSettingsManagerMock();
        _logMock = MockBuilders.CreateLogServiceMock();
        _dependencyMock = MockBuilders.CreateDependencyManagerMock(allInstalled: true);
        _probeMock = MockBuilders.CreateMediaProbeMock();
    }

    /// <summary>
    /// Создаёт WorkPanelViewModel с моками и трекает подписки messenger.
    /// </summary>
    private WorkPanelViewModel CreateViewModel()
    {
        var vm = new WorkPanelViewModel(
            _navigationMock.Object,
            _dialogMock.Object,
            _settingsMock.Object,
            _logMock.Object,
            _dependencyMock.Object,
            _probeMock.Object);
        MessengerIsolation.Track(vm);
        return vm;
    }

    /// <summary>
    /// Создаёт скрипт с успешным обработчиком по умолчанию.
    /// </summary>
    private StubScript CreateScript(Func<string, Dictionary<string, object>, Task<List<string>>>? handler = null)
    {
        var script = new StubScript(
            _logMock.Object,
            _settingsMock.Object,
            MockBuilders.CreatePathManagerMock().Object);
        script.ExecuteHandler = handler ?? ((file, settings) =>
            Task.FromResult(new List<string> { $"✅ Готово: {file}" }));
        return script;
    }

    /// <summary>
    /// Создаёт очередь файлов со ссылками на несуществующие пути (не требуют диска).
    /// </summary>
    private static ObservableCollection<FileQueueItem> CreateFiles(params string[] paths)
    {
        var files = new ObservableCollection<FileQueueItem>();
        foreach (var path in paths)
        {
            files.Add(new FileQueueItem(path));
        }
        return files;
    }

    /// <summary>
    /// Проверяет, что запуск с пустой очередью логирует ошибку и не запускает обработку.
    /// </summary>
    [TestMethod]
    public async Task StartExecutionAsync_EmptyQueue_LogsErrorAndDoesNotExecute()
    {
        // Arrange
        var script = CreateScript();
        var vm = CreateViewModel();
        vm.Initialize(script, CreateFiles());

        // Act
        await vm.StartExecutionCommand.ExecuteAsync(null);

        // Assert
        script.SavedLogText.Should().Contain("нет файлов для обработки",
            "должно быть записано сообщение об ошибке в лог");
        script.SavedLogText.Should().Contain("❌");
        script.IsProcessing.Should().BeFalse("обработка не должна запускаться");
        script.SavedStatusText.Should().Be("Ожидание запуска...", "статус не должен меняться");
    }

    /// <summary>
    /// Проверяет успешное выполнение двух файлов: финальные Saved-состояния
    /// (статус "Обработка завершена", прогресс 100, логи результатов).
    /// </summary>
    [TestMethod]
    public async Task StartExecutionAsync_TwoFiles_SuccessfulExecution()
    {
        // Arrange
        var executedFiles = new List<string>();
        var script = CreateScript((file, settings) =>
        {
            lock (executedFiles)
            {
                executedFiles.Add(file);
            }
            return Task.FromResult(new List<string> { $"✅ Готово: {file}" });
        });
        var vm = CreateViewModel();
        var files = CreateFiles("C:\\media\\one.mkv", "C:\\media\\two.mp4");
        vm.Initialize(script, files);

        // Act
        await vm.StartExecutionCommand.ExecuteAsync(null);

        // Assert
        executedFiles.Should().HaveCount(2, "оба файла должны быть обработаны");
        executedFiles[0].Should().Be("C:\\media\\one.mkv", "последовательный режим обрабатывает файлы по порядку");
        executedFiles[1].Should().Be("C:\\media\\two.mp4");
        script.SavedStatusText.Should().Be("Обработка завершена");
        script.SavedGlobalProgress.Should().Be(100);
        script.IsProcessing.Should().BeFalse();
        script.SavedLogText.Should().Contain("🚀 Запуск скрипта");
        script.SavedLogText.Should().Contain("✅ Готово: C:\\media\\one.mkv");
        script.SavedLogText.Should().Contain("✅ Готово: C:\\media\\two.mp4");
        script.SavedLogText.Should().Contain("🎉 Все файлы успешно обработаны");
    }

    /// <summary>
    /// Проверяет, что результат с "❌" помечает выполнение как ошибочное:
    /// сообщение об ошибке в логе, финальный статус — ошибка прервана не была.
    /// </summary>
    [TestMethod]
    public async Task StartExecutionAsync_HandlerReturnsError_LoggedAsError()
    {
        // Arrange
        var script = CreateScript((file, settings) =>
            Task.FromResult(new List<string> { "❌ Ошибка декодирования" }));
        var vm = CreateViewModel();
        vm.Initialize(script, CreateFiles("C:\\media\\bad.mkv"));

        // Act
        await vm.StartExecutionCommand.ExecuteAsync(null);

        // Assert — ошибка попадает в лог, но выполнение продолжается до финализации
        script.SavedLogText.Should().Contain("❌ Ошибка декодирования");
        script.IsProcessing.Should().BeFalse("после ошибки выполнение завершается");
        script.SavedGlobalProgress.Should().Be(100, "глобальный прогресс достигает 100 после обработки всех файлов");
    }

    /// <summary>
    /// Проверяет, что исключение в handler перехватывается: файл помечается ошибкой,
    /// логируется через ILogService.Exception и критическое сообщение пишется в лог.
    /// </summary>
    [TestMethod]
    public async Task StartExecutionAsync_HandlerThrows_ExceptionLoggedAndExecutionFinalized()
    {
        // Arrange
        var script = CreateScript((file, settings) =>
            throw new InvalidOperationException("Аварийное завершение процесса"));
        var vm = CreateViewModel();
        vm.Initialize(script, CreateFiles("C:\\media\\crash.mkv"));

        // Act
        await vm.StartExecutionCommand.ExecuteAsync(null);

        // Assert
        _logMock.Verify(
            l => l.Exception(
                It.Is<InvalidOperationException>(ex => ex.Message.Contains("Аварийное завершение")),
                It.IsAny<string>(),
                "WorkPanelViewModel"),
            Times.Once,
            "исключение handler должно логироваться через ILogService.Exception");
        script.SavedLogText.Should().Contain("❌ Критическая ошибка: Аварийное завершение процесса");
        script.IsProcessing.Should().BeFalse("после исключения выполнение финализируется");
        script.SavedStatusText.Should().Be("Обработка завершена");
    }

    /// <summary>
    /// Проверяет отмену выполнения: CancelExecutionCommand во время обработки
    /// приводит к статусу "Обработка отменена" и обнулению прогресса.
    /// </summary>
    [TestMethod]
    public async Task StartExecutionAsync_CancelDuringExecution_StatusBecomesCancelled()
    {
        // Arrange
        var release = new TaskCompletionSource();
        var script = CreateScript(async (file, settings) =>
        {
            await release.Task; // держим обработку, пока тест не отменит
            return new List<string> { $"✅ Готово: {file}" };
        });
        var vm = CreateViewModel();
        vm.Initialize(script, CreateFiles("C:\\media\\slow.mkv"));

        // Act — запускаем и отменяем после старта
        var executionTask = vm.StartExecutionCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => script.IsProcessing, TimeSpan.FromSeconds(2));

        // В headless vm.IsProcessing обновляется только через TryEnqueue (skip),
        // поэтому выставляем синхронно — как это сделал бы UI-поток в реальном приложении.
        vm.IsProcessing = true;
        vm.CancelExecutionCommand.Execute(null);
        vm.IsProcessing.Should().BeTrue("симуляция UI-синхронизации перед отменой");
        release.SetResult(); // разрешаем handler завершиться
        await executionTask;

        // Assert
        script.SavedStatusText.Should().Be("Обработка отменена");
        script.SavedGlobalProgress.Should().Be(0, "при отмене прогресс сбрасывается в 0");
        script.SavedLogText.Should().Contain("⚠ Обработка прервана пользователем");
        script.IsCancelled.Should().BeTrue("скрипт должен находиться в отменённом состоянии");
        script.IsProcessing.Should().BeFalse();
    }

    /// <summary>
    /// Проверяет параллельный режим: supportsParallel + EnableParallel + MaxParallelTasks=3,
    /// все файлы обрабатываются, handler'ы выполняются параллельно.
    /// </summary>
    [TestMethod]
    public async Task StartExecutionAsync_ParallelMode_AllFilesExecuted()
    {
        // Arrange
        _settingsMock.Object.EnableParallel = true;
        _settingsMock.Object.MaxParallelTasks = 3;
        var executedCount = 0;
        var maxConcurrent = 0;
        var concurrent = 0;
        var gate = new object();
        var script = new StubScript(
            _logMock.Object,
            _settingsMock.Object,
            MockBuilders.CreatePathManagerMock().Object,
            supportsParallel: true);
        script.ExecuteHandler = async (file, settings) =>
        {
            lock (gate)
            {
                concurrent++;
                executedCount++;
                maxConcurrent = Math.Max(maxConcurrent, concurrent);
            }
            await Task.Delay(150); // имитируем работу для реального перекрытия
            lock (gate)
            {
                concurrent--;
            }
            return new List<string> { $"✅ Готово: {file}" };
        };
        var vm = CreateViewModel();
        var files = CreateFiles(
            "C:\\p\\a.mkv", "C:\\p\\b.mkv", "C:\\p\\c.mkv", "C:\\p\\d.mkv", "C:\\p\\e.mkv");
        vm.Initialize(script, files);

        // Act
        await vm.StartExecutionCommand.ExecuteAsync(null);

        // Assert
        executedCount.Should().Be(5, "все пять файлов должны быть обработаны");
        maxConcurrent.Should().BeGreaterThan(1,
            "в параллельном режиме обработчики должны перекрываться (лимит 3)");
        maxConcurrent.Should().BeLessThanOrEqualTo(3, "не должно быть больше MaxParallelTasks одновременных задач");
        script.SavedStatusText.Should().Be("Обработка завершена");
        script.SavedGlobalProgress.Should().Be(100);
        script.SavedLogText.Should().Contain("🎉 Все файлы успешно обработаны");
    }

    /// <summary>
    /// Проверяет, что при EnableParallel=false скрипт выполняется последовательно
    /// (никакого перекрытия handler'ов).
    /// </summary>
    [TestMethod]
    public async Task StartExecutionAsync_ParallelDisabledButScriptSupports_ExecutesSequentially()
    {
        // Arrange
        _settingsMock.Object.EnableParallel = false;
        var concurrent = 0;
        var maxConcurrent = 0;
        var gate = new object();
        var script = new StubScript(
            _logMock.Object,
            _settingsMock.Object,
            MockBuilders.CreatePathManagerMock().Object,
            supportsParallel: true);
        script.ExecuteHandler = async (file, settings) =>
        {
            lock (gate)
            {
                maxConcurrent = Math.Max(maxConcurrent, ++concurrent);
            }
            await Task.Delay(80);
            lock (gate)
            {
                concurrent--;
            }
            return new List<string> { "✅ Готово" };
        };
        var vm = CreateViewModel();
        vm.Initialize(script, CreateFiles("C:\\s\\a.mkv", "C:\\s\\b.mkv", "C:\\s\\c.mkv"));

        // Act
        await vm.StartExecutionCommand.ExecuteAsync(null);

        // Assert
        maxConcurrent.Should().Be(1, "при выключенной параллельной настройке файлы обрабатываются по одному");
        script.SavedStatusText.Should().Be("Обработка завершена");
    }

    /// <summary>
    /// Проверяет, что при IsProcessing=true повторный запуск игнорируется.
    /// vm.IsProcessing выставляется вручную (симуляция UI-синхронизации),
    /// так как в headless TryEnqueue-обновление пропускается.
    /// </summary>
    [TestMethod]
    public async Task StartExecutionAsync_AlreadyProcessing_SecondStartIgnored()
    {
        // Arrange
        var executionCount = 0;
        var release = new TaskCompletionSource();
        var script = CreateScript(async (file, settings) =>
        {
            Interlocked.Increment(ref executionCount);
            await release.Task;
            return new List<string> { "✅ Готово" };
        });
        var vm = CreateViewModel();
        vm.Initialize(script, CreateFiles("C:\\busy\\file.mkv"));

        // Act — первый запуск в фоне; после старта выставляем IsProcessing (как UI)
        var firstRun = vm.StartExecutionCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => executionCount == 1, TimeSpan.FromSeconds(2));
        vm.IsProcessing = true; // симуляция UI-синхронизации (headless: TryEnqueue skip)
        await vm.StartExecutionCommand.ExecuteAsync(null); // повторный запуск должен игнорироваться
        release.SetResult();
        await firstRun;

        // Assert
        executionCount.Should().Be(1, "повторный запуск во время обработки не должен дублировать выполнение");
    }

    /// <summary>
    /// Проверяет round-trip SaveState/RestoreState: сохранённые логи, статус,
    /// прогресс и выходной путь восстанавливаются при повторной Initialize.
    /// </summary>
    [TestMethod]
    public void SaveAndRestoreState_ModifiedValues_AreRestoredOnReinitialize()
    {
        // Arrange
        var script = CreateScript();
        var files = CreateFiles("C:\\media\\keep.mkv");
        var vm = CreateViewModel();
        vm.Initialize(script, files);

        // Act — меняем состояние VM и сохраняем
        vm.LogText = "Накопленный лог выполнения";
        vm.StatusText = "Обработка файла 3 из 5 (42.0%)";
        vm.GlobalProgressValue = 42.0;
        vm.OutputPath = "C:\\output\\results";
        vm.SaveState();

        // Assert — Saved-состояния скрипта обновлены
        script.SavedLogText.Should().Be("Накопленный лог выполнения");
        script.SavedStatusText.Should().Be("Обработка файла 3 из 5 (42.0%)");
        script.SavedGlobalProgress.Should().Be(42.0);
        script.SavedOutputPath.Should().Be("C:\\output\\results");

        // Act — создаем новый VM (симуляция повторного открытия страницы) и восстанавливаем
        var vm2 = CreateViewModel();
        vm2.Initialize(script, files);

        // Assert — состояние восстановлено из скрипта
        vm2.LogText.Should().Be("Накопленный лог выполнения");
        vm2.StatusText.Should().Be("Обработка файла 3 из 5 (42.0%)");
        vm2.GlobalProgressValue.Should().Be(42.0);
        vm2.OutputPath.Should().Be("C:\\output\\results");
    }

    /// <summary>
    /// Проверяет, что RestoreState после SaveState с другим VM восстанавливает
    /// значения по умолчанию, если они не были сохранены.
    /// </summary>
    [TestMethod]
    public void SaveState_ThenRestoreWithFreshVM_PropertiesMatchSavedScriptState()
    {
        // Arrange
        var script = CreateScript();
        var vm1 = CreateViewModel();
        vm1.Initialize(script, CreateFiles("C:\\media\\x.mkv"));
        vm1.LogText = "line1";
        vm1.SaveState();

        // Act — новый VM без изменений
        var vm2 = CreateViewModel();
        vm2.Initialize(script, CreateFiles("C:\\media\\x.mkv"));

        // Assert
        vm2.LogText.Should().Be("line1", "лог восстанавливается");
        vm2.GlobalProgressValue.Should().Be(0);
        vm2.IsProcessing.Should().BeFalse();
        vm2.IsStartButtonEnabled.Should().BeTrue("после восстановления кнопка запуска доступна");
    }

    /// <summary>
    /// Проверяет цепочку IsStartButtonEnabled: отсутствующие зависимости блокируют
    /// кнопку и показывают предупреждение.
    /// </summary>
    [TestMethod]
    public void CheckDependencies_MissingDependency_DisablesStartAndShowsWarning()
    {
        // Arrange
        var script = new StubScript(
            _logMock.Object,
            _settingsMock.Object,
            MockBuilders.CreatePathManagerMock().Object,
            dependencies: new[] { "ffmpeg", "mkvtoolnix" });
        _dependencyMock.Setup(d => d.IsInstalled("ffmpeg")).Returns(true);
        _dependencyMock.Setup(d => d.IsInstalled("mkvtoolnix")).Returns(false);
        var vm = CreateViewModel();

        // Act
        vm.Initialize(script, CreateFiles("C:\\media\\file.mkv"));

        // Assert
        vm.IsStartButtonEnabled.Should().BeFalse("отсутствующая зависимость должна блокировать запуск");
        vm.IsDependencyWarningOpen.Should().BeTrue();
        vm.DependencyWarningText.Should().Contain("MKVTOOLNIX",
            "имя отсутствующей зависимости должно быть в предупреждении в верхнем регистре");
        vm.DependencyWarningText.Should().NotContain("FFMPEG", "установленная зависимость не указывается");
    }

    /// <summary>
    /// Проверяет цепочку IsStartButtonEnabled: все зависимости установлены → кнопка доступна.
    /// </summary>
    [TestMethod]
    public void CheckDependencies_AllInstalled_EnablesStartButton()
    {
        // Arrange
        var script = new StubScript(
            _logMock.Object,
            _settingsMock.Object,
            MockBuilders.CreatePathManagerMock().Object,
            dependencies: new[] { "ffmpeg" });
        _dependencyMock.Setup(d => d.IsInstalled(It.IsAny<string>())).Returns(true);
        var vm = CreateViewModel();

        // Act
        vm.Initialize(script, CreateFiles("C:\\media\\file.mkv"));

        // Assert
        vm.IsStartButtonEnabled.Should().BeTrue();
        vm.IsDependencyWarningOpen.Should().BeFalse();
        vm.DependencyWarningText.Should().BeEmpty();
    }

    /// <summary>
    /// Проверяет CheckDependencies без активного скрипта — возвращает false.
    /// </summary>
    [TestMethod]
    public void CheckDependencies_NoActiveScript_ReturnsFalse()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        bool result = vm.CheckDependencies();

        // Assert
        result.Should().BeFalse("без скрипта зависимостей быть не может");
    }

    /// <summary>
    /// Проверяет, что Initialize отправляет ActiveScriptChangedMessage
    /// и выставляет флаги видимости вкладок по схеме скрипта.
    /// </summary>
    [TestMethod]
    public void Initialize_WithScript_SendsActiveScriptChangedMessage()
    {
        // Arrange
        var script = CreateScript();
        ActiveScriptChangedMessage? received = null;
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Register<WorkPanelViewModelExecutionTests, ActiveScriptChangedMessage>(
            this, (r, m) => received = m);
        var vm = CreateViewModel();

        // Act
        vm.Initialize(script, CreateFiles("C:\\media\\f.mkv"));

        // Assert
        received.Should().NotBeNull("Initialize должен рассылать сообщение об активном скрипте");
        received!.Script.Should().BeSameAs(script);
        vm.IsTracksTabVisible.Should().BeFalse("StubScript не использует кастомный виджет");
        vm.IsSettingsTabVisible.Should().BeTrue("полная схема содержит поля переименования");

        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    /// <summary>
    /// Проверяет, что Initialize со скриптом с UseCustomWidget показывает вкладку дорожек.
    /// </summary>
    [TestMethod]
    public void Initialize_CustomWidgetScript_ShowsTracksTab()
    {
        // Arrange
        var script = new StubScript(
            _logMock.Object,
            _settingsMock.Object,
            MockBuilders.CreatePathManagerMock().Object,
            useCustomWidget: true);
        var vm = CreateViewModel();

        // Act
        vm.Initialize(script, CreateFiles("C:\\media\\f.mkv"));

        // Assert
        vm.IsTracksTabVisible.Should().BeTrue();
    }

    /// <summary>
    /// Проверяет, что после успешного выполнения повторная Initialize восстанавливает
    /// итоговое состояние (статус "Обработка завершена", прогресс 100).
    /// </summary>
    [TestMethod]
    public async Task FullExecutionRoundTrip_SaveRestoreAfterExecution_StatePersisted()
    {
        // Arrange
        var script = CreateScript();
        var files = CreateFiles("C:\\media\\final.mkv");
        var vm = CreateViewModel();
        vm.Initialize(script, files);

        // Act — выполняем и пересоздаем VM (эмуляция ухода и возврата на страницу).
        // SaveState НЕ вызываем: RestoreState при Initialize читает Saved*-состояния,
        // установленные FinalizeExecution (в headless vm.StatusText не синхронизируется
        // через TryEnqueue, поэтому SaveState перезаписал бы актуальный статус).
        await vm.StartExecutionCommand.ExecuteAsync(null);
        var vm2 = CreateViewModel();
        vm2.Initialize(script, files);

        // Assert
        vm2.StatusText.Should().Be("Обработка завершена", "финальный статус сохраняется между визитами");
        vm2.GlobalProgressValue.Should().Be(100);
        vm2.LogText.Should().Contain("🎉 Все файлы успешно обработаны");
        vm2.IsStartButtonEnabled.Should().BeTrue("после завершения доступен повторный запуск");
    }

    /// <summary>
    /// Проверяет, что выбранные дорожки из TrackSelectedMessage попадают в настройки выполнения.
    /// </summary>
    [TestMethod]
    public async Task StartExecutionAsync_TrackSelectionMessageSelected_SettingsContainSelection()
    {
        // Arrange
        Dictionary<string, object>? capturedSettings = null;
        var script = CreateScript((file, settings) =>
        {
            capturedSettings = settings;
            return Task.FromResult(new List<string> { "✅ Готово" });
        });
        var vm = CreateViewModel();
        vm.Initialize(script, CreateFiles("C:\\media\\sel.mkv"));

        // Act — отправляем выбор дорожек и запускаем
        var tracks = new Dictionary<string, List<int>>
        {
            ["C:\\media\\sel.mkv"] = new List<int> { 0, 1 }
        };
        var attachments = new Dictionary<string, List<int>>
        {
            ["C:\\media\\sel.mkv"] = new List<int> { 2 }
        };
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(
            new KTools_App.ViewModels.Messages.TrackSelectedMessage(tracks, attachments));
        await vm.StartExecutionCommand.ExecuteAsync(null);

        // Assert
        capturedSettings.Should().NotBeNull("handler должен был получить настройки");
        capturedSettings!.Should().ContainKey("selected_tracks_per_file",
            "выбор дорожек из сообщения должен передаваться в настройки");
        capturedSettings.Should().ContainKey("selected_attachments_per_file");
        capturedSettings["selected_tracks_per_file"].Should().BeSameAs(tracks);
    }

    /// <summary>
    /// Проверяет, что выполнение с заданным пользовательским OutputPath
    /// успешно завершается (передача outputPath в handler недоступна для
    /// проверки напрямую: StubScript.ExecuteSingleAsync его не пробрасывает).
    /// </summary>
    [TestMethod]
    public async Task StartExecutionAsync_CustomOutputPath_ExecutesSuccessfully()
    {
        // Arrange
        var script = new StubScript(
            _logMock.Object,
            _settingsMock.Object,
            MockBuilders.CreatePathManagerMock().Object);
        script.ExecuteHandler = (file, settings) => Task.FromResult(new List<string> { "✅ Готово" });
        var vm = CreateViewModel();
        vm.Initialize(script, CreateFiles("C:\\media\\out.mkv"));
        vm.OutputPath = "C:\\custom\\out";

        // Act
        await vm.StartExecutionCommand.ExecuteAsync(null);

        // Assert — выполнение успешно завершается с кастомным OutputPath
        script.SavedStatusText.Should().Be("Обработка завершена");
        script.SavedLogText.Should().Contain("✅ Готово");
    }

    /// <summary>
    /// Проверяет, что StartExecutionAsync при ActiveScript=null не бросает исключений.
    /// </summary>
    [TestMethod]
    public async Task StartExecutionAsync_NoActiveScript_DoesNotThrow()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        Func<Task> act = () => vm.StartExecutionCommand.ExecuteAsync(null);

        // Assert
        await act.Should().NotThrowAsync("запуск без активного скрипта безопасен");
    }

    /// <summary>
    /// Проверяет, что при успешном выполнении всей очереди задач без ошибок
    /// панель журнала выполнения остается закрытой (IsLogExpanded == false).
    /// </summary>
    [TestMethod]
    public async Task StartExecutionAsync_SuccessfulExecution_LogExpanderRemainsClosed()
    {
        // Arrange
        var script = CreateScript((file, settings) =>
            Task.FromResult(new List<string> { $"✅ Готово: {file}" }));
        var vm = CreateViewModel();
        vm.Initialize(script, CreateFiles("C:\\media\\file1.mkv", "C:\\media\\file2.mp4"));
        vm.IsLogExpanded = false;

        // Act
        await vm.StartExecutionCommand.ExecuteAsync(null);

        // Assert
        vm.IsLogExpanded.Should().BeFalse("при успешном выполнении очереди лог не должен раскрываться автоматически");
        script.SavedStatusText.Should().Be("Обработка завершена");
    }

    /// <summary>
    /// Проверяет, что при возникновении ошибки выполнения файла в результатах (префикс "❌")
    /// панель журнала выполнения автоматически открывается (IsLogExpanded == true).
    /// </summary>
    [TestMethod]
    public async Task StartExecutionAsync_WithError_LogExpanderOpens()
    {
        // Arrange
        var script = CreateScript((file, settings) =>
            Task.FromResult(new List<string> { "❌ Ошибка обработки файла" }));
        var vm = CreateViewModel();
        vm.Initialize(script, CreateFiles("C:\\media\\file_with_error.mkv"));
        vm.IsLogExpanded = false;

        // Act
        await vm.StartExecutionCommand.ExecuteAsync(null);

        // Assert
        vm.IsLogExpanded.Should().BeTrue("при ошибке в результатах выполнения лог должен автоматически раскрыться");
    }

    /// <summary>
    /// Проверяет, что при возникновении необработанного исключения в обработчике задачи
    /// панель журнала выполнения также автоматически открывается (IsLogExpanded == true).
    /// </summary>
    [TestMethod]
    public async Task StartExecutionAsync_HandlerThrows_LogExpanderOpens()
    {
        // Arrange
        var script = CreateScript((file, settings) =>
            throw new InvalidOperationException("Фатальная ошибка кодирования"));
        var vm = CreateViewModel();
        vm.Initialize(script, CreateFiles("C:\\media\\fatal.mkv"));
        vm.IsLogExpanded = false;

        // Act
        await vm.StartExecutionCommand.ExecuteAsync(null);

        // Assert
        vm.IsLogExpanded.Should().BeTrue("при исключении в процессе обработки файла лог должен автоматически раскрыться");
    }

    /// <summary>
    /// Вспомогательный метод ожидания условия с поллингом (без Thread.Sleep).
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(25);
        }
    }
}
