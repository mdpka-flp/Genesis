using System;

namespace Genesis.Core
{
    /// <summary>Что показывать в оверлее поверх симуляции.</summary>
    [Flags]
    public enum OverlayParts
    {
        None = 0,
        ParticleCount = 1 << 0,
        TypeCount = 1 << 1,
        Fps = 1 << 2,
        Mode = 1 << 3,
        All = ParticleCount | TypeCount | Fps | Mode,
    }

    /// <summary>
    /// Видимые строки оверлея. Настройка общая для всех бэкендов, поэтому
    /// переключается один раз и не сбрасывается при смене режима.
    /// </summary>
    public static class OverlaySettings
    {
        /// <summary>Включённые строки. По умолчанию видны все, как раньше.</summary>
        public static OverlayParts Parts { get; set; } = OverlayParts.All;

        public static bool Has(OverlayParts part) => (Parts & part) != 0;
    }
}
