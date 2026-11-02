// -*- coding: utf-8 -*-
using System.Collections.Generic;
using System.Linq;

using KTools_App.Services.Contracts;
using KTools_App.UI.Pages;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using FluentAssertions;

namespace KTools.App.Tests;

[TestClass]
public class TimingCalculatorHistoryTests
{
    private static TimingHistoryEntry MakeEntry(string before, string after = "0:00:00.000")
    {
        return new TimingHistoryEntry
        {
            Before = before,
            After = after,
            ShiftAegisub = "0:00:00.00",
            ShiftMs = 0,
            Direction = "Совпадает",
        };
    }

    [TestMethod]
    [DataRow(1L, "Вперёд")]
    [DataRow(1500L, "Вперёд")]
    [DataRow(-1L, "Назад")]
    [DataRow(-60000L, "Назад")]
    [DataRow(0L, "Совпадает")]
    public void GetDirectionLabel_DiffValue_ReturnsExpectedLabel(long diffMs, string expected)
    {
        Assert.AreEqual(expected, TimingCalculatorPage.GetDirectionLabel(diffMs));
    }

    [TestMethod]
    public void BuildHistoryEntry_ValidTimes_ReturnsShiftAndDirection()
    {
        // Arrange & Act.
        TimingHistoryEntry entry = TimingCalculatorPage.BuildHistoryEntry("0:00:01.000", "0:00:05.000");

        // Assert.
        Assert.AreEqual("0:00:01.000", entry.Before);
        Assert.AreEqual("0:00:05.000", entry.After);
        Assert.AreEqual(4000L, entry.ShiftMs);
        Assert.AreEqual("0:00:04.00", entry.ShiftAegisub);
        Assert.AreEqual("Вперёд", entry.Direction);
        Assert.AreEqual("4000 ms", entry.ShiftMsText);
    }

    [TestMethod]
    public void BuildHistoryEntry_EqualTimes_ReturnsMatchWithZeroShift()
    {
        TimingHistoryEntry entry = TimingCalculatorPage.BuildHistoryEntry("0:00:01.000", "0:00:01.000");

        Assert.AreEqual(0L, entry.ShiftMs);
        Assert.AreEqual("0:00:00.00", entry.ShiftAegisub);
        Assert.AreEqual("Совпадает", entry.Direction);
    }

    [TestMethod]
    public void BuildHistoryEntry_NullInput_ThrowsArgumentNullException()
    {
        System.Action first = () => TimingCalculatorPage.BuildHistoryEntry(null!, "0:00:00.000");
        System.Action second = () => TimingCalculatorPage.BuildHistoryEntry("0:00:00.000", null!);
        first.Should().Throw<ArgumentNullException>();
        second.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void PushHistoryEntry_EmptyHistory_AddsEntryFirst()
    {
        TimingHistoryEntry entry = MakeEntry("0:00:01.000");

        List<TimingHistoryEntry> result = TimingCalculatorPage.PushHistoryEntry(null, entry);

        Assert.HasCount(1, result);
        Assert.AreSame(entry, result[0]);
    }

    [TestMethod]
    public void PushHistoryEntry_DuplicateOfNewest_SkipsDuplicate()
    {
        var current = new List<TimingHistoryEntry> { MakeEntry("0:00:01.000"), MakeEntry("0:00:02.000") };

        List<TimingHistoryEntry> result = TimingCalculatorPage.PushHistoryEntry(current, MakeEntry("0:00:01.000"));

        Assert.HasCount(2, result);
        Assert.AreEqual("0:00:01.000", result[0].Before);
    }

    [TestMethod]
    public void PushHistoryEntry_OlderDuplicate_AddsAsNewest()
    {
        var current = new List<TimingHistoryEntry> { MakeEntry("0:00:01.000"), MakeEntry("0:00:02.000") };

        List<TimingHistoryEntry> result = TimingCalculatorPage.PushHistoryEntry(current, MakeEntry("0:00:02.000"));

        Assert.HasCount(3, result);
        Assert.AreEqual("0:00:02.000", result[0].Before);
    }

    [TestMethod]
    public void PushHistoryEntry_OverLimit_DropsOldest()
    {
        List<TimingHistoryEntry> current = Enumerable.Range(0, 20).Select(i => MakeEntry("запись " + i)).ToList();
        var fresh = MakeEntry("новая");

        List<TimingHistoryEntry> result = TimingCalculatorPage.PushHistoryEntry(current, fresh);

        Assert.HasCount(20, result);
        Assert.AreEqual("новая", result[0].Before);
        Assert.IsFalse(result.Any(e => e.Before == "запись 19"));
        Assert.IsTrue(result.Any(e => e.Before == "запись 0"));
    }

    [TestMethod]
    public void PushHistoryEntry_NullEntry_ThrowsArgumentNullException()
    {
        System.Action act = () => TimingCalculatorPage.PushHistoryEntry(new List<TimingHistoryEntry>(), null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void LoadHistory_NoStoredValue_ReturnsEmpty()
    {
        var settingsMock = new Mock<ISettingsManager>();
        settingsMock.Setup(s => s.GetSetting(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<TimingHistoryEntry>?>()))
            .Returns((List<TimingHistoryEntry>?)null);

        List<TimingHistoryEntry> result = TimingCalculatorPage.LoadHistory(settingsMock.Object);

        Assert.IsEmpty(result);
    }

    [TestMethod]
    public void LoadHistory_StoredList_ReturnsTrimmedNewestFirst()
    {
        List<TimingHistoryEntry> stored = Enumerable.Range(0, 21).Select(i => MakeEntry("запись " + i)).ToList();
        var settingsMock = new Mock<ISettingsManager>();
        settingsMock.Setup(s => s.GetSetting(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<TimingHistoryEntry>?>()))
            .Returns(stored);

        List<TimingHistoryEntry> result = TimingCalculatorPage.LoadHistory(settingsMock.Object);

        Assert.HasCount(20, result);
        Assert.AreEqual("запись 0", result[0].Before);
    }

    [TestMethod]
    public void LoadHistory_InvalidEntries_SkipsThem()
    {
        var stored = new List<TimingHistoryEntry> { MakeEntry(string.Empty), MakeEntry("0:00:01.000") };
        var settingsMock = new Mock<ISettingsManager>();
        settingsMock.Setup(s => s.GetSetting(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<TimingHistoryEntry>?>()))
            .Returns(stored);

        List<TimingHistoryEntry> result = TimingCalculatorPage.LoadHistory(settingsMock.Object);

        Assert.HasCount(1, result);
        Assert.AreEqual("0:00:01.000", result[0].Before);
    }

    [TestMethod]
    public void LoadHistory_NullSettings_ThrowsArgumentNullException()
    {
        System.Action act = () => TimingCalculatorPage.LoadHistory(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void SaveHistory_OverLimit_WritesTrimmedListToSettings()
    {
        List<TimingHistoryEntry> history = Enumerable.Range(0, 21).Select(i => MakeEntry("запись " + i)).ToList();
        var settingsMock = new Mock<ISettingsManager>();

        TimingCalculatorPage.SaveHistory(settingsMock.Object, history);

        settingsMock.Verify(s => s.SetSetting(
            It.Is<string>(g => g == TimingCalculatorPage.HistorySettingsGroup),
            It.Is<string>(k => k == TimingCalculatorPage.HistorySettingsKey),
            It.Is<List<TimingHistoryEntry>>(l => l.Count == 20)), Times.Once);
    }

    [TestMethod]
    public void SaveHistory_NullSettings_ThrowsArgumentNullException()
    {
        System.Action act = () => TimingCalculatorPage.SaveHistory(null!, new List<TimingHistoryEntry>());
        act.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    [DataRow("Вперёд", true, false, false)]
    [DataRow("Назад", false, true, false)]
    [DataRow("Совпадает", false, false, true)]
    public void DirectionFlags_DirectionValue_MatchesExpectedFlags(string direction, bool forward, bool backward, bool match)
    {
        var entry = new TimingHistoryEntry { Direction = direction };

        Assert.AreEqual(forward, entry.IsForward);
        Assert.AreEqual(backward, entry.IsBackward);
        Assert.AreEqual(match, entry.IsMatch);
    }

    [TestMethod]
    public void SaveHistory_AfterEntryRemoved_WritesShortenedListToSettings()
    {
        var history = new List<TimingHistoryEntry> { MakeEntry("0:00:01.000"), MakeEntry("0:00:02.000") };
        history.RemoveAt(0);
        var settingsMock = new Mock<ISettingsManager>();

        TimingCalculatorPage.SaveHistory(settingsMock.Object, history);

        settingsMock.Verify(s => s.SetSetting(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.Is<List<TimingHistoryEntry>>(l => l.Count == 1 && l[0].Before == "0:00:02.000")), Times.Once);
    }
}
