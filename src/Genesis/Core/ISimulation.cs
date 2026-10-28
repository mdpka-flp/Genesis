using System.Numerics;
using Raylib_cs;

namespace Genesis.Core 
{
    public interface ISimulation
    {
        int TypeCount { get; }
        int ParticleCount { get; }
    
        float InteractionRadius { get; set; }
        float ForceMultiplier { get; set; }
        float Friction { get; set; }
        float MinDistance { get; set; }
        float RepulsionStrength { get; set; }

        /// <summary>
        /// Цвета типов частиц (индекс — тип). Массив изменяется напрямую из UI,
        /// поэтому в нём всегда ровно <see cref="TypeCount"/> элементов.
        /// </summary>
        Color[] TypeColors { get; }

        /// <summary>Частицы: позиции, скорости и типы. Массив доступен только на чтение.</summary>
        Particle[] Particles { get; }

        /// <summary>
        /// Матрица связей: [свой тип, тип соседа] — сколько ближайших соседей этого типа
        /// частица учитывает при притяжении. 0 — без ограничения (совместимость со
        /// старыми пресетами; в UI показывается как «inf»).
        /// </summary>
        int[,] MaxConnections { get; }
    
        void Initialize(int particleCount, int typeCount);
        void Update(float deltaTime);
        void Draw();
    
        void GenerateRules();
        void GenerateColors();
        void AddType();
        void RemoveType();
        void SetParticleCount(int newCount);
        void Restart();

        /// <summary>
        /// Переносит правила, связи и цвета из снимка. Используется при смене бэкенда,
        /// чтобы новый режим стартовал на тех же настройках, а не на случайных.
        /// Позиции и скорости частиц не трогаются.
        /// </summary>
        void ApplySettings(SimulationSettings settings);

        /// <summary>
        /// Восстанавливает позиции, скорости и типы частиц из снимка другого бэкенда.
        /// Вызывается после <see cref="Initialize"/>, который расставляет частицы заново.
        /// </summary>
        void RestoreParticles(Particle[] source);
    }
}