using System;

namespace Genesis.Core
{
    /// <summary>
    /// Общая для всех бэкендов подготовка матрицы пределов связей.
    ///
    /// Семантика: <c>MaxConnections[свой тип, тип соседа] = K</c> означает, что при
    /// притяжении частица учитывает не больше <b>K ближайших</b> соседей этого типа.
    /// Значение 0 — «без ограничения» (так читаются старые пресеты, в которых матрицы
    /// ещё не было), и тогда притяжение работает по-старому: все соседи в радиусе.
    /// Значение <see cref="None"/> (-1) — «связей нет»: положительное правило пары
    /// не создаёт ни связи, ни притяжения; отрицательное по-прежнему отталкивает.
    /// Отталкивание матрицей не ограничивается никогда.
    ///
    /// Здесь же — перевод матрицы в плоские массивы, которые читают горячие циклы:
    /// <c>flat</c> (пределы), <c>slotBase</c> (смещение слотов своего типа) и
    /// <c>slotTotal</c> (сколько всего слотов у своего типа). Смещения нужны, чтобы
    /// держать выбранных соседей в одном плоском буфере на частицу.
    /// </summary>
    internal static class ConnectionLimits
    {
        /// <summary>
        /// Жёсткий потолок суммы пределов по строке матрицы, то есть числа слотов
        /// на частицу. Ограничен памятью GPU-скретча (частицы × слоты) и разумностью
        /// самого правила: больше 64 связей на частицу всё равно не увидеть.
        /// </summary>
        public const int MaxSlotsPerParticle = 64;

        /// <summary>Максимум, который принимает редактор матрицы в UI.</summary>
        public const int MaxPerType = 32;

        /// <summary>«Связей нет»: положительное правило пары не создаёт связь.</summary>
        public const int None = -1;

        private static bool warnedAboutBudget;

        /// <summary>
        /// Перекладывает матрицу в плоские массивы. Возвращает true, если в матрице
        /// есть хотя бы одна не-ноль клетка (положительный предел или «связей нет»);
        /// иначе горячий цикл идёт по старому пути без выбора соседей.
        /// </summary>
        public static bool Flatten(
            int[,]? source,
            int typeCount,
            int[] flat,
            int[] slotBase,
            int[] slotTotal)
        {
            bool hasLimits = false;

            bool sourceOk = source != null &&
                            source.GetLength(0) >= typeCount &&
                            source.GetLength(1) >= typeCount;

            for (int i = 0; i < typeCount; i++)
            {
                // Строка матрицы — бюджет одной частицы этого типа. Сумма по всем
                // типам соседей не должна превысить MaxSlotsPerParticle.
                int remaining = MaxSlotsPerParticle;
                int total = 0;

                for (int j = 0; j < typeCount; j++)
                {
                    int limit = sourceOk ? source![i, j] : 0;
                    if (limit < 0) limit = None;
                    if (limit > MaxPerType) limit = MaxPerType;

                    int slots = limit > 0 ? limit : 0;

                    if (slots > remaining)
                    {
                        slots = remaining;

                        // Молча превращать тип в «без ограничения» нельзя — это
                        // меняет смысл правила, поэтому пишем один раз в консоль.
                        if (!warnedAboutBudget && remaining == 0)
                        {
                            warnedAboutBudget = true;
                            Console.WriteLine(
                                "[Genesis] Бюджет связей больше " +
                                $"{MaxSlotsPerParticle} на частицу: лишние типы " +
                                "считаются безлимитными.");
                        }
                    }

                    remaining -= slots;

                    flat[i * typeCount + j] = limit;
                    slotBase[i * typeCount + j] = total;
                    total += slots;

                    if (limit != 0) hasLimits = true;
                }

                slotTotal[i] = total;
            }

            return hasLimits;
        }

        /// <summary>
        /// Отпечаток раскладки слотов. По нему бэкенды понимают, что матрицу связей
        /// поменяли (в UI её правят прямо в массиве), и сбрасывают запомненные связи:
        /// старые слоты могут относиться уже к другому типу.
        /// </summary>
        public static int Hash(int[] flat, int[] slotBase, int count)
        {
            unchecked
            {
                int hash = (int)2166136261;
                for (int i = 0; i < count; i++)
                {
                    hash = (hash ^ flat[i]) * 16777619;
                    hash = (hash ^ slotBase[i]) * 16777619;
                }

                return hash;
            }
        }
    }
}
