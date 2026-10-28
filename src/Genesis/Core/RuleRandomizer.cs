using System;
using System.Collections.Generic;
using System.Numerics;

namespace Genesis.Core
{
    /// <summary>
    /// Что именно перегенерировать при нажатии «Generate random rules».
    ///
    /// Маска живёт здесь, а не в UI: её читают все три бэкенда из
    /// <c>GenerateRules()</c>, поэтому кнопка и горячая клавиша G ведут себя одинаково.
    /// </summary>
    [Flags]
    public enum RuleRandomParts
    {
        None = 0,
        Radius = 1 << 0,
        ForceMultiplier = 1 << 1,
        Friction = 1 << 2,
        MinDistance = 1 << 3,
        RepulsionStrength = 1 << 4,
        Matrix = 1 << 5,
        Connections = 1 << 6,
        All = Radius | ForceMultiplier | Friction | MinDistance |
              RepulsionStrength | Matrix | Connections,
    }

    /// <summary>
    /// Параметры, для которых можно задать диапазон случайных значений.
    /// Порядок элементов задаёт порядок пунктов в выпадающем меню UI.
    /// </summary>
    public enum RandomParameter
    {
        Radius = 0,
        ForceMultiplier = 1,
        Friction = 2,
        MinDistance = 3,
        RepulsionStrength = 4,
        Rule = 5,
        Connections = 6,
    }

    /// <summary>Диапазон, из которого берётся случайное значение (границы включены).</summary>
    public readonly struct RandomRange
    {
        public RandomRange(float min, float max)
        {
            Min = min;
            Max = max;
        }

        public float Min { get; }
        public float Max { get; }
    }

    /// <summary>
    /// Общий источник случайных правил для всех бэкендов.
    ///
    /// Диапазоны по умолчанию подобраны так, чтобы правила получались «живыми», но без
    /// крайностей: при слишком малом ForceMultiplier частицы замирают, при слишком
    /// большом — разлетаются. Держать их в одном месте важно, иначе три реализации
    /// разъедутся по поведению.
    ///
    /// Настройки статические, а не на симуляцию: это свойство эксперимента, которое
    /// должно переезжать вместе с правилами при смене бэкенда.
    /// </summary>
    public static class RuleRandomizer
    {
        private static readonly RandomRange[] Ranges =
        {
            new RandomRange(55f, 120f),      // Radius, px
            new RandomRange(25f, 70f),       // ForceMultiplier
            new RandomRange(0.950f, 0.985f), // Friction
            new RandomRange(12f, 36f),       // MinDistance, px
            new RandomRange(25f, 75f),       // RepulsionStrength
            new RandomRange(-9f, 9f),        // Rule (и компонент комплексного правила)
            new RandomRange(1f, 4f),         // Connections
        };

        /// <summary>
        /// Какие параметры меняются при генерации правил. По умолчанию — все:
        /// так «Generate random rules» остаётся прежним поведением.
        /// </summary>
        public static RuleRandomParts Parts { get; set; } = RuleRandomParts.All;

        public static RandomRange GetRange(RandomParameter parameter)
            => Ranges[(int)parameter];

        /// <summary>
        /// Задаёт диапазон. Границы нормализуются (min ≤ max) и обрезаются до
        /// безопасных пределов: нулевой радиус сломал бы сетку, трение больше 1 —
        /// разгон вместо затухания.
        /// </summary>
        public static void SetRange(RandomParameter parameter, float min, float max)
        {
            if (min > max)
                (min, max) = (max, min);

            (min, max) = parameter switch
            {
                RandomParameter.Radius => (MathF.Max(1f, min), MathF.Max(1f, max)),
                RandomParameter.Friction => (Math.Clamp(min, 0f, 1f), Math.Clamp(max, 0f, 1f)),
                RandomParameter.MinDistance => (MathF.Max(0f, min), MathF.Max(0f, max)),
                RandomParameter.RepulsionStrength => (MathF.Max(0f, min), MathF.Max(0f, max)),
                RandomParameter.Connections => (
                    Math.Clamp(min, 1f, ConnectionLimits.MaxPerType),
                    Math.Clamp(max, 1f, ConnectionLimits.MaxPerType)),
                _ => (min, max),
            };

            // После обрезки порядок мог нарушиться (например, min = 100 → 32, max = 5).
            if (min > max)
                (min, max) = (max, min);

            Ranges[(int)parameter] = new RandomRange(min, max);
        }

        public static float RandomFloat(RandomParameter parameter, Random random)
        {
            RandomRange range = Ranges[(int)parameter];
            float min = MathF.Min(range.Min, range.Max);
            float max = MathF.Max(range.Min, range.Max);

            if (max <= min)
                return min;

            return min + (float)random.NextDouble() * (max - min);
        }

        /// <summary>Случайное целое из диапазона (обе границы включены).</summary>
        public static int RandomInt(RandomParameter parameter, Random random)
        {
            RandomRange range = Ranges[(int)parameter];
            int min = (int)MathF.Ceiling(MathF.Min(range.Min, range.Max));
            int max = (int)MathF.Floor(MathF.Max(range.Min, range.Max));

            if (max <= min)
                return min;

            return random.Next(min, max + 1);
        }

        /// <summary>Скалярные параметры, общие для всех бэкендов (с учётом маски).</summary>
        public static void ApplyScalars(ISimulation sim, Random random)
        {
            RuleRandomParts parts = Parts;

            if ((parts & RuleRandomParts.Radius) != 0)
                sim.InteractionRadius = RandomFloat(RandomParameter.Radius, random);

            if ((parts & RuleRandomParts.ForceMultiplier) != 0)
                sim.ForceMultiplier = RandomFloat(RandomParameter.ForceMultiplier, random);

            if ((parts & RuleRandomParts.Friction) != 0)
                sim.Friction = RandomFloat(RandomParameter.Friction, random);

            if ((parts & RuleRandomParts.MinDistance) != 0)
                sim.MinDistance = RandomFloat(RandomParameter.MinDistance, random);

            if ((parts & RuleRandomParts.RepulsionStrength) != 0)
                sim.RepulsionStrength = RandomFloat(RandomParameter.RepulsionStrength, random);
        }

        public static float[,] NewRuleMatrix(Random random, int typeCount)
        {
            var matrix = new float[typeCount, typeCount];
            for (int i = 0; i < typeCount; i++)
                for (int j = 0; j < typeCount; j++)
                    matrix[i, j] = RandomFloat(RandomParameter.Rule, random);

            return matrix;
        }

        /// <summary>
        /// Матрица правил для комплексного режима: вещественная и мнимая части берутся
        /// из одного диапазона Rule (мнимая часть — это поворот, а не отдельная сила).
        /// </summary>
        public static Complex[,] NewComplexRuleMatrix(Random random, int typeCount)
        {
            var matrix = new Complex[typeCount, typeCount];
            for (int i = 0; i < typeCount; i++)
                for (int j = 0; j < typeCount; j++)
                    matrix[i, j] = new Complex(
                        RandomFloat(RandomParameter.Rule, random),
                        RandomFloat(RandomParameter.Rule, random));

            return matrix;
        }

        /// <summary>
        /// Матрица правил для готовой матрицы связей: притяжение включается только
        /// на рёбрах дерева (обе клетки положительны). Остальные пары получают 0 —
        /// они не тянутся и не отталкиваются, но MinDistance всё равно не даёт
        /// частицам слипнуться.
        /// </summary>
        public static float[,] NewRuleMatrix(Random random, int[,] connections, int typeCount)
        {
            var matrix = new float[typeCount, typeCount];
            for (int i = 0; i < typeCount; i++)
                for (int j = 0; j < typeCount; j++)
                {
                    if (connections[i, j] <= 0 || connections[j, i] <= 0)
                    {
                        matrix[i, j] = 0f;
                        continue;
                    }

                    float strength = MathF.Abs(RandomFloat(RandomParameter.Rule, random));
                    matrix[i, j] = MathF.Max(0.5f, strength);
                }

            return matrix;
        }

        /// <summary>Комплексный вариант: положительная вещественная часть — притяжение,
        /// мнимая — закрутка вокруг партнёра.</summary>
        public static Complex[,] NewComplexRuleMatrix(
            Random random, int[,] connections, int typeCount)
        {
            var matrix = new Complex[typeCount, typeCount];
            for (int i = 0; i < typeCount; i++)
                for (int j = 0; j < typeCount; j++)
                {
                    if (connections[i, j] <= 0 || connections[j, i] <= 0)
                    {
                        matrix[i, j] = Complex.Zero;
                        continue;
                    }

                    float real = MathF.Max(
                        0.5f, MathF.Abs(RandomFloat(RandomParameter.Rule, random)));
                    float imaginary = RandomFloat(RandomParameter.Rule, random);
                    matrix[i, j] = new Complex(real, imaginary);
                }

            return matrix;
        }

        /// <summary>
        /// Случайное дерево типов вместо полной матрицы. У каждого типа один родитель;
        /// родитель может притянуть несколько частиц каждого дочернего типа, ребёнок —
        /// только одну частицу родителя. Дерево без циклов и с валентностью 1 у детей
        /// даёт конечные молекулы (звёзды и ветки), а не одну сеть на весь экран.
        /// </summary>
        public static int[,] NewConnectionTree(Random random, int typeCount)
        {
            var matrix = new int[typeCount, typeCount];
            for (int i = 0; i < typeCount; i++)
                for (int j = 0; j < typeCount; j++)
                    matrix[i, j] = ConnectionLimits.None;

            if (typeCount <= 0)
                return matrix;

            if (typeCount == 1)
            {
                // Один тип: разрешаем димеры, иначе структур не будет вообще.
                matrix[0, 0] = 1;
                return matrix;
            }

            int root = random.Next(typeCount);
            var attached = new List<int>(typeCount) { root };
            var remaining = new List<int>(typeCount - 1);
            for (int t = 0; t < typeCount; t++)
                if (t != root)
                    remaining.Add(t);

            for (int i = remaining.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (remaining[i], remaining[j]) = (remaining[j], remaining[i]);
            }

            var depth = new int[typeCount];
            depth[root] = 0;
            const int maxDepth = 2;

            foreach (int child in remaining)
            {
                int candidates = 0;
                foreach (int t in attached)
                    if (depth[t] < maxDepth)
                        candidates++;

                int parent;
                if (candidates > 0)
                {
                    int pick = random.Next(candidates);
                    parent = attached[0];
                    foreach (int t in attached)
                    {
                        if (depth[t] >= maxDepth)
                            continue;
                        if (pick-- == 0)
                        {
                            parent = t;
                            break;
                        }
                    }
                }
                else
                {
                    parent = attached[random.Next(attached.Count)];
                }

                int valence = Math.Max(1, RandomInt(RandomParameter.Connections, random));
                matrix[parent, child] = valence;
                matrix[child, parent] = 1;
                depth[child] = depth[parent] + 1;
                attached.Add(child);
            }

            return matrix;
        }
    }
}
