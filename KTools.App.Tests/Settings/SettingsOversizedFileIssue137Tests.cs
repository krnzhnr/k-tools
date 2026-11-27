// -*- coding: utf-8 -*-
using System;
using System.IO;
using FluentAssertions;
using Moq;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.Tests.TestHelpers;

namespace KTools_App.Tests.Settings;

/// <summary>
/// Проверка issue #137: чтение раздутого settings.json не должно приводить к OOM.
/// Файл больше лимита записи 8МБ переходит в деградированную загрузку TooLarge.
/// </summary>
[TestClass]
public class SettingsOversizedFileIssue137Tests
{
    private const int SettingsLimitBytes = 8 * 1024 * 1024;

    private Mock<IPathManager> _pathManagerMock = null!;
    private RecordingLogService _logService = null!;
    private string _tempSettingsDir = null!;
    private string _tempSettingsFile = null!;
    private SettingsManager? _manager;

    [TestInitialize]
    public void Setup()
    {
        _logService = new RecordingLogService();
        _pathManagerMock = new Mock<IPathManager>();
        _tempSettingsDir = Path.Combine(Path.GetTempPath(), "KTools_Oversized137_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempSettingsDir);
        _tempSettingsFile = Path.Combine(_tempSettingsDir, "settings.json");
        _pathManagerMock.Setup(pm => pm.GetSettingsDirectory()).Returns(_tempSettingsDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            _manager?.Dispose();
        }
        catch (Exception)
        {
        }
        finally
        {
            _manager = null;
        }

        if (Directory.Exists(_tempSettingsDir))
        {
            Directory.Delete(_tempSettingsDir, true);
        }
    }

    [TestMethod]
    public void LoadSettings_OversizedFile_ReturnsTooLargeDegraded()
    {
        // Arrange: файл больше лимита 8МБ, содержимое не читается.
        using (var stream = new FileStream(_tempSettingsFile, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(SettingsLimitBytes + 1024);
        }

        long sizeBefore = new FileInfo(_tempSettingsFile).Length;
        sizeBefore.Should().BeGreaterThan(SettingsLimitBytes);

        // Act: конструктор и повторная загрузка оба упираются в лимит длины.
        _manager = new SettingsManager(_logService, _pathManagerMock.Object);
        PersistenceResult result = _manager.LoadSettings();

        // Assert: деградированная загрузка с кодом зеркально лимиту записи.
        result.IsFailure.Should().BeTrue();
        result.IsDegraded.Should().BeTrue();
        result.ErrorCode.Should().Be(PersistenceErrorCodes.SnapshotTooLarge);

        // Резервная копия не создаётся: содержимое не повреждено, а слишком велико.
        Directory.GetFiles(_tempSettingsDir, "*.bak").Should().BeEmpty();
        new FileInfo(_tempSettingsFile).Length.Should().Be(sizeBefore);

        // Запись заблокирована защитой деградированной загрузки.
        PersistenceResult blocked = _manager.SetSetting("General", "Theme", "Light");
        blocked.IsFailure.Should().BeTrue();
        blocked.ErrorCode.Should().Be(PersistenceErrorCodes.DegradedLoad);
        new FileInfo(_tempSettingsFile).Length.Should().Be(sizeBefore);

        // В журнале есть событие деградированной загрузки.
        _logService.EventsById(SettingsEventIds.LoadDegraded).Should().NotBeEmpty();
    }
}
