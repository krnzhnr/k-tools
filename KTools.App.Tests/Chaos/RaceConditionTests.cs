// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.ViewModels;
using KTools_App.Services.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests.Chaos;

/// <summary>
/// Хаос-тесты состояний гонки в WorkPanelViewModel и коллекциях.
/// Проверяют поведение при быстрых повторных действиях пользователя,
/// отмене во время выполнения и параллельной обработке очереди.
///
/// ВАЖНОЕ ОГРАНИЧЕНИЕ HEADLESS-СРЕды: обновления VM-свойств и FileQueueItem
/// в WorkPanelViewModel маршализируются через App.CurrentMainWindow.DispatcherQueue
/// (UpdateFileStatus, FinalizeExecution, OnScriptStateChanged). В MSTest-процессе
/// App.CurrentMainWindow == null, поэтому эти обновления пропускаются. Ассерты
/// выполняются на уровне AbstractScript.Saved* (заполняется напрямую, без диспетчера).
///
/// Класс помечен DoNotParallelize: тесты проверяют временные окна выполнения,
/// чувствительные к конкуренции за CPU.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RaceConditionTests : IsolatedMessengerTestBase
{
    private Mock<INavigationService> _navigationMock = null!;
    private Mock<IDialogService> _dialogMock = null!;
    private Mock<ISettingsManager> _settingsMock = null!;
    private Mock<ILogService> _logMock = null!;
    private Mock<IDependencyManager> _dependencyMock = null!;
    private Mock<IMediaProbeService> _probeMock = null!;

    /// <summary>
    /// Создаёт настроенные моки зависимостей WorkPanelViewModel.
    /// </summary>
    [TestInitialize]
    public void ChaosSetup()
    {
        MessengerIsolation.ResetMessenger();
        _navigationMock = MockBuilders.CreateNavigationMock();
        _dialogMock = MockBuilders.CreateDialogMock();
        _settingsMock = MockBuilders.CreateSettingsManagerMock();
        _logMock = MockBuilders.CreateLogServiceMock();
        _dependencyMock = MockBuilders.CreateDependencyManagerMock();
        _probeMock = MockBuilders.CreateMediaProbeMock();
    }

    private WorkPanelViewModel CreateViewModel()
    {
        return new WorkPanelViewModel(
            _navigationMock.Object,
            _dialogMock.Object,
            _settingsMock.Object,
            _logMock.Object,
            _dependencyMock.Object,
            _probeMock.Object);
    }

    private StubScript CreateScript(
        Func<string, Dictionary<string, object>, Task<List<string>>>? handler = null,
        bool supportsParallel = false)
    {
        var script = new StubScript(
            _logMock.Object,
            _settingsMock.Object,
            MockBuilders.CreatePathManagerMock().Object,
            supportsParallel: supportsParallel);
        if (handler != null)
        {
            script.ExecuteHandler = handler;
        }

        return script;
    }

    /// <summary>
    /// Хаос: двойной быстрый клик по кнопке запуска. Текущее поведение —
/// characterization: гвардия IsProcessing обновляется асинхронно через
    /// диспетчер (OnScriptStateChanged), поэтому оба запуска могут пройти
    /// (найденный баг, см. StartExecution_DoubleConcurrentClick_ShouldExecuteOnlyOnce).
    /// Проверяем, что двойной запуск не приводит к краху и очередь
    /// завершается в согласованном состоянии.
    /// </summary>
    [TestMethod]
    public async Task StartExecution_DoubleConcurrentClick_DoesNotCrashAndFinalizes()
    {
        // Arrange
        using var tempDir = new TempDirectoryScope();
        string file = tempDir.CreateFile("video.mkv", "data");
        int executions = 0;

        var script = CreateScript((_, _) =>
        {
            Interlocked.Increment(ref executions);
            return Task.FromResult(new List<string> { "✅ Готово" });
        });

        var vm = CreateViewModel();
        var files = new ObservableCollection<FileQueueItem> { new(file) };
        vm.Initialize(script, files);
        MessengerIsolation.Track(vm);

        // Act — два одновременных запуска
        var first = vm.StartExecutionCommand.ExecuteAsync(null);
        var second = vm.StartExecutionCommand.ExecuteAsync(null);
        await Task.WhenAll(first, second);

        // Assert — оба выполнения завершились без взаимных блокировок
        executions.Should().BeGreaterThanOrEqualTo(1,
            "как минимум первое выполнение обязано произойти");
        script.IsProcessing.Should().BeFalse(
            "после завершения обоих запусков скрипт не должен остаться в состоянии обработки");
    }

    /// <summary>
    /// Хаос (желаемое поведение, СЕЙЧАС НЕ ВЫПОЛНЯЕТСЯ — известный баг):
    /// повторный клик до завершения первого выполнения должен блокироваться
    /// гвардией IsProcessing. Фактически гвардия читает VM-свойство IsProcessing,
    /// которое обновляется только асинхронно через DispatcherQueue
    /// (WorkPanelViewModel.OnScriptStateChanged), — между запуском и обработкой
    /// события существует окно гонки, в которое второй запуск проходит
    /// (WorkPanelViewModel.cs:301 — проверка устаревшего состояния;
    /// PrepareExecutionState устанавливает ActiveScript.IsProcessing синхронно,
    /// но гвардия проверяет свойство VM, а не скрипта).
    /// </summary>
    [TestMethod]
    [Ignore("Известный баг приложения: гвардия двойного запуска использует "
        + "VM.IsProcessing, обновляемый асинхронно через DispatcherQueue — "
        + "состояние гонки позволяет второе выполнение. Тест активировать после "
        + "добавления синхронного флага исполнения (например, volatile bool "
        + "_isExecuting в WorkPanelViewModel).")]
    public async Task StartExecution_DoubleConcurrentClick_ShouldExecuteOnlyOnce()
    {
        using var tempDir = new TempDirectoryScope();
        string file = tempDir.CreateFile("video.mkv", "data");
        int executions = 0;

        var script = CreateScript(async (_, _) =>
        {
            Interlocked.Increment(ref executions);
            await Task.Delay(300);
            return new List<string> { "✅ Готово" };
        });

        var vm = CreateViewModel();
        var files = new ObservableCollection<FileQueueItem> { new(file) };
        vm.Initialize(script, files);
        MessengerIsolation.Track(vm);

        var first = vm.StartExecutionCommand.ExecuteAsync(null);
        var second = vm.StartExecutionCommand.ExecuteAsync(null);
        await Task.WhenAll(first, second);

        executions.Should().Be(1,
            "повторный запуск во время обработки должен блокироваться");
    }

    /// <summary>
    /// Хаос: отмена скрипта во время активного выполнения переводит
    /// итоговый статус в "Обработка отменена" и помечает журнал прерывания.
    /// Отмена выполняется напрямую через AbstractScript.Cancel, так как
    /// VM-команда CancelExecutionCommand зависит от VM.IsProcessing,
    /// которое в headless не обновляется (см. док класса).
    /// </summary>
    [TestMethod]
    public async Task Cancel_DuringProcessing_MarksExecutionCancelled()
    {
        // Arrange
        using var tempDir = new TempDirectoryScope();
        string file = tempDir.CreateFile("video.mkv", "data");
        using var startedGate = new ManualResetEventSlim(false);

        StubScript? captured = null;
        captured = CreateScript(async (_, _) =>
        {
            startedGate.Set();
            // Ждём команду отмены с защитным таймаутом
            bool cancelled = await TestAsyncHelpers.WaitForAsync(
                () => captured!.IsCancelled,
                TimeSpan.FromSeconds(8));
            return new List<string> { cancelled ? "Прервано" : "✅ Готово" };
        });

        var vm = CreateViewModel();
        var files = new ObservableCollection<FileQueueItem> { new(file) };
        vm.Initialize(captured, files);
        MessengerIsolation.Track(vm);

        // Act — стартуем и отменяем, как только выполнение началось
        var execution = vm.StartExecutionCommand.ExecuteAsync(null);
        startedGate.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue(
            "обработчик должен запуститься в разумные сроки");
        captured!.Cancel();
        await execution;

        // Assert — статус скрипта отражает отмену
        captured.SavedStatusText.Should().Be("Обработка отменена",
            "отмена во время выполнения должна отражаться в статусе скрипта");
        captured.SavedLogText.Should().Contain("прервана",
            "журнал должен содержать отметку о прерывании пользователем");
    }

    /// <summary>
    /// Хаос: параллельная обработка 8 файлов с ограничением в 4 слота
    /// завершает все файлы и вычисляет корректный итоговый прогресс.
    /// </summary>
    [TestMethod]
    public async Task StartExecution_ParallelQueueOf8_CompletesWithProgress100()
    {
        // Arrange
        using var tempDir = new TempDirectoryScope();
        var files = new ObservableCollection<FileQueueItem>();
        for (int i = 0; i < 8; i++)
        {
            files.Add(new FileQueueItem(tempDir.CreateFile($"file{i}.mkv", "data")));
        }

        int concurrentPeak = 0;
        int concurrentCurrent = 0;

        var script = CreateScript(async (_, _) =>
        {
            int current = Interlocked.Increment(ref concurrentCurrent);
            try
            {
                int observed;
                do
                {
                    observed = Volatile.Read(ref concurrentPeak);
                    if (current <= observed)
                    {
                        break;
                    }
                }
                while (Interlocked.CompareExchange(ref concurrentPeak, current, observed) != observed);

                await Task.Delay(100);
                return new List<string> { "✅ Готово" };
            }
            finally
            {
                Interlocked.Decrement(ref concurrentCurrent);
            }
        }, supportsParallel: true);

        _settingsMock.Object.EnableParallel = true;
        _settingsMock.Object.MaxParallelTasks = 4;

        var vm = CreateViewModel();
        vm.Initialize(script, files);
        MessengerIsolation.Track(vm);

        // Act
        await vm.StartExecutionCommand.ExecuteAsync(null);

        // Assert — на уровне скрипта (диспетчер-независимые поля)
        script.SavedStatusText.Should().Be("Обработка завершена");
        script.SavedGlobalProgress.Should().Be(100,
            "все 8 файлов должны быть учтены в интегральном прогрессе");
        concurrentPeak.Should().BeLessThanOrEqualTo(4,
            "семафор должен ограничивать одновременную обработку настроенным значением");
    }

    /// <summary>
    /// Хаос (characterization, найденный баг): переключение скрипта во время
    /// выполнения финализирует НОВЫЙ скрипт, а не выполняющийся. FinalizeExecution
    /// (WorkPanelViewModel.cs:531) читает текущее свойство ActiveScript вместо
    /// захваченного экземпляра — старый скрипт остаётся IsProcessing=true навсегда
    /// (утечка состояния), новый получает статус "Обработка завершена" и прогресс
    /// выполнения чужой очереди.
    /// </summary>
    [TestMethod]
    public async Task Initialize_SwitchingScriptDuringExecution_FinalizesNewScriptInstead()
    {
        // Arrange
        using var tempDir = new TempDirectoryScope();
        string fileA = tempDir.CreateFile("a.mkv", "data");
        string fileB = tempDir.CreateFile("b.mkv", "data");
        using var release = new SemaphoreSlim(0, 1);

        var slowScript = CreateScript(async (_, _) =>
        {
            await release.WaitAsync(TimeSpan.FromSeconds(10));
            return new List<string> { "✅ Готово" };
        });
        var fastScript = CreateScript();

        var vm = CreateViewModel();
        var filesA = new ObservableCollection<FileQueueItem> { new(fileA) };
        var filesB = new ObservableCollection<FileQueueItem> { new(fileB) };
        vm.Initialize(slowScript, filesA);
        MessengerIsolation.Track(vm);

        // Act — запускаем и тут же переключаем скрипт
        var execution = vm.StartExecutionCommand.ExecuteAsync(null);
        vm.Initialize(fastScript, filesB);

        release.Release();
        await execution;
        await Task.Delay(200); // даём финализации завершиться

        // Assert — текущее (ошибочное) поведение зафиксировано:
        // финализация применена к новому скрипту
        fastScript.SavedStatusText.Should().Be("Обработка завершена",
            "баг: FinalizeExecution финализирует текущий ActiveScript, а не выполнявшийся");
        fastScript.SavedGlobalProgress.Should().Be(100);
        slowScript.IsProcessing.Should().BeTrue(
            "баг: исходный скрипт зависает в IsProcessing=true — состояние никогда не сбрасывается");
    }

    /// <summary>
    /// Хаос (желаемое поведение, СЕЙЧАС НЕ ВЫПОЛНЯЕТСЯ — известный баг):
    /// переключение скрипта во время выполнения должно корректно
    /// финализировать именно выполнявшийся скрипт.
    /// </summary>
    [TestMethod]
    [Ignore("Известный баг приложения: WorkPanelViewModel.FinalizeExecution (строка ~531) "
        + "обращается к свойству ActiveScript на момент завершения, а не к захваченному "
        + "экземпляру выполнявшегося скрипта — при переключении вкладок во время обработки "
        + "статус и прогресс применяются к новому скрипту, старый зависает в IsProcessing. "
        + "Тест активировать после захвата ссылки на выполняемый скрипт в ProcessQueueAsync.")]
    public async Task Initialize_SwitchingScriptDuringExecution_ShouldFinalizeOldScript()
    {
        using var tempDir = new TempDirectoryScope();
        string fileA = tempDir.CreateFile("a.mkv", "data");
        string fileB = tempDir.CreateFile("b.mkv", "data");
        using var release = new SemaphoreSlim(0, 1);

        var slowScript = CreateScript(async (_, _) =>
        {
            await release.WaitAsync(TimeSpan.FromSeconds(10));
            return new List<string> { "✅ Готово" };
        });
        var fastScript = CreateScript();

        var vm = CreateViewModel();
        var filesA = new ObservableCollection<FileQueueItem> { new(fileA) };
        var filesB = new ObservableCollection<FileQueueItem> { new(fileB) };
        vm.Initialize(slowScript, filesA);
        MessengerIsolation.Track(vm);

        var execution = vm.StartExecutionCommand.ExecuteAsync(null);
        vm.Initialize(fastScript, filesB);

        release.Release();
        await execution;

        slowScript.IsProcessing.Should().BeFalse(
            "исходный скрипт обязан корректно финализироваться");
        slowScript.SavedStatusText.Should().Be("Обработка завершена");
        fastScript.SavedStatusText.Should().NotBe("Обработка завершена",
            "новый скрипт не должен получать финализацию чужой очереди");
    }

    /// <summary>
    /// Хаос: ReplaceRange из фонового потока при активном подписчике
    /// доставляет Reset-уведомление и не приводит к исключениям.
    /// </summary>
    [TestMethod]
    public void ObservableRangeCollection_ReplaceRangeFromBackgroundThread_DeliversReset()
    {
        // Arrange
        var collection = new ObservableRangeCollection<int>(new[] { 1, 2, 3 });
        int resetCount = 0;
        collection.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                Interlocked.Increment(ref resetCount);
            }
        };

        // Act
        var task = Task.Run(() =>
            collection.ReplaceRange(Enumerable.Range(10, 5)));
        task.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        // Assert
        collection.Should().BeEquivalentTo(new[] { 10, 11, 12, 13, 14 });
        resetCount.Should().BeGreaterThanOrEqualTo(1,
            "ReplaceRange обязан отправить хотя бы одно Reset-уведомление");
    }

    /// <summary>
    /// Хаос: пакетное добавление из множества потоков под внешней блокировкой
    /// не должно терять элементы и уведомления.
    /// </summary>
    [TestMethod]
    public void ObservableRangeCollection_ConcurrentAddRangeWithExternalLock_LosesNothing()
    {
        // Arrange
        var collection = new ObservableRangeCollection<int>();
        var gate = new object();
        const int threads = 10;
        const int perThread = 200;
        var tasks = new List<Task>();

        // Act
        for (int t = 0; t < threads; t++)
        {
            int threadId = t;
            tasks.Add(Task.Run(() =>
            {
                lock (gate)
                {
                    collection.AddRange(
                        Enumerable.Range(threadId * perThread, perThread));
                }
            }));
        }

        Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(30));

        // Assert — все 2000 элементов на месте
        collection.Count.Should().Be(threads * perThread,
            "пакетное добавление под внешней блокировкой не должно терять элементы");
        collection.ToHashSet().Count.Should().Be(threads * perThread,
            "все добавленные элементы должны быть уникальны");
    }
}
