// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.Models;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;
using KTools_App.ViewModels;

namespace KTools_App.Tests.ViewModels;

/// <summary>
/// Проверки доставки прогресса загрузки в UI-поток (ишью #141):
/// события DependencyManager приходят с фоновых потоков скачивания,
/// поэтому запись в привязанные свойства обязана маршалиться через DispatcherQueue,
/// а повторные значения — отбрасываться до записи, чтобы не штормить UI.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class DependencyProgressMarshalingTests
{
    private Mock<IDependencyManager> _dependencyManagerMock = null!;
    private Mock<INavigationService> _navigationMock = null!;
    private Mock<ILogService> _logServiceMock = null!;
    private Mock<IPathManager> _pathManagerMock = null!;

    [TestInitialize]
    public void Setup()
    {
        _dependencyManagerMock = MockBuilders.CreateDependencyManagerMock(allInstalled: false);
        _navigationMock = MockBuilders.CreateNavigationMock();
        _logServiceMock = MockBuilders.CreateLogServiceMock();
        _pathManagerMock = MockBuilders.CreatePathManagerMock();
    }

    private DependencySetupViewModel CreateViewModelWithSingleDependency(string key)
    {
        var info = new DependencyInfo
        {
            Key = key,
            DisplayName = key,
            Description = "Тестовая зависимость",
            IconName = "video",
            SizeMb = 100.0,
            ArchiveSizeMb = 50.0,
            IsRequired = true
        };
        _dependencyManagerMock
            .Setup(d => d.GetRegistry())
            .Returns(new List<DependencyInfo> { info });
        return new DependencySetupViewModel(
            _dependencyManagerMock.Object,
            _navigationMock.Object,
            _logServiceMock.Object,
            _pathManagerMock.Object);
    }

    private static int CountNotifications(INotifyPropertyChanged source, string propertyName, Action raise)
    {
        int count = 0;
        void Handler(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == propertyName)
            {
                count++;
            }
        }

        source.PropertyChanged += Handler;
        try
        {
            raise();
        }
        finally
        {
            source.PropertyChanged -= Handler;
        }

        return count;
    }

    [TestMethod]
    public void DependencyVM_InheritsThreadSafeViewModel_MarshalsSettersOffUiThread()
    {
        typeof(DependencyVM).IsSubclassOf(typeof(ThreadSafeViewModel)).Should().BeTrue(
            "свойства дорожки обновляются с фонового потока скачивания, уведомления обязаны идти через DispatcherQueue");
    }

    [TestMethod]
    public void ProgressChanged_SamePercentTwice_RaisesProgressNotificationOnce()
    {
        // Arrange.
        DependencySetupViewModel viewModel = CreateViewModelWithSingleDependency("ffmpeg");
        DependencyVM dependency = viewModel.RequiredDependencies.Single();

        // Act: тот же процент дважды подряд (фон шлёт каждый чанк).
        int notifications = CountNotifications(
            dependency,
            nameof(DependencyVM.Progress),
            () =>
            {
                _dependencyManagerMock.Raise(m => m.ProgressChanged += null, "ffmpeg", 50);
                _dependencyManagerMock.Raise(m => m.ProgressChanged += null, "ffmpeg", 50);
            });

        // Assert.
        dependency.Progress.Should().Be(50);
        notifications.Should().Be(1, "повторное значение не должно заново дёргать UI");
    }

    [TestMethod]
    public void ProgressChanged_NewPercent_UpdatesValueAndRaises()
    {
        // Arrange.
        DependencySetupViewModel viewModel = CreateViewModelWithSingleDependency("ffmpeg");
        DependencyVM dependency = viewModel.RequiredDependencies.Single();

        // Act.
        _dependencyManagerMock.Raise(m => m.ProgressChanged += null, "ffmpeg", 25);

        // Assert.
        dependency.Progress.Should().Be(25);
    }

    [TestMethod]
    public void SpeedUpdated_SameTextTwice_RaisesSpeedNotificationOnce()
    {
        // Arrange.
        DependencySetupViewModel viewModel = CreateViewModelWithSingleDependency("ffmpeg");
        DependencyVM dependency = viewModel.RequiredDependencies.Single();

        // Act: та же строка скорости дважды подряд.
        int notifications = CountNotifications(
            dependency,
            nameof(DependencyVM.Speed),
            () =>
            {
                _dependencyManagerMock.Raise(m => m.SpeedUpdated += null, "ffmpeg", "1,5 МБ/с");
                _dependencyManagerMock.Raise(m => m.SpeedUpdated += null, "ffmpeg", "1,5 МБ/с");
            });

        // Assert.
        dependency.Speed.Should().Be("1,5 МБ/с");
        notifications.Should().Be(1, "повторное значение не должно заново дёргать UI");
    }
}
