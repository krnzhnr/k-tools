// -*- coding: utf-8 -*-
using System.IO;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;
using KTools_App.UI.Controls;

namespace KTools_App.Tests.TrackSelection;

/// <summary>
/// Юнит-тесты персистентного флага одноразовой подсказки о горизонтальной прокрутке фильтров.
/// Проверяют, что факт показа переживает пересоздание элемента управления и перезапуск
/// приложения (ишью #77: раньше флаг жил только в памяти TrackSelectionControl и сбрасывался
/// при переходе между скриптами и перезапуске).
/// Все комментарии выполнены на русском языке.
/// </summary>
[TestClass]
public class DemuxScrollTipTrackerTests
{
    private string _tempSettingsDir = null!;
    private readonly List<SettingsManager> _managers = new();

    [TestInitialize]
    public void Setup()
    {
        _tempSettingsDir = Path.Combine(Path.GetTempPath(), "KTools_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempSettingsDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (SettingsManager manager in _managers)
        {
            try
            {
                manager.Dispose();
            }
            catch (Exception)
            {
                // Освобождение не должно ронять очистку теста.
            }
        }

        _managers.Clear();

        if (Directory.Exists(_tempSettingsDir))
        {
            Directory.Delete(_tempSettingsDir, true);
        }
    }

    /// <summary>
    /// Проверяет, что конструктор с null бросает ArgumentNullException.
    /// </summary>
    [TestMethod]
    public void Constructor_NullSettings_ThrowsArgumentNullException()
    {
        // Act
        Action act = () => new DemuxScrollTipTracker(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// Проверяет, что до первого показа флаг считается непоказанным.
    /// </summary>
    [TestMethod]
    public void HasBeenShown_WhenFlagNotPersisted_ReturnsFalse()
    {
        // Arrange
        var tracker = new DemuxScrollTipTracker(MockBuilders.CreateSettingsManagerMock().Object);

        // Act
        bool shown = tracker.HasBeenShown();

        // Assert
        shown.Should().BeFalse("подсказка ещё ни разу не показывалась");
    }

    /// <summary>
    /// Проверяет, что отметка показа сохраняется типизированным вызовом SetSetting,
    /// а не остаётся только в памяти элемента управления.
    /// </summary>
    [TestMethod]
    public void MarkAsShown_WhenCalled_PersistsFlagInSettings()
    {
        // Arrange
        var settingsMock = MockBuilders.CreateSettingsManagerMock();
        var tracker = new DemuxScrollTipTracker(settingsMock.Object);

        // Act
        tracker.MarkAsShown();

        // Assert
        settingsMock.Verify(
            m => m.SetSetting("General", "DemuxScrollTipShown", true),
            Times.Once,
            "факт показа обязан сохраняться в настройках, а не только в памяти");
    }

    /// <summary>
    /// Воспроизводит баг ишью #77: после отметки показа новый экземпляр трекера
    /// (пересозданный контрол при переходе между скриптами) обязан видеть флаг.
    /// Старый подход с полем в памяти возвращал бы здесь false.
    /// </summary>
    [TestMethod]
    public void HasBeenShown_WhenFlagPersistedByPreviousInstance_ReturnsTrue()
    {
        // Arrange: общее хранилище настроек за моком.
        bool stored = false;
        var settingsMock = MockBuilders.CreateSettingsManagerMock();
        settingsMock.Setup(m => m.GetSetting("General", "DemuxScrollTipShown", false))
            .Returns(() => stored);
        settingsMock.Setup(m => m.SetSetting("General", "DemuxScrollTipShown", true))
            .Callback(() => stored = true)
            .Returns(PersistenceResult.Succeeded(new[] { "General/DemuxScrollTipShown" }));

        var first = new DemuxScrollTipTracker(settingsMock.Object);
        first.MarkAsShown();

        // Act: контрол пересоздан, работает новый экземпляр трекера на тех же настройках.
        var second = new DemuxScrollTipTracker(settingsMock.Object);

        // Assert
        second.HasBeenShown().Should().BeTrue("флаг обязан переживать пересоздание элемента управления");
    }

    /// <summary>
    /// Проверяет, что флаг переживает перезапуск приложения: значение, записанное
    /// на диск, читается новым экземпляром менеджера настроек из того же каталога.
    /// </summary>
    [TestMethod]
    public void HasBeenShown_AfterManagerRecreation_ReturnsTrue()
    {
        // Arrange
        var pathManagerMock = new Mock<IPathManager>();
        pathManagerMock.Setup(pm => pm.GetSettingsDirectory()).Returns(_tempSettingsDir);

        SettingsManager first = CreateManager(pathManagerMock.Object);
        var firstTracker = new DemuxScrollTipTracker(first);
        firstTracker.MarkAsShown();
        first.SaveSettings().IsSuccess.Should().BeTrue("флаг обязан записаться на диск");
        first.Dispose();

        // Act: перезапуск приложения — новый менеджер на том же каталоге настроек.
        SettingsManager second = CreateManager(pathManagerMock.Object);
        var secondTracker = new DemuxScrollTipTracker(second);

        // Assert
        secondTracker.HasBeenShown().Should().BeTrue("флаг обязан переживать перезапуск приложения");
    }

    private SettingsManager CreateManager(IPathManager pathManager)
    {
        var manager = new SettingsManager(new RecordingLogService(), pathManager);
        _managers.Add(manager);
        return manager;
    }
}
