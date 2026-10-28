using Raylib_cs;

namespace Genesis.Core
{
    internal static class Overlay
    {
        /// <summary>
        /// Рисует включённые строки сверху вниз. Выключенная строка не оставляет
        /// пустого места: следующие поднимаются выше.
        /// </summary>
        public static void Draw(int particleCount, int typeCount, string mode, Color modeColor)
        {
            int y = 10;

            if (OverlaySettings.Has(OverlayParts.ParticleCount))
            {
                Raylib.DrawText("particle count: " + particleCount, 10, y, 50, Color.White);
                y += 60;
            }

            if (OverlaySettings.Has(OverlayParts.TypeCount))
            {
                Raylib.DrawText("type count: " + typeCount, 10, y, 50, Color.White);
                y += 60;
            }

            if (OverlaySettings.Has(OverlayParts.Fps))
            {
                Raylib.DrawText($"FPS: {Raylib.GetFPS()}", 10, y, 50, Color.White);
                y += 60;
            }

            if (OverlaySettings.Has(OverlayParts.Mode))
                Raylib.DrawText(mode, 10, y, 30, modeColor);
        }
    }
}
