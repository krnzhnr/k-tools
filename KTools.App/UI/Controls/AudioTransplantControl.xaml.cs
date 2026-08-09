// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;
using KTools_App.Core;
using KTools_App.Services.Contracts;
using KTools_App.Scripts;

namespace KTools_App.UI.Controls;

/// <summary>
/// Компонент интерфейса для управления пересадкой аудиодорожки в стиле DubSwap.
/// Содержит карточки выбора файлов с поддержкой Drag and Drop, выбора отдельных аудиодорожек и выравнивания.
/// Все комментарии, логи и исключения выполнены на русском языке в соответствии с регламентом.
/// </summary>
public sealed partial class AudioTransplantControl : UserControl
{
    private readonly IMediaProbeService _mediaProbeService;
    private readonly ISettingsManager _settingsManager;
    private readonly ILogService _logService;
    private AbstractScript? _activeScript;
    private bool _isUpdatingUi = false;

    /// <summary>
    /// Активный скрипт пересадки аудио.
    /// </summary>
    public AbstractScript? ActiveScript
    {
        get => _activeScript;
        set
        {
            _activeScript = value;
            if (_activeScript is AudioTransplantScript script)
            {
                _isUpdatingUi = true;
                try
                {
                    SourcePathTextBox.Text = script.SourceFilePath;
                    DestPathTextBox.Text = script.DestFilePath;
                    SubtitlesPathTextBox.Text = script.SubtitlesFilePath;

                    UpdateShiftStatusText(script.ShiftMs);
                }
                finally
                {
                    _isUpdatingUi = false;
                }

                if (!string.IsNullOrWhiteSpace(SourcePathTextBox.Text) && File.Exists(SourcePathTextBox.Text.Trim()))
                {
                    _ = PopulateSourceTracksAsync(SourcePathTextBox.Text.Trim());
                }

                if (!string.IsNullOrWhiteSpace(DestPathTextBox.Text) && File.Exists(DestPathTextBox.Text.Trim()))
                {
                    string trimmedDest = DestPathTextBox.Text.Trim();
                    if (_activeScript.FilesQueue.Count == 0 || _activeScript.FilesQueue[0].FilePath != trimmedDest)
                    {
                        _activeScript.FilesQueue.Clear();
                        _activeScript.FilesQueue.Add(new FileQueueItem(trimmedDest));
                    }
                    _ = PopulateDestTracksAsync(trimmedDest);
                }
            }
        }
    }

    public AudioTransplantControl()
    {
        InitializeComponent();
        _mediaProbeService = App.Services.GetRequiredService<IMediaProbeService>();
        _settingsManager = App.Services.GetRequiredService<ISettingsManager>();
        _logService = App.Services.GetRequiredService<ILogService>();
    }

    /// <summary>
    /// Полный сброс состояния полей формы в памяти при запуске/перезапуске скрипта.
    /// </summary>
    private void ResetFormState()
    {
        _isUpdatingUi = true;
        try
        {
            SourcePathTextBox.Text = string.Empty;
            DestPathTextBox.Text = string.Empty;
            SubtitlesPathTextBox.Text = string.Empty;

            SourceTrackComboBox.Items.Clear();
            SourceTrackComboBox.PlaceholderText = "Сначала выберите файл источника...";

            DestTrackComboBox.Items.Clear();
            DestTrackComboBox.PlaceholderText = "Сначала выберите целевое видео...";

            UpdateShiftStatusText(0);

            if (_activeScript is AudioTransplantScript script)
            {
                script.SourceFilePath = string.Empty;
                script.DestFilePath = string.Empty;
                script.SubtitlesFilePath = string.Empty;
                script.ShiftMs = 0;
                script.SourceTrackIndex = 0;
                script.DestTrackIndex = 0;
                script.FilesQueue.Clear();
            }
        }
        finally
        {
            _isUpdatingUi = false;
        }
    }

    #region Drag & Drop обработчики

    private void Card_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.Caption = "Добавить файл";
        e.Handled = true;
    }

    private async void SourceCard_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var files = await GetDroppedFilesAsync(e);
        if (files.Count > 0)
        {
            SourcePathTextBox.Text = files[0];
        }
    }

    private async void DestCard_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var files = await GetDroppedFilesAsync(e);
        if (files.Count > 0)
        {
            DestPathTextBox.Text = files[0];
        }
    }

    private async void SubtitlesCard_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var files = await GetDroppedFilesAsync(e);
        if (files.Count > 0)
        {
            SubtitlesPathTextBox.Text = files[0];
        }
    }

    private static async Task<List<string>> GetDroppedFilesAsync(DragEventArgs e)
    {
        var result = new List<string>();
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var items = await e.DataView.GetStorageItemsAsync();
            foreach (var item in items)
            {
                if (item is Windows.Storage.StorageFile file)
                {
                    result.Add(file.Path);
                }
            }
        }
        return result;
    }

    #endregion

    #region Кнопки Обзора Файлов

    private async void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        string? file = await PickSingleFileAsync("Медиафайлы", new[] { ".mkv", ".mp4", ".avi", ".m2ts", ".ts", ".wav", ".flac", ".mp3", ".aac", ".ac3", ".dts", ".mka" });
        if (!string.IsNullOrEmpty(file))
        {
            SourcePathTextBox.Text = file;
        }
    }

    private async void BrowseDest_Click(object sender, RoutedEventArgs e)
    {
        string? file = await PickSingleFileAsync("Видеоконтейнеры", new[] { ".mkv", ".mp4", ".avi", ".m2ts", ".ts", ".webm" });
        if (!string.IsNullOrEmpty(file))
        {
            DestPathTextBox.Text = file;
        }
    }

    private async void BrowseSubtitles_Click(object sender, RoutedEventArgs e)
    {
        string? file = await PickSingleFileAsync("Файлы субтитров", new[] { ".ass", ".srt", ".vtt", ".ssa" });
        if (!string.IsNullOrEmpty(file))
        {
            SubtitlesPathTextBox.Text = file;
        }
    }

    private void ClearSubtitles_Click(object sender, RoutedEventArgs e)
    {
        SubtitlesPathTextBox.Text = string.Empty;
    }

    private static async Task<string?> PickSingleFileAsync(string description, string[] extensions)
    {
        var picker = new FileOpenPicker();
        InitPickerWindow(picker);
        picker.FileTypeFilter.Clear();
        foreach (var ext in extensions)
        {
            picker.FileTypeFilter.Add(ext);
        }
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private static void InitPickerWindow(object picker)
    {
        if (App.CurrentMainWindow != null)
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(App.CurrentMainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }
    }

    #endregion

    #region Изменение текстов и сканирование дорожек

    private async void SourcePathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingUi || _activeScript is not AudioTransplantScript script) return;

        string path = SourcePathTextBox.Text.Trim();
        script.SourceFilePath = path;

        if (File.Exists(path))
        {
            await PopulateSourceTracksAsync(path);
        }
        else
        {
            SourceTrackComboBox.Items.Clear();
            SourceTrackComboBox.PlaceholderText = "Сначала выберите файл источника...";
        }
    }

    private async Task PopulateSourceTracksAsync(string filePath)
    {
        SourceTrackComboBox.Items.Clear();
        SourceTrackComboBox.PlaceholderText = "Анализ дорожек источника...";

        try
        {
            var mediaStructure = await _mediaProbeService.ProbeAsync(filePath);
            if (mediaStructure != null && mediaStructure.Tracks.Count > 0)
            {
                var audioTracks = mediaStructure.Tracks.Where(t => t.TrackType.Equals("audio", StringComparison.OrdinalIgnoreCase)).ToList();
                if (audioTracks.Count > 0)
                {
                    int ffmpegIndex = 0;
                    foreach (var track in audioTracks)
                    {
                        string lang = string.IsNullOrWhiteSpace(track.Language) ? "und" : track.Language;
                        string title = string.IsNullOrWhiteSpace(track.Name) ? "" : $" ({track.Name})";
                        string label = $"[Дорожка #{ffmpegIndex}] [{lang}] {track.Codec.ToUpperInvariant()} - {track.Channels} кан.{title}";

                        SourceTrackComboBox.Items.Add(new ComboBoxItem { Content = label, Tag = ffmpegIndex });
                        ffmpegIndex++;
                    }

                    int savedTrackIndex = (_activeScript is AudioTransplantScript script) ? script.SourceTrackIndex : 0;
                    int selectedIndex = 0;
                    for (int i = 0; i < SourceTrackComboBox.Items.Count; i++)
                    {
                        if (SourceTrackComboBox.Items[i] is ComboBoxItem item && item.Tag is int tagIdx && tagIdx == savedTrackIndex)
                        {
                            selectedIndex = i;
                            break;
                        }
                    }
                    SourceTrackComboBox.SelectedIndex = selectedIndex;
                    return;
                }
            }

            SourceTrackComboBox.Items.Add(new ComboBoxItem { Content = "[Дорожка #0] Исходное аудио", Tag = 0 });
            SourceTrackComboBox.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, $"Ошибка сканирования дорожек источника '{filePath}'", "AudioTransplantControl");
            SourceTrackComboBox.Items.Add(new ComboBoxItem { Content = "[Дорожка #0] Исходное аудио", Tag = 0 });
            SourceTrackComboBox.SelectedIndex = 0;
        }
    }

    private async void DestPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingUi || _activeScript is not AudioTransplantScript script) return;

        string path = DestPathTextBox.Text.Trim();
        script.DestFilePath = path;

        script.FilesQueue.Clear();
        if (File.Exists(path))
        {
            script.FilesQueue.Add(new FileQueueItem(path));
            await PopulateDestTracksAsync(path);
        }
        else
        {
            DestTrackComboBox.Items.Clear();
            DestTrackComboBox.PlaceholderText = "Сначала выберите целевое видео...";
        }
    }

    private async Task PopulateDestTracksAsync(string filePath)
    {
        DestTrackComboBox.Items.Clear();
        DestTrackComboBox.PlaceholderText = "Анализ дорожек целевого видео...";

        try
        {
            var mediaStructure = await _mediaProbeService.ProbeAsync(filePath);
            if (mediaStructure != null && mediaStructure.Tracks.Count > 0)
            {
                var audioTracks = mediaStructure.Tracks.Where(t => t.TrackType.Equals("audio", StringComparison.OrdinalIgnoreCase)).ToList();
                if (audioTracks.Count > 0)
                {
                    int ffmpegIndex = 0;
                    foreach (var track in audioTracks)
                    {
                        string lang = string.IsNullOrWhiteSpace(track.Language) ? "und" : track.Language;
                        string title = string.IsNullOrWhiteSpace(track.Name) ? "" : $" ({track.Name})";
                        string label = $"[Дорожка #{ffmpegIndex}] [{lang}] {track.Codec.ToUpperInvariant()} - {track.Channels} кан.{title}";

                        DestTrackComboBox.Items.Add(new ComboBoxItem { Content = label, Tag = ffmpegIndex });
                        ffmpegIndex++;
                    }

                    int savedTrackIndex = (_activeScript is AudioTransplantScript script) ? script.DestTrackIndex : 0;
                    int selectedIndex = 0;
                    for (int i = 0; i < DestTrackComboBox.Items.Count; i++)
                    {
                        if (DestTrackComboBox.Items[i] is ComboBoxItem item && item.Tag is int tagIdx && tagIdx == savedTrackIndex)
                        {
                            selectedIndex = i;
                            break;
                        }
                    }
                    DestTrackComboBox.SelectedIndex = selectedIndex;
                    return;
                }
            }

            DestTrackComboBox.Items.Add(new ComboBoxItem { Content = "[Дорожка #0] Основное аудио", Tag = 0 });
            DestTrackComboBox.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, $"Ошибка сканирования дорожек целевого видео '{filePath}'", "AudioTransplantControl");
            DestTrackComboBox.Items.Add(new ComboBoxItem { Content = "[Дорожка #0] Основное аудио", Tag = 0 });
            DestTrackComboBox.SelectedIndex = 0;
        }
    }

    private void SubtitlesPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingUi || _activeScript is not AudioTransplantScript script) return;
        script.SubtitlesFilePath = SubtitlesPathTextBox.Text.Trim();
    }

    private void SourceTrackComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingUi || _activeScript is not AudioTransplantScript script) return;
        if (SourceTrackComboBox.SelectedItem is ComboBoxItem item && item.Tag is int idx)
        {
            script.SourceTrackIndex = idx;
        }
    }

    private void DestTrackComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingUi || _activeScript is not AudioTransplantScript script) return;
        if (DestTrackComboBox.SelectedItem is ComboBoxItem item && item.Tag is int idx)
        {
            script.DestTrackIndex = idx;
        }
    }

    private async void OpenSyncWindowButton_Click(object sender, RoutedEventArgs e)
    {
        string sourcePath = SourcePathTextBox.Text.Trim();
        string destPath = DestPathTextBox.Text.Trim();

        var dialogService = App.Services.GetRequiredService<IDialogService>();

        if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
        {
            await dialogService.ShowMessageAsync("Файлы не выбраны", "Пожалуйста, сначала укажите существующий исходный файл (Этап 1).");
            return;
        }

        if (string.IsNullOrEmpty(destPath) || !File.Exists(destPath))
        {
            await dialogService.ShowMessageAsync("Файлы не выбраны", "Пожалуйста, сначала укажите существующее целевое видео (Этап 2).");
            return;
        }

        int sourceTrackIndex = 0;
        if (SourceTrackComboBox.SelectedItem is ComboBoxItem sourceItem && sourceItem.Tag is int sIdx)
        {
            sourceTrackIndex = sIdx;
        }

        int destTrackIndex = 0;
        if (DestTrackComboBox.SelectedItem is ComboBoxItem destItem && destItem.Tag is int dIdx)
        {
            destTrackIndex = dIdx;
        }

        var waveformService = App.Services.GetRequiredService<IAudioWaveformService>();
        var syncWindow = new AudioSyncWindow(waveformService, destPath, destTrackIndex, sourcePath, sourceTrackIndex);

        syncWindow.Closed += (s, args) =>
        {
            if (syncWindow.IsConfirmed && _activeScript is AudioTransplantScript script)
            {
                int userShift = syncWindow.UserShiftMs;
                script.ShiftMs = userShift;
                UpdateShiftStatusText(userShift);
            }
        };

        syncWindow.Activate();
    }

    private void UpdateShiftStatusText(int shiftMs)
    {
        if (shiftMs == 0)
        {
            ShiftStatusTextBlock.Text = "Выбранный сдвиг: 0 мс (без сдвига)";
        }
        else
        {
            string sign = shiftMs > 0 ? "+" : "";
            ShiftStatusTextBlock.Text = $"Выбранный сдвиг: {sign}{shiftMs} мс";
        }
    }

    #endregion
}
