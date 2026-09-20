// -*- coding: utf-8 -*-
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KTools_App.Core;
using KTools_App.Infrastructure;
using KTools_App.Services.Contracts;

namespace KTools_App.Scripts;

/// <summary>
/// Скрипт автоматического распознавания речи и генерации субтитров на основе whisper.cpp CLI.
/// Выполняет извлечение аудио через FFmpeg, инференс нейросетевых моделей Whisper и сохранение субтитров.
/// </summary>
public sealed class SpeechRecognitionScript : AbstractScript
{
    private readonly IWhisperRunner _whisperRunner;
    private readonly IWhisperModelManager _modelManager;
    private readonly IFFmpegRunner _ffmpegRunner;
    private readonly IDependencyManager _dependencyManager;
    private readonly IDialogService _dialogService;

    /// <summary>
    /// Инициализирует новый экземпляр класса <see cref="SpeechRecognitionScript"/> с внедрением зависимостей.
    /// </summary>
    public SpeechRecognitionScript(
        ILogService logService,
        ISettingsManager settingsManager,
        IPathManager pathManager,
        IWhisperRunner whisperRunner,
        IWhisperModelManager modelManager,
        IFFmpegRunner ffmpegRunner,
        IDependencyManager dependencyManager,
        IDialogService dialogService)
        : base(logService, settingsManager, pathManager)
    {
        _whisperRunner = whisperRunner ?? throw new ArgumentNullException(nameof(whisperRunner));
        _modelManager = modelManager ?? throw new ArgumentNullException(nameof(modelManager));
        _ffmpegRunner = ffmpegRunner ?? throw new ArgumentNullException(nameof(ffmpegRunner));
        _dependencyManager = dependencyManager ?? throw new ArgumentNullException(nameof(dependencyManager));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
    }

    /// <inheritdoc/>
    public override string Name => AppConstants.ScriptMetadata.SpeechRecognitionName;

    /// <inheritdoc/>
    public override string Description => AppConstants.ScriptMetadata.SpeechRecognitionDesc;

    /// <inheritdoc/>
    public override string Category => AppConstants.ScriptCategory.Subtitles;

    /// <inheritdoc/>
    public override string IconName => AppConstants.ScriptIcons.SpeechRecognition;

    /// <inheritdoc/>
    public override string[] FileExtensions => AppConstants.VideoContainers
        .Concat(AppConstants.AudioContainers)
        .Concat(AppConstants.AudioStreams)
        .ToArray();

    /// <inheritdoc/>
    public override string[] RequiredDependencies => new[] { "ffmpeg" };

    /// <inheritdoc/>
    public override bool SupportsParallel => false; // Транскрибация ресурсоёмка, пакет обрабатывается последовательно

    private static readonly List<string> SupportedLanguages = new()
    {
        "Авто",
        "Русский (ru)",
        "English (en)",
        "Deutsch (de)",
        "Français (fr)",
        "Español (es)",
        "Italiano (it)",
        "日本語 (ja)",
        "中文 (zh)",
        "한국어 (ko)",
        "Português (pt)",
        "Українська (uk)",
        "Polski (pl)",
        "Türkçe (tr)",
        "العربية (ar)"
    };

    private static readonly List<string> BackendOptions = new()
    {
        "Авто (Рекомендуется)",
        "CPU (Процессор, AVX2)",
        "CUDA (NVIDIA GPU)"
    };

    /// <inheritdoc/>
    public override List<SettingField> SettingsSchema
    {
        get
        {
            var schema = new List<SettingField>();

            // --- 1. Вкладка: Основные (Модель и язык) ---
            var models = _modelManager.GetAvailableModels();
            var downloadedModels = models.Where(m => _modelManager.IsModelDownloaded(m.Key)).Select(m => m.Key).ToList();
            var modelOptions = downloadedModels.Count > 0
                ? downloadedModels
                : new List<string> { "(Нет загруженных моделей)" };

            string defaultModel = downloadedModels.Contains("large-v3-turbo", StringComparer.OrdinalIgnoreCase)
                ? "large-v3-turbo"
                : (downloadedModels.FirstOrDefault() ?? "large-v3-turbo");

            schema.Add(new SettingField(
                "whisper_model",
                "Модель Whisper",
                SettingType.Combo,
                defaultModel,
                "Основные:Модель и язык",
                downloadedModels.Count > 0
                    ? "Выберите загруженную модель для распознавания речи."
                    : "⚠️ Ни одна модель Whisper не загружена. Перейдите на вкладку 'Модели' и скачайте желаемую модель.",
                options: modelOptions));

            schema.Add(new SettingField(
                "whisper_language",
                "Язык исходного аудио",
                SettingType.Combo,
                "Авто",
                "Основные:Модель и язык",
                "Язык речи. Режим 'Авто' определяет язык по первым 30 секундам записи.",
                options: SupportedLanguages));

            schema.Add(new SettingField(
                "whisper_translate",
                "Перевести на английский язык",
                SettingType.Checkbox,
                false,
                "Основные:Модель и язык",
                "Автоматический перевод распознаваемого текста на английский язык силами модели Whisper."));

            // --- 2. Вкладка: Субтитры (Форматы и разбивка) ---
            schema.Add(new SettingField(
                "output_srt",
                "Создавать субтитры SRT (.srt)",
                SettingType.Checkbox,
                true,
                "Субтитры:Форматы экспорта",
                "Стандартный формат субтитров SubRip с таймкодами."));

            schema.Add(new SettingField(
                "output_vtt",
                "Создавать субтитры WebVTT (.vtt)",
                SettingType.Checkbox,
                false,
                "Субтитры:Форматы экспорта",
                "Формат субтитров для веб-плееров и потокового видео."));

            schema.Add(new SettingField(
                "output_txt",
                "Сохранять обычный текст (.txt)",
                SettingType.Checkbox,
                true,
                "Субтитры:Форматы экспорта",
                "Сплошной текстовый документ без таймкодов для чтения и конспектов."));

            schema.Add(new SettingField(
                "output_lrc",
                "Создавать караоке LRC (.lrc)",
                SettingType.Checkbox,
                false,
                "Субтитры:Форматы экспорта",
                "Формат построчных караоке-текстов для музыкальных плееров."));

            schema.Add(new SettingField(
                "output_json",
                "Экспортировать JSON (.json)",
                SettingType.Checkbox,
                false,
                "Субтитры:Форматы экспорта",
                "Структурированный файл с метаданными токенов и точными таймингами."));

            schema.Add(new SettingField(
                "max_segment_length",
                "Максимальная длина строки (символы)",
                SettingType.Int,
                35,
                "Субтитры:Параметры субтитров",
                "35–42 символа — общепринятый стандарт длины строки для кино и сериалов (Netflix: до 42, BBC/дубляж: 37–39, ТВ: до 40). При 0 перенос отключен.",
                minimum: 0,
                maximum: 200));

            schema.Add(new SettingField(
                "split_on_word",
                "Разделять строго по словам",
                SettingType.Checkbox,
                true,
                "Субтитры:Параметры субтитров",
                "Не разрывать слова посередине при переносе длинных фраз на новую строку."));

            schema.Add(new SettingField(
                "whisper_max_context",
                "Контекст предыдущих токенов",
                SettingType.Int,
                0,
                "Субтитры:Параметры субтитров",
                "Количество токенов контекста из прошлой фразы. Рекомендуется 0 для исключения зацикливания и бесконечных повторов слов на музыке/тишине.",
                minimum: 0,
                maximum: 448));

            schema.Add(new SettingField(
                "whisper_no_speech_thold",
                "Порог отсечения тишины и музыки",
                SettingType.Float,
                0.65,
                "Субтитры:Параметры субтитров",
                "Порог вероятности отсутствия речи (0.0..1.0). Чем выше, тем строже отсекаются паузы, тишина и фоновые звуки. По умолчанию: 0.65.",
                minimum: 0.0,
                maximum: 1.0));

            schema.Add(new SettingField(
                "whisper_suppress_nst",
                "Подавление неречевых галлюцинаций",
                SettingType.Checkbox,
                true,
                "Субтитры:Параметры субтитров",
                "Запрещает Whisper генерировать псевдоречевые маркеры и спам-токены ('ПОДПИШИСЬ!' и др.) при отсутствии голоса (--suppress-nst)."));

            // --- 3. Вкладка: Движок (Производительность и GPU) ---
            schema.Add(new SettingField(
                "whisper_backend",
                "Вычислительный движок",
                SettingType.Combo,
                "Авто (Рекомендуется)",
                "Движок:Аппаратное ускорение",
                "Выбор устройства вычислений. 'Авто' автоматически использует быстрейший доступный GPU.",
                options: BackendOptions));

            schema.Add(new SettingField(
                "whisper_threads",
                "Количество потоков CPU",
                SettingType.Int,
                Math.Min(Environment.ProcessorCount, 8),
                "Движок:Аппаратное ускорение",
                "Число потоков процессора для расчётов. Рекомендуется от 4 до 8.",
                minimum: 1,
                maximum: 32));

            schema.Add(new SettingField(
                "whisper_flash_attn",
                "Flash Attention (для GPU)",
                SettingType.Checkbox,
                false,
                "Движок:Аппаратное ускорение",
                "Оптимизация внимания: экономит VRAM и ускоряет инференс. Рекомендуется для видеокарт NVIDIA поколения Ampere и новее (RTX 30xx, 40xx). Для более старых карт (Turing/RTX 20xx) рекомендуется выключить при сбоях."));

            // --- 4. Вкладка: Модели (Управление и загрузка) ---
            foreach (var m in models)
            {
                schema.Add(new SettingField(
                    $"whisper_model_action_{m.Key}",
                    $"{m.Key} (~{m.SizeMb:F0} МБ)",
                    SettingType.WhisperModelAction,
                    m.Key,
                    "Модели:Доступные модели",
                    m.Description));
            }

            return schema;
        }
    }

    /// <inheritdoc/>
    public override List<SettingField> GetSettingsSchema(Dictionary<string, object>? currentSettings = null)
    {
        return SettingsSchema;
    }

    /// <inheritdoc/>
    public override async Task<List<string>> ExecuteSingleAsync(
        string filePath,
        Dictionary<string, object> settings,
        string? outputPath,
        ScriptProgressCallback progressCallback,
        int fileIndex,
        int totalCount)
    {
        var resultMessages = new List<string>();

        if (!File.Exists(filePath))
        {
            string notFoundMsg = $"Входной файл не найден: '{filePath}'";
            _logService.Error(notFoundMsg, Name);
            resultMessages.Add(notFoundMsg);
            return resultMessages;
        }

        // 1. Получаем выбранную модель
        string modelKey = GetSettingValue(settings, "whisper_model", "large-v3-turbo");
        if (!_modelManager.IsModelDownloaded(modelKey))
        {
            _logService.Warn($"Выбранная модель Whisper '{modelKey}' не найдена на диске. Инициализация загрузки...", Name);
            progressCallback(fileIndex, totalCount, $"Загрузка модели Whisper ({modelKey})...", 5);

            var modelInfo = _modelManager.GetModelInfo(modelKey);
            string prompt = modelInfo != null
                ? $"Модель '{modelInfo.DisplayName}' отсутствует на диске.\n\nСкачать её сейчас (размер: ~{modelInfo.SizeMb:F0} МБ)?"
                : $"Модель '{modelKey}' не загружена. Скачать её?";

            bool confirmed = await _dialogService.ShowConfirmationAsync("Загрузка модели Whisper", prompt, "Скачать", "Отмена");
            if (!confirmed)
            {
                string cancelMsg = $"Операция отменена: модель '{modelKey}' не загружена.";
                resultMessages.Add(cancelMsg);
                return resultMessages;
            }

            var downloadProgress = new Progress<int>(p =>
            {
                progressCallback(fileIndex, totalCount, $"Скачивание модели '{modelKey}': {p}%", p * 0.25);
            });

            var speedProgress = new Progress<string>(s =>
            {
                progressCallback(fileIndex, totalCount, $"Скачивание модели '{modelKey}' ({s})...", null);
            });

            bool downloaded = await _modelManager.DownloadModelAsync(modelKey, downloadProgress, speedProgress, CancellationToken);
            if (!downloaded || !_modelManager.IsModelDownloaded(modelKey))
            {
                string failMsg = $"Не удалось загрузить модель Whisper '{modelKey}'. Проверьте сетевое подключение.";
                _logService.Error(failMsg, Name);
                resultMessages.Add(failMsg);
                return resultMessages;
            }
        }

        string modelFilePath = _modelManager.GetModelFilePath(modelKey);

        // 2. Определяем вычислительный бэкенд
        string backendSetting = GetSettingValue(settings, "whisper_backend", "Авто (Рекомендуется)");
        WhisperBackend backend = backendSetting switch
        {
            "CPU (Процессор, AVX2)" => WhisperBackend.Cpu,
            "CUDA (NVIDIA GPU)" => WhisperBackend.Cuda,
            _ => WhisperBackend.Auto
        };

        WhisperBackend effectiveBackend = backend == WhisperBackend.Auto
            ? WhisperBackendDetector.DetectBestBackend(_dependencyManager)
            : backend;

        string depKey = WhisperBackendDetector.GetDependencyKey(effectiveBackend);
        if (!_dependencyManager.IsInstalled(depKey))
        {
            _logService.Info($"Рантайм Whisper '{depKey}' отсутствует на диске. Инициируется фоновая установка...", Name);
            progressCallback(fileIndex, totalCount, $"Установка рантайма Whisper ({depKey})...", 10);
            await _dependencyManager.InstallDependencyAsync(depKey);

            if (!_dependencyManager.IsInstalled(depKey))
            {
                string depFail = $"Не удалось установить зависимость '{depKey}'. Транскрибация невозможна.";
                _logService.Error(depFail, Name);
                resultMessages.Add(depFail);
                return resultMessages;
            }
        }

        // 3. Подготавливаем временный WAV-файл (16kHz Mono 16-bit PCM) через FFmpeg
        string tempWavPath = Path.Combine(Path.GetTempPath(), $"ktools_whisper_{Guid.NewGuid():N}.wav");
        try
        {
            progressCallback(fileIndex, totalCount, "Извлечение аудиопотока в 16-кГц WAV...", 15);
            _logService.Info($"Конвертация аудиодорожки из '{Path.GetFileName(filePath)}' во временный WAV: '{tempWavPath}'", Name);

            var extraFfmpegArgs = new List<string> { "-vn", "-acodec", "pcm_s16le", "-ar", "16000", "-ac", "1" };
            bool ffmpegOk = await _ffmpegRunner.RunAsync(
                inputPath: filePath,
                outputPath: tempWavPath,
                extraArgs: extraFfmpegArgs,
                overwrite: true,
                cancellationToken: CancellationToken);

            if (!ffmpegOk || !File.Exists(tempWavPath))
            {
                string ffmpegFail = "Ошибка извлечения аудиопотока через FFmpeg: процесс завершился с ошибкой.";
                _logService.Error(ffmpegFail, Name);
                resultMessages.Add(ffmpegFail);
                return resultMessages;
            }

            // 4. Формируем выходной базовый путь
            string safeOut = GetSafeOutputPath(filePath, string.IsNullOrWhiteSpace(outputPath) ? filePath : Path.Combine(outputPath, Path.GetFileName(filePath)), settings);
            string outputDir = Path.GetDirectoryName(safeOut) ?? Path.GetDirectoryName(filePath) ?? "";
            string stem = Path.GetFileNameWithoutExtension(safeOut);
            string outputBasePath = Path.Combine(outputDir, stem);

            // 5. Разбираем настройки языков и субтитров
            string langStr = GetSettingValue(settings, "whisper_language", "Авто");
            string langCode = "auto";
            if (!langStr.Equals("Авто", StringComparison.OrdinalIgnoreCase))
            {
                var match = System.Text.RegularExpressions.Regex.Match(langStr, @"\(([a-z]{2})\)");
                langCode = match.Success ? match.Groups[1].Value : "auto";
            }

            bool translate = GetSettingValue(settings, "whisper_translate", false);
            bool outSrt = GetSettingValue(settings, "output_srt", true);
            bool outVtt = GetSettingValue(settings, "output_vtt", false);
            bool outTxt = GetSettingValue(settings, "output_txt", true);
            bool outLrc = GetSettingValue(settings, "output_lrc", false);
            bool outJson = GetSettingValue(settings, "output_json", false);

            int maxLen = GetSettingValue(settings, "max_segment_length", 35);
            bool splitOnWord = GetSettingValue(settings, "split_on_word", true);
            int maxContext = GetSettingValue(settings, "whisper_max_context", 0);
            double noSpeechThold = Convert.ToDouble(GetSettingValue(settings, "whisper_no_speech_thold", 0.65f), System.Globalization.CultureInfo.InvariantCulture);
            bool suppressNst = GetSettingValue(settings, "whisper_suppress_nst", true);
            int threads = GetSettingValue(settings, "whisper_threads", Math.Min(Environment.ProcessorCount, 8));
            bool flashAttn = GetSettingValue(settings, "whisper_flash_attn", false);

            var transcribeOptions = new WhisperTranscribeOptions
            {
                InputWavPath = tempWavPath,
                ModelPath = modelFilePath,
                OutputBasePath = outputBasePath,
                Language = langCode,
                Translate = translate,
                Backend = effectiveBackend,
                OutputSrt = outSrt,
                OutputVtt = outVtt,
                OutputTxt = outTxt,
                OutputLrc = outLrc,
                OutputJson = outJson,
                MaxSegmentLength = maxLen,
                SplitOnWord = splitOnWord,
                MaxContext = maxContext,
                NoSpeechThreshold = noSpeechThold,
                SuppressNonSpeechTokens = suppressNst,
                EnableVad = false,
                Threads = threads,
                FlashAttention = flashAttn
            };

            // 6. Запуск процесса транскрибации
            progressCallback(fileIndex, totalCount, $"Распознавание речи через Whisper ({effectiveBackend})...", 20);

            var whisperResult = await _whisperRunner.TranscribeAsync(
                transcribeOptions,
                onProgress: percent =>
                {
                    // Масштабируем прогресс инференса от 20% до 98%
                    double overallPercent = 20.0 + (percent * 0.78);
                    progressCallback(fileIndex, totalCount, $"Распознавание речи: {percent}%", overallPercent);
                },
                onSegment: segmentText =>
                {
                    _logService.Info(segmentText, Name);
                },
                cancellationToken: CancellationToken);

            if (!whisperResult.IsSuccess)
            {
                string whisperFail = $"Ошибка транскрибации Whisper: {whisperResult.Message}";
                _logService.Error(whisperFail, Name);
                resultMessages.Add(whisperFail);
                return resultMessages;
            }

            progressCallback(fileIndex, totalCount, "Распознавание речи успешно завершено!", 100);
            string successMsg = $"Файлы субтитров/текста успешно созданы в папке: '{outputDir}'";
            _logService.Info(successMsg, Name);
            resultMessages.Add(successMsg);
        }
        catch (OperationCanceledException)
        {
            string cancelMsg = "Распознавание речи отменено пользователем.";
            _logService.Warn(cancelMsg, Name);
            resultMessages.Add(cancelMsg);
        }
        catch (Exception ex)
        {
            _logService.Exception(ex, $"Критический сбой при распознавании речи: {ex.Message}", Name);
            resultMessages.Add($"Ошибка: {ex.Message}");
        }
        finally
        {
            // Очистка промежуточного аудиофайла
            if (File.Exists(tempWavPath))
            {
                try { File.Delete(tempWavPath); } catch { /* Игнорируем */ }
            }
        }

        return resultMessages;
    }
}
