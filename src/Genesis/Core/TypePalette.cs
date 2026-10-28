using Raylib_cs;

namespace Genesis.Core
{
    /// <summary>
    /// Стандартные цвета типов частиц и работа с палитрой.
    ///
    /// Цвета фиксированные, а не случайные: иначе одинаковые правила в двух прогонах
    /// выглядели бы по-разному. Тона пастельные, чтобы на чёрном фоне несколько структур
    /// рядом не сливались. Менять цвета здесь; типов может быть больше, чем цветов,
    /// тогда палитра повторяется по кругу.
    /// </summary>
    public static class TypePalette
    {
        private static readonly Color[] Colors =
        {
            new Color(226, 137, 137, 255), // пастельный красный
            new Color(138, 166, 226, 255), // пастельный синий
            new Color(146, 200, 148, 255), // пастельный зелёный
            new Color(226, 196, 138, 255), // пастельный янтарный
            new Color(190, 158, 222, 255), // пастельный фиолетовый
            new Color(136, 203, 199, 255), // пастельный бирюзовый
        };

        public static Color Get(int typeIndex)
            => Colors[((typeIndex % Colors.Length) + Colors.Length) % Colors.Length];

        public static void Reset(Color[] target)
        {
            for (int i = 0; i < target.Length; i++)
                target[i] = Get(i);
        }

        /// <summary>
        /// Копирует цвета из источника; если источник короче (или его нет), остальные
        /// типы получают цвет по умолчанию.
        /// </summary>
        public static void Apply(Color[] target, Color[]? source)
        {
            for (int i = 0; i < target.Length; i++)
                target[i] = source != null && i < source.Length ? source[i] : Get(i);
        }
    }
}
