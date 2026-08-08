# Спецификация Цепочки Видеофильтров и Модулей Видеокодирования

Документ сохранен для сохранения всей архитектуры, порядка применения видеофильтров и нюансов работы с кодеками/субтитрами при последующей реализации.

---

## 1. Порядок применения Видеофильтров (Priority & Order)

Строка `-vf` в FFmpeg собирается через запятую в **строгом приоритетном порядке**:

```text
[SubtitlesFilter (Priority 0)] -> [AutoCropFilter (Priority 10)] -> [AutoScaleFilter (Priority 20)]
```

### 1.1 `SubtitlesFilter` (Приоритет: 0)
- **Цель**: Вшивание субтитров (Hardsub).
- **Синтаксис**:
  - На Windows все слэши в путях приводятся к экранированным прямолинейным: `.Replace("\\", "/").Replace(":", "\\:")`.
  - Формируемый аргумент:
    ```text
    subtitles='C\:/Users/.../temp.ass':fontsdir='C\:/Users/.../fonts'
    ```
- **Параметры применения**:
  - Применяется при `burn_subtitles_enabled == true` и наличии существующего `.ass` файла.

### 1.2 `AutoCropFilter` (Приоритет: 10)
- **Цель**: Автоматическое удаление черных полос (Letterboxing/Pillarboxing).
- **Логика детекции**:
  - Запуск зондирования через `ffmpeg -ss <skip_sec> -i <file> -vframes 10 -vf cropdetect=<threshold>:<round>:0 -f null -`.
  - Извлечение значений `crop=w:h:x:y`.
- **Синтаксис в -vf**: `crop=W:H:X:Y`.

### 1.3 `AutoScaleFilter` (Приоритет: 20)
- **Цель**: Масштабирование видео с сохранением пропорций и проверкой кратности 2 (`trunc(iw*coef/2)*2`).
- **Синтаксис в -vf**:
  ```text
  scale=eval=exact:w='trunc(iw*0.5/2)*2':h='trunc(ih*0.5/2)*2':flags=lanczos
  ```
- **Поддерживаемые алгоритмы**: `lanczos`, `bicubic`, `bilinear`, `spline`.

---

## 2. Важные нюансы работы с Энкодерами

1. **HEVC / H.265**:
   - Для контейнера `.mp4` при использовании HEVC обязателен тэг `-tag:v hvc1` (для воспроизведения в Apple QuickTime / iOS).
   - **Внимание**: Для `av01` (AV1) или `h264` этот флаг **НЕ должен передаваться**, иначе FFmpeg падает с ошибкой `Tag hvc1 incompatible with output codec`.

2. **AV1 (libsvtav1)**:
   - Не поддерживает честный математический Lossless через `-crf 0` (при `crf 0` улетает в `crf 35`).
   - Для режима Near-Lossless / Visually Lossless в `libsvtav1` нужно использовать **`-crf 1`**.

3. **Связка UI и полей `auto_bitrate`**:
   - Поля `min_bitrate`, `max_bitrate` и `bufsize` при включении `auto_bitrate` должны оставаться **видимыми**, но делать свойство `IsEnabled = false`.
