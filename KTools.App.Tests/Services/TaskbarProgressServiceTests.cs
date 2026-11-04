// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using FluentAssertions;
using KTools_App.Services.Contracts;
using KTools_App.Services.Implementations;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests;

/// <summary>
/// Юнит-тесты службы прогресса панели задач.
/// Проверяют обрезку значений, состояния Normal/Error/NoProgress,
/// троттлинг частых обновлений и безопасный пропуск без окна.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class TaskbarProgressServiceTests
{
    private Mock<IWindowHandleProvider> _handleMock = null!;
    private Mock<ILogService> _logMock = null!;

    [TestInitialize]
    public void Setup()
    {
        _handleMock = new Mock<IWindowHandleProvider>();
        _handleMock.Setup(h => h.GetMainWindowHandle()).Returns(new IntPtr(12345));
        _logMock = MockBuilders.CreateLogServiceMock();
    }

    /// <summary>
    /// Тестовый двойник, записывающий вызовы COM без обращения к панели задач.
    /// </summary>
    private sealed class RecordingTaskbarService : TaskbarProgressService
    {
        public List<(ulong Completed, ulong Total, int State)> Calls { get; } = new();

        public RecordingTaskbarService(
            IWindowHandleProvider handleProvider,
            ILogService logService,
            Func<int> tickProvider)
            : base(handleProvider, logService, tickProvider)
        {
        }

        protected override void ApplyProgress(IntPtr hwnd, ulong completed, ulong total, int state)
        {
            hwnd.Should().NotBe(IntPtr.Zero);
            Calls.Add((completed, total, state));
        }
    }

    private RecordingTaskbarService CreateService(int startTick = 1000)
    {
        int tick = startTick;
        return new RecordingTaskbarService(_handleMock.Object, _logMock.Object, () => tick);
    }

    [TestMethod]
    public void SetProgress_ValidPercent_AppliesNormalState()
    {
        // Arrange
        var service = CreateService();

        // Act
        service.SetProgress(42.5);

        // Assert
        service.Calls.Should().ContainSingle();
        var call = service.Calls[0];
        call.Total.Should().Be(1000UL);
        call.Completed.Should().Be(425UL);
        call.State.Should().Be(2);
    }

    [TestMethod]
    public void SetProgress_OutOfRangePercent_ClampsToBounds()
    {
        // Arrange
        var lowTick = 1000;
        var low = new RecordingTaskbarService(_handleMock.Object, _logMock.Object, () => lowTick);
        var highTick = 1000;
        var high = new RecordingTaskbarService(_handleMock.Object, _logMock.Object, () => highTick);

        // Act
        low.SetProgress(-10.0);
        high.SetProgress(150.0);

        // Assert
        low.Calls.Should().ContainSingle().Which.Completed.Should().Be(0UL);
        high.Calls.Should().ContainSingle().Which.Completed.Should().Be(1000UL);
    }

    [TestMethod]
    public void SetProgress_NonNumericPercent_TreatsAsZero()
    {
        // Arrange
        var service = CreateService();

        // Act
        service.SetProgress(double.NaN);

        // Assert
        service.Calls.Should().ContainSingle().Which.Completed.Should().Be(0UL);
    }

    [TestMethod]
    public void SetError_FailedQueue_AppliesErrorState()
    {
        // Arrange
        var service = CreateService();

        // Act
        service.SetProgress(50.0);
        service.SetError(100.0);

        // Assert
        service.Calls.Should().HaveCount(2);
        service.Calls[1].State.Should().Be(4);
        service.Calls[1].Completed.Should().Be(1000UL);
    }

    [TestMethod]
    public void Clear_AfterProgress_ResetsToNoProgress()
    {
        // Arrange
        var service = CreateService();

        // Act
        service.SetProgress(50.0);
        service.Clear();

        // Assert
        service.Calls.Should().HaveCount(2);
        service.Calls[1].State.Should().Be(0);
        service.Calls[1].Completed.Should().Be(0UL);
    }

    [TestMethod]
    public void Clear_RepeatedCalls_EmitsOnlyOnce()
    {
        // Arrange
        var service = CreateService();

        // Act
        service.Clear();
        service.Clear();

        // Assert
        service.Calls.Should().BeEmpty("повторный сброс без изменений не должен обращаться к COM");
    }

    [TestMethod]
    public void SetProgress_RapidCalls_ThrottlesIntermediateUpdates()
    {
        // Arrange
        int tick = 1000;
        var service = new RecordingTaskbarService(_handleMock.Object, _logMock.Object, () => tick);

        // Act
        service.SetProgress(10.0);
        tick += 10;
        service.SetProgress(20.0);
        tick += 200;
        service.SetProgress(30.0);

        // Assert
        service.Calls.Should().HaveCount(2, "промежуточное обновление внутри окна троттлинга пропускается");
        service.Calls[0].Completed.Should().Be(100UL);
        service.Calls[1].Completed.Should().Be(300UL);
    }

    [TestMethod]
    public void SetProgress_ZeroHandle_SkipsComWithoutThrowing()
    {
        // Arrange
        _handleMock.Setup(h => h.GetMainWindowHandle()).Returns(IntPtr.Zero);
        var service = CreateService();

        // Act
        Action act = () =>
        {
            service.SetProgress(50.0);
            service.SetError(100.0);
            service.Clear();
        };

        // Assert
        act.Should().NotThrow();
        service.Calls.Should().BeEmpty();
    }

    [TestMethod]
    public void SetProgress_HandleProviderThrows_SwollowsAndDisablesFurtherCalls()
    {
        // Arrange
        _handleMock.Setup(h => h.GetMainWindowHandle()).Throws(new InvalidOperationException("Окно не готово"));
        var service = CreateService();

        // Act
        Action act = () =>
        {
            service.SetProgress(50.0);
            service.SetProgress(60.0);
        };

        // Assert
        act.Should().NotThrow();
        service.Calls.Should().BeEmpty();
    }
}
