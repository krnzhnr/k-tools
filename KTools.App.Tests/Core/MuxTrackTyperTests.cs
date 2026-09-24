// -*- coding: utf-8 -*-
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using KTools_App.Core;

namespace KTools_App.Tests.Core;

/// <summary>
/// Юнит-тесты для определения роли субтитров по суффиксу MuxTrackTyper.
/// Все комментарии написаны на русском языке.
/// </summary>
[TestClass]
public class MuxTrackTyperTests
{
    /// <summary>
    /// Проверяет, что точное совпадение имени считается полными субтитрами.
    /// </summary>
    [TestMethod]
    public void GetSubsRole_ExactMatch_ReturnsFull()
    {
        MuxTrackTyper.GetSubsRole("video", "video").Should().Be(MuxSubsRole.Full);
    }

    /// <summary>
    /// Проверяет распознавание надписей по суффиксам .signs и смежным меткам.
    /// </summary>
    [TestMethod]
    public void GetSubsRole_SignsSuffix_ReturnsSigns()
    {
        MuxTrackTyper.GetSubsRole("video", "video.signs").Should().Be(MuxSubsRole.Signs);
        MuxTrackTyper.GetSubsRole("video", "video.sign").Should().Be(MuxSubsRole.Signs);
        MuxTrackTyper.GetSubsRole("s01e01", "s01e01.songs").Should().Be(MuxSubsRole.Signs);
        MuxTrackTyper.GetSubsRole("video", "VIDEO.SIGNS").Should().Be(MuxSubsRole.Signs);
    }

    /// <summary>
    /// Проверяет распознавание полных субтитров по суффиксу .full.
    /// </summary>
    [TestMethod]
    public void GetSubsRole_FullSuffix_ReturnsFull()
    {
        MuxTrackTyper.GetSubsRole("video", "video.full").Should().Be(MuxSubsRole.Full);
        MuxTrackTyper.GetSubsRole("s01e01", "s01e01.ru.full").Should().Be(MuxSubsRole.Full);
    }

    /// <summary>
    /// Проверяет, что языковые и служебные суффиксы считаются прочими субтитрами.
    /// </summary>
    [TestMethod]
    public void GetSubsRole_LanguageOrForcedSuffix_ReturnsOther()
    {
        MuxTrackTyper.GetSubsRole("video", "video.en").Should().Be(MuxSubsRole.Other);
        MuxTrackTyper.GetSubsRole("video", "video.ru.forced").Should().Be(MuxSubsRole.Other);
        MuxTrackTyper.GetSubsRole("video", "video.dub").Should().Be(MuxSubsRole.Other);
        MuxTrackTyper.GetSubsRole("video", "other").Should().Be(MuxSubsRole.Other);
    }

    /// <summary>
    /// Проверяет порядок сортировки: надписи, полные, прочие.
    /// </summary>
    [TestMethod]
    public void GetRoleOrder_SignsFullOther_ReturnsAscending()
    {
        MuxTrackTyper.GetRoleOrder(MuxSubsRole.Signs).Should().BeLessThan(MuxTrackTyper.GetRoleOrder(MuxSubsRole.Full));
        MuxTrackTyper.GetRoleOrder(MuxSubsRole.Full).Should().BeLessThan(MuxTrackTyper.GetRoleOrder(MuxSubsRole.Other));
    }

    /// <summary>
    /// Проверяет, что ручной выбор роли приоритетнее автоопределения по суффиксу.
    /// </summary>
    [TestMethod]
    public void ResolveSubsRole_ManualOverride_BeatsSuffix()
    {
        MuxTrackTyper.ResolveSubsRole("video", "video.en", MuxSubsRole.Signs).Should().Be(MuxSubsRole.Signs);
        MuxTrackTyper.ResolveSubsRole("video", "video.signs", MuxSubsRole.Full).Should().Be(MuxSubsRole.Full);
        MuxTrackTyper.ResolveSubsRole("video", "video.en", null).Should().Be(MuxSubsRole.Other);
    }

    /// <summary>
    /// Проверяет вывод заголовка дорожки из различающейся части имени.
    /// </summary>
    [TestMethod]
    public void InferTrackTitle_DistinctSuffix_ReturnsTitle()
    {
        MuxTrackTyper.InferTrackTitle("s01e01", "s01e01.LostFilm").Should().Be("LostFilm");
        MuxTrackTyper.InferTrackTitle("s01e01", "s01e01.Kuraj.Bambey").Should().Be("Kuraj Bambey");
        MuxTrackTyper.InferTrackTitle("s01e01", "s01e01").Should().BeEmpty();
        MuxTrackTyper.InferTrackTitle("s01e01", "s01e01.en").Should().BeEmpty();
        MuxTrackTyper.InferTrackTitle("s01e01", "s01e01.ru.full").Should().Be("ru full");
    }

    /// <summary>
    /// Проверяет, что языковые токены не распознаются как роль.
    /// </summary>
    [TestMethod]
    public void GetSubsRole_LanguageTokens_ReturnsOther()
    {
        MuxTrackTyper.GetSubsRole("video", "video.en").Should().Be(MuxSubsRole.Other);
        MuxTrackTyper.GetSubsRole("video", "video.deu.dub").Should().Be(MuxSubsRole.Other);
    }
}
