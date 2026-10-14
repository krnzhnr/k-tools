// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.Linq;

using CommunityToolkit.WinUI.Controls;

using KTools_App.Core;
using KTools_App.Diagnostics;
using KTools_App.Services.Contracts;
using KTools_App.ViewModels;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

namespace KTools_App.UI.Dialogs;

/// <summary>
/// Диалоговое окно «Что нового»: сверху шапка с GIF-сценой
/// (листание сцен коллекции по кругу с кроссфейдом),
/// под ней краткая выжимка и раскрывающиеся секции изменений.
/// Сцены лежат в Assets/WhatsNew/art, листаются только имеющиеся файлы.
/// </summary>
public sealed class WhatsNewDialog : ContentDialog
{
    private const string SourceName = nameof(WhatsNewDialog);
    private const double ArtHeight = 180;

    /// <summary>
    /// Все GIF-сцены записаны в одном формате: 1280x640.
    /// Проверено по всем файлам папки, поэтому пропорции зашиты
    /// константой вместо чтения заголовков.
    /// </summary>
    private const double ArtAspectRatio = 2.0;
    private const int SwitchTimeoutMs = 3000;
    private const int RevealFallbackMs = 4000;

    private readonly WhatsNewViewModel _viewModel;

    /// <summary>Сцены, для которых есть GIF-файлы, в порядке круга.</summary>
    private readonly IReadOnlyList<string> _availableArt;

    private string _currentPiece = WhatsNewArtCatalog.DefaultPiece;
    private readonly bool _animationsEnabled;

    private Grid _artHeader = null!;
    private Image _frontImage = null!;
    private Image _backImage = null!;
    private Button _nextArtButton = null!;
    private CheckBox _optOutCheckBox = null!;
    private TextBlock _artFallbackLabel = null!;

    /// <summary>
    /// Скролл только списка карточек (шапка фиксирована).
    /// Высота подгоняется под окно, чтобы низ списка не резался.
    /// </summary>
    private ScrollViewer _listScroll = null!;

    /// <summary>Первый кадр уже показан (вуаль снята).</summary>
    private bool _artRevealed;

    /// <summary>
    /// Отмена фоновых continuations шапки при закрытии окна.
    /// </summary>
    private readonly System.Threading.CancellationTokenSource _closeCts = new();

    /// <summary>
    /// Вуаль для затухания/появления шапки. Чистый XAML, анимация идёт
    /// compositor-потоком без подвисаний UI. Эффекта на границе цикла нет
    /// сознательно: вспышка градиентом раздражает сильнее самого стыка.
    /// </summary>
    private Border _veil = null!;
    private Storyboard _veilFadeOut = null!;

    /// <summary>
    /// Инициализирует окно готовыми данными релиза.
    /// </summary>
    public WhatsNewDialog(ReleaseNotes notes)
    {
        ArgumentNullException.ThrowIfNull(notes);

        _viewModel = new WhatsNewViewModel(notes);
        _availableArt = WhatsNewArtCatalog.ListAvailableArt();
        _animationsEnabled = AreAnimationsEnabled();

        if (_availableArt.Count > 0)
        {
            _currentPiece = _availableArt.Contains(_viewModel.ArtPieceName, StringComparer.OrdinalIgnoreCase)
                ? _viewModel.ArtPieceName
                : _availableArt[0];
            _viewModel.ArtPieceName = _currentPiece;
        }

        // Ширина окна намеренно не задаётся: диалог центрируется сам.
        // (Фиксированная ширина смещала окно влево, а полосы при ресайзе
        // остались в прошлом вместе с WebView2 — GIF масштабируется гладко.)
        // Отступов вправо тоже нет: скроллбар занимает своё место сам,
        // при коротком списке окно полностью симметрично.
        // Кнопки диалога свои (подвал ниже), штатная CloseButtonText не задаётся.

        // Корень: шапка фиксирована сверху, скроллится только список.
        var root = new Grid
        {
            MinWidth = 420,
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(BuildArtHeader());

        // Тело — скролл только списка: шапка всегда на месте,
        // низ списка доступен всегда, резать нечего.
        var bodyStack = new StackPanel { Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };

        if (_viewModel.Highlights.Count > 0)
        {
            bodyStack.Children.Add(BuildHighlightsCard());
        }

        for (int i = 0; i < _viewModel.Sections.Count; i++)
        {
            bodyStack.Children.Add(BuildSectionExpander(_viewModel.Sections[i], false));
        }

        _listScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = bodyStack,

            // Ритм формы: 12 между шапкой и списком (бывший Spacing корня).
            Margin = new Thickness(0, 12, 0, 0),
        };
        Grid.SetRow(_listScroll, 1);
        root.Children.Add(_listScroll);
        root.Children.Add(BuildFooter());
        Content = root;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>
    /// Подвал окна: слева флажок «Больше не показывать», справа «Закрыть».
    /// Флажок навсегда отключает автопоказ (включить обратно — тумблером
    /// в настройках обновлений).
    /// </summary>
    private UIElement BuildFooter()
    {
        // Ритм формы: 24 над рядом кнопок. Снизу отступ не нужен —
        // у диалога свой нижний паддинг, иначе двойной подбородок.
        var footer = new Grid { Margin = new Thickness(0, 24, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _optOutCheckBox = new CheckBox
        {
            Content = "Больше не показывать",
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(_optOutCheckBox, "Больше не показывать окно изменений после обновлений");
        Grid.SetColumn(_optOutCheckBox, 0);
        footer.Children.Add(_optOutCheckBox);

        var closeButton = new Button
        {
            Content = "Закрыть",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 120,
        };
        AutomationProperties.SetName(closeButton, "Закрыть окно изменений");
        closeButton.Click += (s, e) =>
        {
            try
            {
                if (_optOutCheckBox.IsChecked == true)
                {
                    App.Services.GetRequiredService<SettingsViewModel>().SetShowWhatsNewAfterUpdateSilent(false);
                    App.Services.GetRequiredService<ILogService>().Write(
                        "settings.whatsnew.optout",
                        LogLevel.Info,
                        LogStatus.Changed,
                        "Автопоказ окна изменений отключён флажком «Больше не показывать»",
                        source: SourceName,
                        properties: LogProps.Create("Group", "Update"));
                }
            }
            catch
            {
                // Игнорируется: отказ флага не должен мешать закрытию окна.
            }

            Hide();
        };
        Grid.SetColumn(closeButton, 1);
        footer.Children.Add(closeButton);

        Grid.SetRow(footer, 2);
        return footer;
    }

    /// <summary>
    /// Шапка с анимацией: под картинками всегда лежит градиент-заглушка,
    /// поэтому при отсутствии GIF-файлов окно выглядит целостно.
    /// Два слоя Image нужны для кроссфейда при переключении сцен.
    /// </summary>
    private UIElement BuildArtHeader()
    {
        var header = new Grid
        {
            Height = ArtHeight,
            CornerRadius = new CornerRadius(8, 8, 0, 0),
        };
        _artHeader = header;

        // Подложки-градиента больше нет: в переходах просвечивает фон окна.
        // Текст заглушки пуст и появляется только при реальных проблемах.
        _artFallbackLabel = new TextBlock
        {
            Text = _availableArt.Count > 0
                ? string.Empty
                : "Добавьте GIF-сцены в Assets/WhatsNew/art",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.8,
        };
        header.Children.Add(_artFallbackLabel);

        _backImage = BuildArtImage("Задний слой анимации");
        _frontImage = BuildArtImage($"Анимация: {_currentPiece}");
        header.Children.Add(_backImage);
        header.Children.Add(_frontImage);

        // Затемняющая полоса снизу для читаемости бейджа версии.
        header.Children.Add(new Border
        {
            Height = 44,
            VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(0, 0, 0, 0),
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00), Offset = 0 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0x99, 0x00, 0x00, 0x00), Offset = 1 },
                },
            },
            IsHitTestVisible = false,
        });

        // Вуаль строго чёрная, а не градиент: затухание идёт в чёрный.
        // Стартует непрозрачной и гаснет, когда готов первый кадр.
        _veil = new Border
        {
            CornerRadius = new CornerRadius(8, 8, 0, 0),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00)),
            Opacity = 1,
            IsHitTestVisible = false,
        };
        header.Children.Add(_veil);
        _veilFadeOut = BuildOpacityFade(_veil, 1, 0, 350);

        var overlay = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(12, 0, 12, 8),
            Spacing = 8,
        };

        var versionBadge = new TextBlock
        {
            Text = _viewModel.VersionTitle,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
        };
        overlay.Children.Add(versionBadge);

        var spacer = new Border { HorizontalAlignment = HorizontalAlignment.Stretch };
        overlay.Children.Add(spacer);

        _nextArtButton = new Button
        {
            Content = "Другая анимация",
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.92,
            Visibility = _availableArt.Count > 1 ? Visibility.Visible : Visibility.Collapsed,
        };
        AutomationProperties.SetName(_nextArtButton, "Показать другую анимацию");
        _nextArtButton.Click += async (s, e) =>
        {
            // Блокируем повторные клики на время кроссфейда.
            _nextArtButton.IsEnabled = false;
            try
            {
                await ShowNextArtAsync();
            }
            finally
            {
                _nextArtButton.IsEnabled = true;
            }
        };
        overlay.Children.Add(_nextArtButton);

        header.Children.Add(overlay);
        return header;
    }

    private static Image BuildArtImage(string automationName)
    {
        var image = new Image
        {
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        AutomationProperties.SetName(image, automationName);
        return image;
    }

    /// <summary>
    /// Строит анимацию прозрачности элемента.
    /// </summary>
    private static Storyboard BuildOpacityFade(UIElement target, double from, double to, int durationMs)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        return storyboard;
    }

    private static SettingsCard BuildHighlightsCardStatic(System.Collections.Generic.IReadOnlyList<string> highlights)
    {
        var card = new SettingsCard
        {
            Header = "Главное в этой версии",
            ContentAlignment = ContentAlignment.Vertical,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        card.HeaderIcon = new FontIcon { Glyph = "\uE946" };
        card.Content = BuildBullets(highlights);
        return card;
    }

    private SettingsCard BuildHighlightsCard()
    {
        return BuildHighlightsCardStatic(_viewModel.Highlights);
    }

    /// <summary>
    /// Одна секция изменений. По регламенту верстки прямым потомком
    /// SettingsExpander.Items является только SettingsCard, а список
    /// пунктов лежит внутри его Content.
    /// </summary>
    private static SettingsExpander BuildSectionExpander(ReleaseNotesSection section, bool isExpanded)
    {
        var expander = new SettingsExpander
        {
            Header = $"{section.Title} ({section.Items.Count})",
            IsExpanded = isExpanded,
        };
        expander.HeaderIcon = new FontIcon { Glyph = SectionGlyph(section.Title) };

        var card = new SettingsCard
        {
            ContentAlignment = ContentAlignment.Vertical,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        card.Content = BuildBullets(section.Items);
        expander.Items.Add(card);
        return expander;
    }

    private static string SectionGlyph(string title)
    {
        if (title.Equals("Добавлено", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE710";
        }

        if (title.Equals("Изменено", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE70F";
        }

        if (title.Equals("Исправлено", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE76D";
        }

        return "\uE946";
    }

    private static StackPanel BuildBullets(System.Collections.Generic.IReadOnlyList<string> items)
    {
        var panel = new StackPanel { Spacing = 4 };
        foreach (string item in items)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"•  {item}",
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return panel;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        FitScrollToWindow();
        if (XamlRoot != null)
        {
            XamlRoot.Changed += OnXamlRootChanged;
        }

        // Метод не бросает исключений: все отказы уходят в заглушку.
        await InitializeArtAsync(_closeCts.Token);
    }

    /// <summary>
    /// Подгоняет высоту скролла списка под окно с запасом на шапку, кнопки
    /// и отступы диалога, чтобы окно влезало в экран и низ списка не резался.
    /// Вызывается при открытии, после подгонки шапки и при ресайзе окна.
    /// </summary>
    private void FitScrollToWindow()
    {
        try
        {
            double windowHeight = XamlRoot?.Size.Height ?? 0;
            double artHeight = _artHeader.ActualHeight > 0 ? _artHeader.ActualHeight : ArtHeight;
            if (windowHeight <= 0)
            {
                _listScroll.MaxHeight = 380;
                return;
            }

            _listScroll.MaxHeight = Math.Max(200, windowHeight - 260 - artHeight);
        }
        catch
        {
            // Игнорируется: остаётся высота по умолчанию.
        }
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        FitScrollToWindow();
    }

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        // Сначала отменяем фоновые continuations, чтобы они не трогали
        // разобранное дерево после закрытия.
        try
        {
            _closeCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Игнорируется: диалог уже закрывался.
        }

        try
        {
            StopArt(_frontImage);
            StopArt(_backImage);
            if (XamlRoot != null)
            {
                XamlRoot.Changed -= OnXamlRootChanged;
            }
        }
        catch
        {
            // Игнорируется: остановка анимаций best-effort.
        }
        finally
        {
            _closeCts.Dispose();
        }
    }

    /// <summary>
    /// Первый показ шапки: грузим текущую сцену и снимаем вуаль.
    /// </summary>
    private async System.Threading.Tasks.Task InitializeArtAsync(System.Threading.CancellationToken token)
    {
        if (_availableArt.Count == 0)
        {
            _veilFadeOut.Begin();
            return;
        }

        try
        {
            token.ThrowIfCancellationRequested();
            if (!WhatsNewArtCatalog.TryResolveArtPath(_currentPiece, out string artPath))
            {
                _artFallbackLabel.Text = $"Файл сцены «{_currentPiece}» не найден";
                Reveal();
                return;
            }

            bool ready = await LoadArtAsync(_frontImage, artPath, token);
            if (!ready && !token.IsCancellationRequested)
            {
                _artFallbackLabel.Text = $"Анимация «{_currentPiece}» не загрузилась";
            }

            if (_frontImage.Source is BitmapImage)
            {
                // Подгоняем высоту шапки под пропорции гифок (2:1) один раз:
                // дальше геометрия стабильна и не прыгает при листании.
                FitArtHeight();
                FitScrollToWindow();
            }

            // Появление из прозрачности даёт гаснущая вуаль поверх.
            _frontImage.Opacity = 1;
            Reveal();
        }
        catch (OperationCanceledException)
        {
            // Закрыто до готовности: молча выходим.
        }

        // Страховка: вуаль снимется в любом случае.
        try
        {
            await System.Threading.Tasks.Task.Delay(RevealFallbackMs, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Reveal();
    }

    /// <summary>
    /// Листание сцен по кругу через прозрачность: текущая растворяется,
    /// новая проявляется после готовности. Без чёрных вспышек.
    /// </summary>
    private async System.Threading.Tasks.Task ShowNextArtAsync()
    {
        System.Threading.CancellationToken token = _closeCts.Token;
        if (_availableArt.Count < 2)
        {
            return;
        }

        try
        {
            token.ThrowIfCancellationRequested();
            string next = NextAvailablePiece();
            if (!WhatsNewArtCatalog.TryResolveArtPath(next, out string artPath))
            {
                return;
            }

            BuildOpacityFade(_frontImage, 1, 0, 200).Begin();
            await System.Threading.Tasks.Task.Delay(220, token);

            bool ready = await LoadArtAsync(_backImage, artPath, token);
            if (!ready)
            {
                BuildOpacityFade(_frontImage, 0, 1, 250).Begin();
                return;
            }

            (_frontImage, _backImage) = (_backImage, _frontImage);
            _backImage.Opacity = 0;
            StopArt(_backImage);

            _currentPiece = next;
            _viewModel.ArtPieceName = next;
            AutomationProperties.SetName(_frontImage, $"Анимация: {next}");
            BuildOpacityFade(_frontImage, 0, 1, 300).Begin();
        }
        catch (OperationCanceledException)
        {
            // Диалог закрыт посреди смены: молча выходим.
        }
    }

    private string NextAvailablePiece()
    {
        for (int i = 0; i < _availableArt.Count; i++)
        {
            if (_availableArt[i].Equals(_currentPiece, StringComparison.OrdinalIgnoreCase))
            {
                return _availableArt[(i + 1) % _availableArt.Count];
            }
        }

        return _availableArt[0];
    }

    /// <summary>
    /// Загружает GIF-сцену в слой и ждёт готовности (или таймаут/отмену).
    /// </summary>
    private async System.Threading.Tasks.Task<bool> LoadArtAsync(
        Image target,
        string artPath,
        System.Threading.CancellationToken token)
    {
        var ready = new System.Threading.Tasks.TaskCompletionSource<bool>();

        void OnOpened(object s, RoutedEventArgs e) => ready.TrySetResult(true);
        void OnFailed(object s, ExceptionRoutedEventArgs e) => ready.TrySetResult(false);

        target.ImageOpened += OnOpened;
        target.ImageFailed += OnFailed;
        try
        {
            var bitmap = new BitmapImage
            {
                AutoPlay = _animationsEnabled,
            };

            // Декодируем сразу в размер показа по обеим осям: однопроходный
            // ресемпл WIC давит муар на периодичной структуре арта (ряды точек),
            // который даёт вертикальный даунскейл 640->256 средствами GPU.
            double shownWidth = _artHeader.ActualWidth;
            if (shownWidth <= 0)
            {
                shownWidth = 512;
            }

            int decodeWidth = Math.Max(320, (int)Math.Ceiling(shownWidth));
            int decodeHeight = Math.Max(140, Math.Min(340, (int)Math.Ceiling(decodeWidth / ArtAspectRatio)));
            bitmap.DecodePixelType = DecodePixelType.Logical;
            bitmap.DecodePixelWidth = decodeWidth;
            bitmap.DecodePixelHeight = decodeHeight;
            bitmap.UriSource = new Uri(artPath);
            target.Source = bitmap;

            await System.Threading.Tasks.Task.WhenAny(
                ready.Task,
                System.Threading.Tasks.Task.Delay(SwitchTimeoutMs, token));
            token.ThrowIfCancellationRequested();
            return ready.Task.IsCompletedSuccessfully && ready.Task.Result;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            target.ImageOpened -= OnOpened;
            target.ImageFailed -= OnFailed;
        }
    }

    /// <summary>
    /// Подгоняет высоту шапки под пропорции гифок (2:1).
    /// Вызывается один раз для стабильности геометрии при листании.
    /// </summary>
    private void FitArtHeight()
    {
        try
        {
            double width = _artHeader.ActualWidth;
            if (width <= 0)
            {
                width = 512;
            }

            _artHeader.Height = Math.Clamp(width / ArtAspectRatio, 140, 340);
        }
        catch
        {
            // Игнорируется: остаётся высота по умолчанию.
        }
    }

    private void Reveal()
    {
        if (!_artRevealed)
        {
            _artRevealed = true;
            _veilFadeOut.Begin();
        }
    }

    private static void StopArt(Image? image)
    {
        try
        {
            if (image?.Source is BitmapImage bitmap && bitmap.IsPlaying)
            {
                bitmap.Stop();
            }
        }
        catch
        {
            // Игнорируется: остановка best-effort.
        }
    }

    /// <summary>
    /// Уважаем системный запрет анимаций: показываем неподвижный первый кадр.
    /// </summary>
    private static bool AreAnimationsEnabled()
    {
        try
        {
            return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        }
        catch
        {
            return true;
        }
    }
}
