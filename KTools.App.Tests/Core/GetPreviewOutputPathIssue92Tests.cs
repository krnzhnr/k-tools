// -*- coding: utf-8 -*-
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using KTools_App.Tests.TestHelpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KTools_App.Tests.Core;

/// <summary>
/// Юнит-тесты предпросмотра выходного пути (issue #92).
/// Превью обязано учитывать автопапку и занятые имена батча,
/// не меняя при этом состояние скрипта и файловой системы.
/// Все комментарии выполнены на русском языке.
/// </summary>
[TestClass]
public class GetPreviewOutputPathIssue92Tests
{
    private TempDirectoryScope _scope = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _scope = new TempDirectoryScope();
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _scope.Dispose();
    }

    private static StubScript CreateScript(bool useAutoSubfolder, string subfolderName = "KTools_Result")
    {
        var settingsMock = MockBuilders.CreateSettingsManagerMock();
        settingsMock.Object.UseAutoSubfolder = useAutoSubfolder;
        settingsMock.Object.DefaultOutputSubfolder = subfolderName;
        return new StubScript(
            MockBuilders.CreateLogServiceMock().Object,
            settingsMock.Object,
            MockBuilders.CreatePathManagerMock().Object);
    }

    [TestMethod]
    public void GetPreviewOutputPath_AutoSubfolderEnabled_ReturnsPathInSubfolder()
    {
        var script = CreateScript(useAutoSubfolder: true);
        string input = _scope.CreateFile("video.mkv", "data");

        string preview = script.GetPreviewOutputPath(input, input, 1);

        preview.Should().Be(Path.Combine(_scope.RootPath, "KTools_Result", "video.mkv"));
    }

    [TestMethod]
    public void GetPreviewOutputPath_AutoSubfolderEnabled_DoesNotCreateSubfolder()
    {
        var script = CreateScript(useAutoSubfolder: true);
        string input = _scope.CreateFile("video.mkv", "data");

        script.GetPreviewOutputPath(input, input, 1);

        Directory.Exists(Path.Combine(_scope.RootPath, "KTools_Result")).Should().BeFalse();
    }

    [TestMethod]
    public void GetPreviewOutputPath_AutoSubfolderDisabled_ReturnsPathInInputDir()
    {
        var script = CreateScript(useAutoSubfolder: false);
        string input = _scope.CreateFile("video.mkv", "data");

        string preview = script.GetPreviewOutputPath(input, input, 1);

        preview.Should().Be(Path.Combine(_scope.RootPath, "video_processed.mkv"));
    }

    [TestMethod]
    public void GetPreviewOutputPath_ReservedOutputCollision_ReturnsSuffixedPath()
    {
        var script = CreateScript(useAutoSubfolder: true);
        string input = _scope.CreateFile("video.mkv", "data");
        script.PrepareBatch(new List<string> { Path.Combine(_scope.RootPath, "KTools_Result", "video.mkv") });

        string preview = script.GetPreviewOutputPath(input, input, 1);

        preview.Should().Be(Path.Combine(_scope.RootPath, "KTools_Result", "video_1.mkv"));
    }

    [TestMethod]
    public void GetPreviewOutputPath_RepeatedCalls_ReturnsSamePath()
    {
        var script = CreateScript(useAutoSubfolder: true);
        string input = _scope.CreateFile("video.mkv", "data");

        string first = script.GetPreviewOutputPath(input, input, 1);
        string second = script.GetPreviewOutputPath(input, input, 1);

        second.Should().Be(first);
    }

    [TestMethod]
    public void GetPreviewOutputPath_AutoSubfolderEnabledWithCustomDir_KeepsCustomDir()
    {
        var script = CreateScript(useAutoSubfolder: true);
        string input = _scope.CreateFile("video.mkv", "data");
        string customDir = _scope.GetFullPath("custom");
        Directory.CreateDirectory(customDir);
        string output = Path.Combine(customDir, "video.mkv");

        string preview = script.GetPreviewOutputPath(input, output, 1);

        preview.Should().Be(output);
    }

    [TestMethod]
    public void GetPreviewOutputPath_EmptySubfolderName_FallsBackToDefault()
    {
        var script = CreateScript(useAutoSubfolder: true, subfolderName: string.Empty);
        string input = _scope.CreateFile("video.mkv", "data");

        string preview = script.GetPreviewOutputPath(input, input, 1);

        preview.Should().Be(Path.Combine(_scope.RootPath, "KTools_Result", "video.mkv"));
    }
}
