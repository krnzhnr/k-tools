// -*- coding: utf-8 -*-
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KTools_App.UI.Pages;

namespace KTools.App.Tests;

[TestClass]
public class TimingCalculatorTests
{
    [TestMethod]
    [DataRow("0:00:00.000", 0L)]
    [DataRow("0:00:01.000", 1000L)]
    [DataRow("0:00:00.005", 5L)]
    [DataRow("0:00:00.050", 50L)]
    [DataRow("1:23:45.678", 5025678L)]
    [DataRow("0:02:44.200", 164200L)]
    [DataRow("0:00:00.00", 0L)]
    [DataRow("0:00:01.00", 1000L)]
    [DataRow("0:00:00.05", 50L)]
    [DataRow("1:23:45.67", 5025670L)] // (1*3600 + 23*60 + 45)*1000 + 67*10 = 5025670 ms
    [DataRow("0:02:44.20", 164200L)]
    public void ParseTimeToMs_WithValidFormat_ReturnsExpectedMs(string timeStr, long expectedMs)
    {
        long actualMs = TimingCalculatorPage.ParseTimeToMs(timeStr);
        Assert.AreEqual(expectedMs, actualMs);
    }

    [TestMethod]
    [DataRow(0L, "0:00:00.00")]
    [DataRow(1000L, "0:00:01.00")]
    [DataRow(50L, "0:00:00.05")]
    [DataRow(5025678L, "1:23:45.67")]
    [DataRow(164200L, "0:02:44.20")]
    [DataRow(-164200L, "0:02:44.20")] // Абсолютное значение
    [DataRow(999L, "0:00:00.99")] // Усечение до сотых (не округление)
    [DataRow(1234L, "0:00:01.23")]
    [DataRow(1999L, "0:00:01.99")]
    public void FormatMsToAegisub_WithMs_ReturnsExpectedFormat(long ms, string expectedStr)
    {
        string actualStr = TimingCalculatorPage.FormatMsToAegisub(ms);
        Assert.AreEqual(expectedStr, actualStr);
    }

    [TestMethod]
    [DataRow("0:01:23.456", "0:01:23.456")]
    [DataRow("0:01:23,456", "0:01:23.456")]
    [DataRow("00:01:23.456", "0:01:23.456")]
    [DataRow("01:23.456", "0:01:23.456")]
    [DataRow("0:01:23.45", "0:01:23.450")]
    [DataRow("00:01:23.45", "0:01:23.450")]
    [DataRow("01:23.45", "0:01:23.450")]
    [DataRow("0:00:00.000", "0:00:00.000")]
    [DataRow("invalid", null)]
    public void NormalizeTimeText_VariousFormats_ReturnsNormalizedOrNull(string input, string? expected)
    {
        string? actual = TimingCalculatorPage.NormalizeTimeText(input);
        Assert.AreEqual(expected, actual);
    }
}
