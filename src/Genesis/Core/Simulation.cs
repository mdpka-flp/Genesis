using System;
using System.Numerics;
using System.Threading.Tasks;
using Raylib_cs;

namespace Genesis.Core
{
    public class Simulation : ISimulation
    {
        public Particle[] Particles { get; private set; }
        public float[,] InteractionMatrix;

        // Новые состояния кадра. Считаем в отдельный массив и меняем местами в конце:
        // иначе поток может прочитать уже обновлённую позицию соседа, и две стороны
        // одной связи примут разные решения о разрыве.
        private Particle[] nextParticles = Array.Empty<Particle>();

        /// <summary>Пределы связей по парам типов (0 — без ограничения).</summary>
        public int[,] MaxConnections { get; private set; } = new int[0, 0];

        public float InteractionRadius { get; set; } = 80f;
        public float ForceMultiplier { get; set; } = 40f;
        public float Friction { get; set; } = 0.98f;
        public float MinDistance { get; set; } = 25f;
        public float RepulsionStrength { get; set; } = 25f;

        public int TypeCount { get; private set; }
        public int ParticleCount => Particles.Length;

        /// <summary>Цвета типов частиц; меняются из UI напрямую.</summary>
        public Color[] TypeColors => typeColors;

        private int screenWidth;
        private int screenHeight;

        private Random random;
        private Color[] typeColors;
        
        private int gridWidth, gridHeight;
        private float cellSize;
        private int[] sortedIndexes;
        private int[] cellStart;
        private int[] cellCount;
        private int[] nextPos;

        // Развёрнутая матрица связей: в горячем цикле плоский массив дешевле int[,].
        private int[] maxConnectionsFlat = Array.Empty<int>();
        private int[] connectionSlotBase = Array.Empty<int>();
        private int[] connectionSlotTotal = Array.Empty<int>();
        private bool hasConnectionLimits;

        // Запомненные связи: bondSlots на частицу, 0 — пусто, иначе индекс партнёра + 1.
        // Слоты идут в том же порядке, что и у селектора: [тип, слот].
        private int[] bonds = Array.Empty<int>();
        private int bondSlots = 1;
        private int connectionHash;

        // Заявки на связи. Связь образуется только когда обе частицы выбрали друг
        // друга, поэтому заявки живут один кадр в двойном буфере: prev читается,
        // cur пишется. Так граф связей остаётся взаимным, а не «кто кого выбрал».
        private int[] proposalPrev = Array.Empty<int>();
        private int[] proposalCur = Array.Empty<int>();
        private int[] proposalCountPrev = Array.Empty<int>();
        private int[] proposalCountCur = Array.Empty<int>();

        // Буфер выбора ближайших соседей — свой на каждый поток, чтобы не аллоцировать
        // массивы на каждую частицу каждый кадр.
        [ThreadStatic] private static NeighbourSelector? selectorBuffer;

        public void Initialize(int particleCount, int typeCount)
        {
            screenWidth = Raylib.GetScreenWidth();
            screenHeight = Raylib.GetScreenHeight();

            random = new Random();

            Particles = new Particle[particleCount];
            nextParticles = new Particle[particleCount];
            this.TypeCount = typeCount;
            
            cellSize = InteractionRadius;
            
            gridWidth = (int)Math.Ceiling(screenWidth / cellSize);
            gridHeight = (int)Math.Ceiling(screenHeight / cellSize);

            sortedIndexes = new int[particleCount];
            cellStart = new int[gridWidth * gridHeight + 1];
            cellCount = new int[gridWidth * gridHeight];
            nextPos = new int[gridWidth * gridHeight + 1];

            typeColors = new Color[typeCount];
            TypePalette.Reset(typeColors);

            // Нулевая матрица правил: GenerateRules() заменит её случайной, но
            // UI и загрузка пресета не должны зависеть от того, вызывали ли его.
            InteractionMatrix = new float[typeCount, typeCount];

            // По умолчанию связей не ограничиваем: 0 = без ограничения.
            MaxConnections = new int[typeCount, typeCount];
            maxConnectionsFlat = Array.Empty<int>();
            connectionSlotBase = Array.Empty<int>();
            connectionSlotTotal = Array.Empty<int>();
            hasConnectionLimits = false;
            bonds = Array.Empty<int>();
            bondSlots = 1;
            connectionHash = 0;
            proposalPrev = Array.Empty<int>();
            proposalCur = Array.Empty<int>();
            proposalCountPrev = Array.Empty<int>();
            proposalCountCur = Array.Empty<int>();
            
            for (int i = 0; i < particleCount; i++)
            {
                Particles[i] = new Particle
                {
                    Position = new Vector2(
                        random.Next(0, screenWidth),
                        random.Next(0, screenHeight)),
                    Velocity = Vector2.Zero,
                    Type = random.Next(0, typeCount)
                };
            }
        }

        public void Update(float deltaTime)
        {
            if (nextParticles.Length != Particles.Length)
                nextParticles = new Particle[Particles.Length];

            BuildSpatialLookup();
            PrepareConnectionLimits();

            // Локальные копии: поля читаются в горячем цикле многократно.
            int typeCount = TypeCount;
            bool limited = hasConnectionLimits;
            int[] limits = maxConnectionsFlat;
            int[] slotBase = connectionSlotBase;
            int[] slotTotal = connectionSlotTotal;
            int[] bondPartners = bonds;
            int[] prevProposals = proposalPrev;
            int[] curProposals = proposalCur;
            int[] prevCounts = proposalCountPrev;
            int[] curCounts = proposalCountCur;
            int maxSlots = bondSlots;
            float[,] matrix = InteractionMatrix;
            float radius = InteractionRadius;
            float radiusSq = radius * radius;
            float minDistance = MinDistance;
            float bondRest = MathF.Max(minDistance, 1f);
            float breakDistance = bondRest * Bonds.BreakDistanceFactor;
            float breakDistanceSq = breakDistance * breakDistance;
            float formDistance = MathF.Min(bondRest * Bonds.FormDistanceFactor, radius);
            float formDistanceSq = formDistance * formDistance;
            float forceMultiplier = ForceMultiplier;
            float repulsion = RepulsionStrength;

            Parallel.For(0, Particles.Length, i =>
            {
                Particle a = Particles[i];
                Vector2 acceleration = Vector2.Zero;
                int bondBase = i * maxSlots;
                int countBase = i * typeCount;

                for (int t = 0; t < typeCount; t++)
                    curCounts[countBase + t] = 0;

                // Существующие связи сначала проверяем и уплотняем: партнёр мог
                // умереть, уехать дальше радиуса разрыва или правило могло стать
                // отталкивающим. Свободные слоты остаются в конце блока типа.
                NeighbourSelector? selector = limited ? RentSelector() : null;
                if (selector != null && slotTotal[a.Type] > 0)
                    selector.Begin(slotTotal[a.Type], typeCount);
                else
                    selector = null;

                if (selector != null)
                {
                    for (int t = 0; t < typeCount; t++)
                    {
                        int limit = limits[a.Type * typeCount + t];
                        if (limit <= 0) continue;

                        int baseSlot = slotBase[a.Type * typeCount + t];
                        float rule = matrix[a.Type, t];
                        int write = 0;

                        if (rule > 0f)
                        {
                            for (int k = 0; k < limit; k++)
                            {
                                int stored = bondPartners[bondBase + baseSlot + k];
                                if (stored == 0) continue;

                                int j = stored - 1;
                                if (j < 0 || j >= Particles.Length) continue;

                                Particle b = Particles[j];
                                if (b.Type != t) continue;

                                float dxPos = WrappedDelta(b.Position.X - a.Position.X, screenWidth);
                                float dyPos = WrappedDelta(b.Position.Y - a.Position.Y, screenHeight);
                                float distanceSq = dxPos * dxPos + dyPos * dyPos;
                                if (distanceSq <= 0f || distanceSq > breakDistanceSq) continue;

                                bondPartners[bondBase + baseSlot + write] = stored;
                                write++;

                                // Связь — пружина с длиной покоя MinDistance: у неё есть
                                // устойчивая длина, поэтому молекула не сжимается в точку.
                                float distance = MathF.Sqrt(distanceSq);
                                float strength = rule * forceMultiplier * BondFactor(distance, bondRest);
                                acceleration.X += dxPos / distance * strength;
                                acceleration.Y += dyPos / distance * strength;
                            }
                        }

                        for (int k = write; k < limit; k++)
                            bondPartners[bondBase + baseSlot + k] = 0;

                        selector.Lock(t, write);
                    }

                    // Заявки прошлого кадра становятся связями только если обе
                    // частицы выбрали друг друга. Иначе заявка просто истекает.
                    for (int t = 0; t < typeCount; t++)
                    {
                        int limit = limits[a.Type * typeCount + t];
                        if (limit <= 0) continue;

                        int baseSlot = slotBase[a.Type * typeCount + t];
                        float rule = matrix[a.Type, t];
                        int locked = selector.LockedCount(t);
                        int promoted = 0;

                        if (rule > 0f)
                        {
                            int prevCount = prevCounts[countBase + t];
                            for (int k = 0; k < prevCount && locked + promoted < limit; k++)
                            {
                                int stored = prevProposals[bondBase + baseSlot + k];
                                if (stored == 0) continue;

                                int j = stored - 1;
                                if (j < 0 || j >= Particles.Length) continue;

                                Particle b = Particles[j];
                                if (b.Type != t) continue;

                                // Взаимное притяжение: иначе это не связь, а
                                // одностороннее влечение, которое копит мусор.
                                if (matrix[b.Type, a.Type] <= 0f) continue;

                                int partnerBase = slotBase[b.Type * typeCount + a.Type];
                                int partnerCount = prevCounts[j * typeCount + a.Type];
                                bool mutual = false;
                                for (int q = 0; q < partnerCount; q++)
                                {
                                    if (prevProposals[j * maxSlots + partnerBase + q] == i + 1)
                                    {
                                        mutual = true;
                                        break;
                                    }
                                }
                                if (!mutual) continue;

                                float dxPos = WrappedDelta(b.Position.X - a.Position.X, screenWidth);
                                float dyPos = WrappedDelta(b.Position.Y - a.Position.Y, screenHeight);
                                float distanceSq = dxPos * dxPos + dyPos * dyPos;
                                if (distanceSq <= 0f || distanceSq > formDistanceSq) continue;

                                if (IsBonded(bondPartners, bondBase + baseSlot,
                                              locked + promoted, j)) continue;

                                bondPartners[bondBase + baseSlot + locked + promoted] = j + 1;
                                promoted++;

                                float distance = MathF.Sqrt(distanceSq);
                                float strength = rule * forceMultiplier * BondFactor(distance, bondRest);
                                acceleration.X += dxPos / distance * strength;
                                acceleration.Y += dyPos / distance * strength;
                            }
                        }

                        selector.Lock(t, locked + promoted);
                    }
                }

                int gx = (int)(a.Position.X / cellSize);
                int gy = (int)(a.Position.Y / cellSize);
                gx = (gx + gridWidth) % gridWidth;
                gy = (gy + gridHeight) % gridHeight;

                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int nx = (gx + dx + gridWidth) % gridWidth;
                        int ny = (gy + dy + gridHeight) % gridHeight;
                        int ncell = ny * gridWidth + nx;

                        int start = cellStart[ncell];
                        int end = cellStart[ncell + 1];

                        for (int idx = start; idx < end; idx++)
                        {
                            int j = sortedIndexes[idx];
                            if (i == j) continue;
                            Particle b = Particles[j];

                            float dxPos = WrappedDelta(b.Position.X - a.Position.X, screenWidth);
                            float dyPos = WrappedDelta(b.Position.Y - a.Position.Y, screenHeight);

                            float distanceSq = dxPos * dxPos + dyPos * dyPos;
                            if (distanceSq > 0 && distanceSq < radiusSq)
                            {
                                float distance = (float)MathF.Sqrt(distanceSq);
                                Vector2 dir = new Vector2(dxPos, dyPos) / distance;

                                if (distance < minDistance)
                                {
                                    // Отталкивание не ограничиваем: это короткодействующая
                                    // защита от наложения, иначе частицы слипаются в точку.
                                    float repel = (1f - distance / minDistance) * repulsion;
                                    acceleration -= dir * repel;
                                    continue;
                                }

                                int limit = limited ? limits[a.Type * typeCount + b.Type] : 0;
                                float rule = matrix[a.Type, b.Type];

                                // «none»: связей с этим типом нет. Отрицательное
                                // правило всё ещё может отталкивать, положительное —
                                // не тянет и не занимает слот.
                                if (limit < 0)
                                {
                                    if (rule < 0f)
                                    {
                                        float repelRule = rule * (1f - distance / radius) * forceMultiplier;
                                        acceleration += dir * repelRule;
                                    }

                                    continue;
                                }

                                // Связи ограничивают только притяжение. Отталкивание
                                // (rule < 0) должно работать всегда, иначе заполненная
                                // структура не сможет оттолкнуть лишнюю частицу.
                                if (limit > 0 && rule > 0f && selector != null)
                                {
                                    int baseSlot = slotBase[a.Type * typeCount + b.Type];

                                    // Уже связанного партнёра не предлагаем повторно:
                                    // иначе он займёт второй слот и сила удвоится.
                                    if (!IsBonded(bondPartners, bondBase + baseSlot,
                                                  selector.LockedCount(b.Type), j))
                                    {
                                        selector.Offer(
                                            b.Type, j, dir.X, dir.Y, distance,
                                            limit, baseSlot);
                                    }

                                    continue;
                                }

                                float strength = rule * (1f - distance / radius) * forceMultiplier;
                                acceleration += dir * strength;
                            }
                        }
                    }
                }

                // Заявки этого кадра пойдут на проверку взаимности в следующем.
                if (selector != null)
                {
                    for (int t = 0; t < typeCount; t++)
                    {
                        int limit = limits[a.Type * typeCount + t];
                        if (limit <= 0) continue;

                        int locked = selector.LockedCount(t);
                        int count = selector.Count(t);
                        int n = count - locked;
                        int baseSlot = slotBase[a.Type * typeCount + t];

                        curCounts[countBase + t] = n;
                        for (int k = 0; k < n; k++)
                        {
                            int src = baseSlot + locked + k;
                            curProposals[bondBase + baseSlot + k] = selector.Indexes[src] + 1;
                        }
                    }
                }

                a.Velocity += acceleration * deltaTime;
                a.Velocity *= Friction;

                float maxSpeed = 200f;
                if (a.Velocity.LengthSquared() > maxSpeed * maxSpeed)
                    a.Velocity = Vector2.Normalize(a.Velocity) * maxSpeed;

                a.Position += a.Velocity * deltaTime;
                
                if (a.Position.X < 0) a.Position.X += screenWidth;
                if (a.Position.X > screenWidth) a.Position.X -= screenWidth;
                if (a.Position.Y < 0) a.Position.Y += screenHeight;
                if (a.Position.Y > screenHeight) a.Position.Y -= screenHeight;

                nextParticles[i] = a;
            });

            (Particles, nextParticles) = (nextParticles, Particles);
            (proposalPrev, proposalCur) = (proposalCur, proposalPrev);
            (proposalCountPrev, proposalCountCur) = (proposalCountCur, proposalCountPrev);
        }

        public void Draw()
        {
            foreach (var p in Particles)
                Raylib.DrawCircleV(p.Position, 3f, typeColors[p.Type]);

            Overlay.Draw(Particles.Length, TypeCount, "Mode: Standard (CPU)", Color.Green);
        }

        public void GenerateRules()
        {
            // Рандомизируем не только матрицу, но и скалярные параметры — иначе
            // «случайные правила» всегда одинаковы по динамике. Маска Parts
            // позволяет оставить часть параметров как есть (см. меню у кнопки).
            RuleRandomizer.ApplyScalars(this, random);

            bool randomMatrix = RuleRandomizer.Parts.HasFlag(RuleRandomParts.Matrix);
            bool randomConnections = RuleRandomizer.Parts.HasFlag(RuleRandomParts.Connections);

            if (randomConnections)
            {
                // Дерево типов задаёт и связи, и (если включена матрица) правила:
                // так случайные правила дают конечные молекулы, а не одну сеть.
                int[,] connections = RuleRandomizer.NewConnectionTree(random, TypeCount);
                MaxConnections = connections;
                InteractionMatrix = randomMatrix
                    ? RuleRandomizer.NewRuleMatrix(random, connections, TypeCount)
                    : SimulationSettings.ToRealMatrix(InteractionMatrix, null, TypeCount);
            }
            else
            {
                MaxConnections = SimulationSettings.ResizeConnections(MaxConnections, TypeCount);
                InteractionMatrix = randomMatrix
                    ? RuleRandomizer.NewRuleMatrix(random, TypeCount)
                    : SimulationSettings.ToRealMatrix(InteractionMatrix, null, TypeCount);
            }
        }

        public void ApplySettings(SimulationSettings settings)
        {
            InteractionRadius = settings.InteractionRadius;
            ForceMultiplier = settings.ForceMultiplier;
            Friction = settings.Friction;
            MinDistance = settings.MinDistance;
            RepulsionStrength = settings.RepulsionStrength;

            InteractionMatrix = SimulationSettings.ToRealMatrix(
                settings.RealMatrix, settings.ComplexMatrix, TypeCount);

            MaxConnections = SimulationSettings.ResizeConnections(settings.Connections, TypeCount);
            TypePalette.Apply(typeColors, settings.Colors);
        }

        public void GenerateColors()
        {
            for (int i = 0; i < TypeCount; i++)
            {
                typeColors[i] = new Color(
                    random.Next(50, 255),
                    random.Next(50, 255),
                    random.Next(50, 255),
                    255);
            }
        }

        public void AddType()
        {
            // Initialize() пересоздаёт цвета и матрицу связей, а правила не трогает.
            // Снимаем настройки, чтобы вернуть их после: смена числа типов не должна
            // менять физику, новые клетки остаются нулевыми.
            Color[] previousColors = typeColors;
            float[,] previousMatrix = InteractionMatrix;
            int[,] previousConnections = MaxConnections;

            Initialize(Particles.Length, TypeCount + 1);

            TypePalette.Apply(typeColors, previousColors);
            InteractionMatrix = SimulationSettings.ToRealMatrix(previousMatrix, null, TypeCount);
            MaxConnections = SimulationSettings.ResizeConnections(previousConnections, TypeCount);
        }
        
        public void RemoveType()
        {
            if (TypeCount > 1)
            {
                Color[] previousColors = typeColors;
                float[,] previousMatrix = InteractionMatrix;
                int[,] previousConnections = MaxConnections;

                Initialize(Particles.Length, TypeCount - 1);

                TypePalette.Apply(typeColors, previousColors);
                InteractionMatrix = SimulationSettings.ToRealMatrix(previousMatrix, null, TypeCount);
                MaxConnections = SimulationSettings.ResizeConnections(previousConnections, TypeCount);
            }
        }
        
        public void Restart()
        {
            for (int i = 0; i < Particles.Length; i++)
            {
                Particles[i].Position = new Vector2(
                    random.Next(0, screenWidth),
                    random.Next(0, screenHeight));
                Particles[i].Velocity = Vector2.Zero;
            }

            ClearBondState();
        }

        public void SetParticleCount(int newCount)
        {
            if (newCount == Particles.Length) return;
            
            Particle[] newParticles = new Particle[newCount];
            
            int copyCount = Math.Min(newCount, Particles.Length);
            Array.Copy(Particles, newParticles, copyCount);
            
            for (int i = copyCount; i < newCount; i++)
            {
                newParticles[i] = new Particle
                {
                    Position = new Vector2(random.Next(0, screenWidth), random.Next(0, screenHeight)),
                    Velocity = Vector2.Zero,
                    Type = random.Next(0, TypeCount)
                };
            }

            Particles = newParticles;
            nextParticles = new Particle[newCount];
            
            sortedIndexes = new int[newCount];
            bonds = Array.Empty<int>();
            proposalPrev = Array.Empty<int>();
            proposalCur = Array.Empty<int>();
            proposalCountPrev = Array.Empty<int>();
            proposalCountCur = Array.Empty<int>();
            bondSlots = 1;
            connectionHash = 0;
        }

        public void RestoreParticles(Particle[] source)
        {
            int count = Math.Min(source.Length, Particles.Length);
            Array.Copy(source, Particles, count);
            if (nextParticles.Length != Particles.Length)
                nextParticles = new Particle[Particles.Length];
            ClearBondState();
        }

        public void BuildSpatialLookup()
        {
            screenWidth = Raylib.GetScreenWidth();
            screenHeight = Raylib.GetScreenHeight();
            
            int newGridWidth = (int)Math.Ceiling(screenWidth / InteractionRadius);
            int newGridHeight = (int)Math.Ceiling(screenHeight / InteractionRadius);
            
            if (newGridWidth != gridWidth || newGridHeight != gridHeight || cellSize != InteractionRadius)
            {
                gridWidth = newGridWidth;
                gridHeight = newGridHeight;
                cellSize = InteractionRadius;

                sortedIndexes = new int[Particles.Length];
                cellStart = new int[gridWidth * gridHeight + 1];
                cellCount = new int[gridWidth * gridHeight];
                nextPos = new int[gridWidth * gridHeight + 1];
            }

            int totalCells = gridWidth * gridHeight;
            
            Array.Clear(cellCount, 0, totalCells);
            
            for (int i = 0; i < Particles.Length; i++)
            {
                int gx = (int)(Particles[i].Position.X / cellSize);
                int gy = (int)(Particles[i].Position.Y / cellSize);
                gx = (gx + gridWidth) % gridWidth;
                gy = (gy + gridHeight) % gridHeight;
                int cellId = gy * gridWidth + gx;
                cellCount[cellId]++;
            }
            
            cellStart[0] = 0;
            for (int c = 0; c < totalCells; c++)
            {
                cellStart[c + 1] = cellStart[c] + cellCount[c];
            }
            
            Array.Copy(cellStart, nextPos, cellStart.Length);
            
            for (int i = 0; i < Particles.Length; i++)
            {
                int gx = (int)(Particles[i].Position.X / cellSize);
                int gy = (int)(Particles[i].Position.Y / cellSize);
                gx = (gx + gridWidth) % gridWidth;
                gy = (gy + gridHeight) % gridHeight;
                int cellId = gy * gridWidth + gx;

                int pos = nextPos[cellId];
                if (pos >= 0 && pos < sortedIndexes.Length)
                {
                    sortedIndexes[pos] = i;
                    nextPos[cellId]++;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"Ошибка индексации: pos={pos}, cellId={cellId}");
                }
            }
        }

        /// <summary>
        /// Перекладывает матрицу связей в плоские массивы, выясняет, есть ли вообще
        /// ограничения, и держит в согласии буфер запомненных связей: при смене
        /// раскладки он очищается, а не переиспользуется с чужими слотами.
        /// </summary>
        private void PrepareConnectionLimits()
        {
            int typeCount = TypeCount;
            int need = typeCount * typeCount;

            if (maxConnectionsFlat.Length != need)
            {
                maxConnectionsFlat = new int[need];
                connectionSlotBase = new int[need];
                connectionSlotTotal = new int[typeCount];
            }

            hasConnectionLimits = ConnectionLimits.Flatten(
                MaxConnections, typeCount,
                maxConnectionsFlat, connectionSlotBase, connectionSlotTotal);

            int slots = 1;
            for (int t = 0; t < typeCount; t++)
                if (connectionSlotTotal[t] > slots) slots = connectionSlotTotal[t];

            int hash = ConnectionLimits.Hash(maxConnectionsFlat, connectionSlotBase, need);
            int needed = Particles.Length * slots;
            int countsNeeded = Particles.Length * typeCount;

            if (slots != bondSlots || bonds.Length != needed ||
                proposalPrev.Length != needed || proposalCountPrev.Length != countsNeeded)
            {
                bondSlots = slots;
                bonds = new int[needed];
                proposalPrev = new int[needed];
                proposalCur = new int[needed];
                proposalCountPrev = new int[countsNeeded];
                proposalCountCur = new int[countsNeeded];
                connectionHash = hash;
            }
            else if (hash != connectionHash)
            {
                connectionHash = hash;
                ClearBondState();
            }
        }

        private void ClearBondState()
        {
            Array.Clear(bonds, 0, bonds.Length);
            Array.Clear(proposalPrev, 0, proposalPrev.Length);
            Array.Clear(proposalCur, 0, proposalCur.Length);
            Array.Clear(proposalCountPrev, 0, proposalCountPrev.Length);
            Array.Clear(proposalCountCur, 0, proposalCountCur.Length);
        }

        private static NeighbourSelector RentSelector()
            => selectorBuffer ??= new NeighbourSelector();

        private static float WrappedDelta(float delta, float size)
        {
            if (delta > size / 2f) return delta - size;
            if (delta < -size / 2f) return delta + size;
            return delta;
        }

        private static bool IsBonded(int[] bondPartners, int baseSlot, int count, int partner)
        {
            int stored = partner + 1;
            for (int k = 0; k < count; k++)
                if (bondPartners[baseSlot + k] == stored) return true;

            return false;
        }

        /// <summary>
        /// Сила связи как у пружины: 0 на длине покоя (MinDistance), растёт при
        /// растяжении и меняет знак при сжатии. Ограничена единицей, чтобы далёкая
        /// связь не разгоняла частицу бесконечно.
        /// </summary>
        private static float BondFactor(float distance, float rest)
        {
            float stretch = (distance - rest) / rest;
            if (stretch > 1f) return 1f;
            if (stretch < -1f) return -1f;
            return stretch;
        }
    }
}