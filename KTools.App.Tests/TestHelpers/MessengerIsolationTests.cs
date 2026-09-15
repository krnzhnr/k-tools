// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KTools_App.Tests.TestHelpers;

/// <summary>
/// Проверки работоспособности самой тестовой инфраструктуры.
/// Гарантируют, что хелперы корректно создаются, изолируют и очищают состояние.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class MessengerIsolationTests
{
    /// <summary>
    /// Убеждается, что полный сброс мессенджера отсоединяет старые подписки:
    /// сообщения, отправленные после ResetMessenger, не доходят до получателей,
    /// зарегистрированных до сброса.
    /// </summary>
    [TestMethod]
    public void ResetMessenger_WithTrackedRecipient_OldSubscribersDetached()
    {
        // Arrange
        var recipient = new object();
        bool oldReceived = false;
        WeakReferenceMessenger.Default.Register<string>(recipient, (_, _) => oldReceived = true);
        MessengerIsolation.Track(recipient);

        // Act
        MessengerIsolation.ResetMessenger();

        // Assert
        WeakReferenceMessenger.Default.Send("test");
        oldReceived.Should().BeFalse("отслеживаемая подписка должна быть снята при сбросе");
    }

    /// <summary>
    /// Убеждается, что IsolatedMessengerTestBase сбрасывает подписки перед тестом
    /// и после теста, не оставляя их между тестами.
    /// </summary>
    [TestMethod]
    public void IsolatedMessengerTestBase_Lifecycle_MessengerResetBeforeAndAfter()
    {
        // Arrange / Act — имитируем жизненный цикл наследника базового класса
        var instance = new DerivedIsolatedTest();
        instance.MessengerSetup();

        var recipient = new object();
        bool received = false;
        WeakReferenceMessenger.Default.Register<string>(recipient, (_, _) => received = true);
        MessengerIsolation.Track(recipient);
        WeakReferenceMessenger.Default.Send("hello");
        received.Should().BeTrue("сообщение должно доходить до действующей подписки");

        instance.MessengerCleanup();

        // Assert — после очистки подписка снята
        received = false;
        WeakReferenceMessenger.Default.Send("again");
        received.Should().BeFalse("после TestCleanup подписки не должны получать сообщения");
    }

    private sealed class DerivedIsolatedTest : IsolatedMessengerTestBase
    {
    }
}

/// <summary>
/// Проверки работоспособности TempDirectoryScope.
/// </summary>
[TestClass]
public sealed class TempDirectoryScopeTests
{
    /// <summary>
    /// Убеждается, что временная директория создается, файлы записываются
    /// с созданием подкаталогов, а Dispose полностью удаляет структуру.
    /// </summary>
    [TestMethod]
    public void TempDirectoryScope_CreateFileAndDispose_DirectoryCreatedAndRemoved()
    {
        string rootPath;
        using (var scope = new TempDirectoryScope())
        {
            // Assert — директория уже создана конструктором
            Directory.Exists(scope.RootPath).Should().BeTrue();

            // Act — создаем файл во вложенном подкаталоге
            string file = scope.CreateFile(
                Path.Combine("sub", "inner", "data.txt"),
                "содержимое");
            File.Exists(file).Should().BeTrue();
            File.ReadAllText(file).Should().Be("содержимое");
            rootPath = scope.RootPath;
        }

        // Assert — Dispose удаляет всё дерево
        Directory.Exists(rootPath).Should().BeFalse();
    }

    /// <summary>
    /// Убеждается, что два TempDirectoryScope создают непересекающиеся директории.
    /// </summary>
    [TestMethod]
    public void TempDirectoryScope_TwoScopes_DoNotCollide()
    {
        using var first = new TempDirectoryScope();
        using var second = new TempDirectoryScope();

        first.RootPath.Should().NotBe(second.RootPath);
    }
}

/// <summary>
/// Проверки работоспособности StubScript и PropertyChangedRecorder.
/// </summary>
[TestClass]
public sealed class TestInfrastructureTests
{
    /// <summary>
    /// Убеждается, что StubScript конструируется с корректными метаданными
    /// и дефолтный обработчик возвращает успешный результат.
    /// </summary>
    [TestMethod]
    public async Task StubScript_DefaultHandler_ReturnsSuccessResult()
    {
        // Arrange
        var logMock = MockBuilders.CreateLogServiceMock();
        var settingsMock = MockBuilders.CreateSettingsManagerMock();
        var pathMock = MockBuilders.CreatePathManagerMock();
        var script = new StubScript(logMock.Object, settingsMock.Object, pathMock.Object);

        // Act
        var result = await script.ExecuteSingleAsync(
            "C:\\test\\file.mkv",
            new Dictionary<string, object>(),
            outputPath: null,
            progressCallback: (_, _, _, _, _, _) => { },
            fileIndex: 0,
            totalCount: 1);

        // Assert
        script.Name.Should().Be(StubScript.DefaultName);
        script.SupportsParallel.Should().BeFalse();
        script.UseCustomWidget.Should().BeFalse();
        script.RequiredDependencies.Should().BeEmpty();
        result.Should().ContainSingle().Which.Should().StartWith("✅ Готово:");
    }

    /// <summary>
    /// Убеждается, что PropertyChangedRecorder фиксирует события
    /// с актуальными значениями свойств.
    /// </summary>
    [TestMethod]
    public void PropertyChangedRecorder_TracksFileQueueItem_ValuesCaptured()
    {
        // Arrange
        var item = new FileQueueItem("C:\\test\\movie.mkv");
        var recorder = new PropertyChangedRecorder(item);

        // Act
        item.Status = "Обработка";
        item.Progress = 55.5;
        item.State = FileProcessingState.Processing;

        // Assert
        var statusEvents = recorder.GetEventsFor(nameof(FileQueueItem.Status));
        statusEvents.Should().ContainSingle().Which.Value.Should().Be("Обработка");

        var progressEvents = recorder.GetEventsFor(nameof(FileQueueItem.Progress));
        progressEvents.Should().ContainSingle().Which.Value.Should().Be(55.5);

        var stateEvents = recorder.GetEventsFor(nameof(FileQueueItem.State));
        stateEvents.Should().ContainSingle().Which.Value.Should().Be(FileProcessingState.Processing);

        recorder.AllEvents.Count.Should().BeGreaterThanOrEqualTo(3);
    }

    /// <summary>
    /// Убеждается, что recorder после Detach больше не фиксирует события.
    /// </summary>
    [TestMethod]
    public void PropertyChangedRecorder_AfterDetach_StopsRecording()
    {
        // Arrange
        var item = new FileQueueItem("C:\\test\\movie.mkv");
        var recorder = new PropertyChangedRecorder(item);

        // Act
        recorder.Detach();
        item.Status = "Новое состояние";

        // Assert
        recorder.GetEventsFor(nameof(FileQueueItem.Status)).Should().BeEmpty();
    }

    /// <summary>
    /// Убеждается, что рекордер отклоняет null-источник.
    /// </summary>
    [TestMethod]
    public void PropertyChangedRecorder_NullSource_ThrowsArgumentNullException()
    {
        // Act / Assert
        var act = () => new PropertyChangedRecorder(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
