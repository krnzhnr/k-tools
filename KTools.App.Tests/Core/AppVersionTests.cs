// -*- coding: utf-8 -*-
using FluentAssertions;
using KTools_App.Core;

namespace KTools_App.Tests.Core;

/// <summary>
/// Юнит-тесты единого источника версии приложения <see cref="AppVersion"/>:
/// нормализация AssemblyInformationalVersion и формат сабтайтла TitleBar.
/// Все комментарии выполнены на русском языке.
/// </summary>
[TestClass]
public class AppVersionTests
{
    [TestMethod]
    public void Resolve_ReturnsNormalizedVersionWithoutBuildMetadata()
    {
        // Arrange / Act
        string version = AppVersion.Resolve();

        // Assert — короткая версия без хэша коммита после '+'
        version.Should().NotBeNullOrWhiteSpace();
        version.Should().NotContain("+", "суффикс сборки с хэшем коммита не входит в короткую версию");
    }

    [TestMethod]
    public void Resolve_MatchesLogServiceVersionSource()
    {
        // Arrange / Act — журнал обязан читать версию из того же источника,
        // что и UI: дублирование логики чтения версии запрещено
        string expected = LogService.ResolveApplicationVersion();
        string actual = AppVersion.Resolve();

        // Assert
        actual.Should().Be(expected);
    }

    [TestMethod]
    public void ToTitleSubtitle_Always_StartsWithVPrefix()
    {
        // Arrange / Act
        string subtitle = AppVersion.ToTitleSubtitle();

        // Assert — компактный формат бара «v2.2.0», а не «Версия ...»
        subtitle.Should().StartWith("v");
        subtitle.Should().Be($"v{AppVersion.Resolve()}");
    }
}
