using System;
using System.Numerics;
using Raylib_cs;

namespace Genesis.Core
{
    /// <summary>
    /// Снимок настроек симуляции: скаляры, матрица правил, матрица связей и цвета типов.
    ///
    /// Нужен при смене бэкенда: новый режим должен стартовать на тех же правилах,
    /// иначе сравнить физику на одном правиле невозможно.
    ///
    /// Матрица хранится в двух видах, потому что у вещественного и комплексного режимов
    /// разные типы. При переносе вещественное правило читается как комплексное с нулевой
    /// мнимой частью, а у комплексного берётся вещественная часть.
    /// </summary>
    public sealed class SimulationSettings
    {
        public float InteractionRadius { get; init; }
        public float ForceMultiplier { get; init; }
        public float Friction { get; init; }
        public float MinDistance { get; init; }
        public float RepulsionStrength { get; init; }

        public float[,]? RealMatrix { get; init; }
        public Complex[,]? ComplexMatrix { get; init; }
        public int[,]? Connections { get; init; }
        public Color[] Colors { get; init; } = Array.Empty<Color>();

        /// <summary>Снимает настройки с текущего бэкенда (позиции частиц не входят).</summary>
        public static SimulationSettings Capture(ISimulation sim)
        {
            float[,]? real = sim switch
            {
                Simulation floatSim => floatSim.InteractionMatrix,
                SimulationGpu gpuSim => gpuSim.InteractionMatrix,
                _ => null,
            };

            Complex[,]? complex = sim is Simulation_ComplexNumbers complexSim
                ? complexSim.InteractionMatrix
                : null;

            return new SimulationSettings
            {
                InteractionRadius = sim.InteractionRadius,
                ForceMultiplier = sim.ForceMultiplier,
                Friction = sim.Friction,
                MinDistance = sim.MinDistance,
                RepulsionStrength = sim.RepulsionStrength,
                RealMatrix = real == null ? null : (float[,])real.Clone(),
                ComplexMatrix = complex == null ? null : (Complex[,])complex.Clone(),
                Connections = sim.MaxConnections == null
                    ? null
                    : (int[,])sim.MaxConnections.Clone(),
                Colors = (Color[])sim.TypeColors.Clone(),
            };
        }

        /// <summary>
        /// Матрица правил для вещественного режима. Недостающие клетки (после смены
        /// числа типов) остаются нулевыми — это «нет взаимодействия», а не случайность.
        /// </summary>
        public static float[,] ToRealMatrix(float[,]? real, Complex[,]? complex, int typeCount)
        {
            var matrix = new float[typeCount, typeCount];

            for (int i = 0; i < typeCount; i++)
            {
                for (int j = 0; j < typeCount; j++)
                {
                    if (real != null && i < real.GetLength(0) && j < real.GetLength(1))
                        matrix[i, j] = real[i, j];
                    else if (complex != null && i < complex.GetLength(0) && j < complex.GetLength(1))
                        matrix[i, j] = (float)complex[i, j].Real;
                }
            }

            return matrix;
        }

        /// <summary>Матрица правил для комплексного режима (вещественное правило — без поворота).</summary>
        public static Complex[,] ToComplexMatrix(Complex[,]? complex, float[,]? real, int typeCount)
        {
            var matrix = new Complex[typeCount, typeCount];

            for (int i = 0; i < typeCount; i++)
            {
                for (int j = 0; j < typeCount; j++)
                {
                    if (complex != null && i < complex.GetLength(0) && j < complex.GetLength(1))
                        matrix[i, j] = complex[i, j];
                    else if (real != null && i < real.GetLength(0) && j < real.GetLength(1))
                        matrix[i, j] = new Complex(real[i, j], 0);
                }
            }

            return matrix;
        }

        /// <summary>
        /// Матрица связей нужного размера: старые значения сохраняются, новые клетки
        /// получают <see cref="ConnectionLimits.None"/> — новый тип по умолчанию ни с
        /// кем не связывается, пока пользователь не задаст правило и предел.
        /// Если исходной матрицы нет (старый пресет), остаётся 0 — «без ограничения».
        /// </summary>
        public static int[,] ResizeConnections(int[,]? source, int typeCount)
        {
            var matrix = new int[typeCount, typeCount];
            if (source == null)
                return matrix;

            for (int i = 0; i < typeCount; i++)
                for (int j = 0; j < typeCount; j++)
                    matrix[i, j] = ConnectionLimits.None;

            int count = Math.Min(typeCount, Math.Min(source.GetLength(0), source.GetLength(1)));
            for (int i = 0; i < count; i++)
                for (int j = 0; j < count; j++)
                    matrix[i, j] = source[i, j];

            return matrix;
        }
    }
}
