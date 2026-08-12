// -*- coding: utf-8 -*-
using System;

namespace KTools_App.Encoders;

/// <summary>
/// Строго типизированный контекст общих параметров, которые влияют 
/// на логику построения аргументов любого видео-энкодера.
/// </summary>
public record EncoderSharedContext(
    bool IsLossless,
    bool Force10Bit,
    string ContainerExtension
);
