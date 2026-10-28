using System;
using System.Numerics;
using ILGPU;
using ILGPU.Runtime;
using Raylib_cs;

namespace Genesis.Core
{
    // Структура для передачи параметров в ядро (решает проблему с лимитом Action)
    public struct SimulationParams
    {
        public int GridWidth;
        public int GridHeight;
        public float CellSize;
        public float ScreenWidth;
        public float ScreenHeight;
        public float InteractionRadius;
        public float MinDistance;
        public float RepulsionStrength;
        public float ForceMultiplier;
        public float Friction;
        public int TypeCount;
        public float DeltaTime;
        public int UseConnLimits;
        /// <summary>Слотов связей на частицу (максимум по типам, минимум 1).</summary>
        public int MaxSlots;
        /// <summary>Расстояние, дальше которого запомненная связь рвётся.</summary>
        public float BondBreakDistance;
        /// <summary>Расстояние, ближе которого пара может образовать новую связь.</summary>
        public float BondFormDistance;
    }

    /// <summary>
    /// Буферы заявок на связи, упакованные в один аргумент ядра: у ILGPU есть
    /// ограничение на число параметров, а заявок нужно три (прошлые, их счётчики
    /// и счётчики текущих).
    /// </summary>
    public struct ProposalBuffers
    {
        public ArrayView<int> PrevProposals;
        public ArrayView<int> PrevCounts;
        public ArrayView<int> CurCounts;
    }

    /// <summary>
    /// Текущее и следующее состояние частиц. Ядро читает только Current, а пишет
    /// в Next: иначе поток мог бы прочитать уже сдвинутую позицию соседа, и две
    /// стороны одной связи приняли бы разные решения о разрыве.
    /// </summary>
    public struct ParticleBuffers
    {
        public ArrayView<Particle> Current;
        public ArrayView<Particle> Next;
    }

    /// <summary>
    /// Симуляция на ILGPU.
    ///
    /// Если ILGPU не находит настоящий GPU (CUDA/OpenCL) и отдаёт CPU-акселератор,
    /// класс прозрачно делегирует работу обычной <see cref="Simulation"/>. Это важно:
    /// у ILGPU CPU-акселератор эмулирует один «мультипроцессор», то есть считает
    /// последовательно, и на нём GPU-путь всегда медленнее, чем Parallel.For.
    ///
    /// На реальном GPU состояние частиц постоянно живёт в памяти устройства: на CPU
    /// копируется только раз в кадр для отрисовки, а пространственная сетка строится
    /// ядрами (clear -> count -> prefix sum -> scatter).
    /// </summary>
    public class SimulationGpu : ISimulation, IDisposable
    {
        public float InteractionRadius { get; set; } = 80f;
        public float ForceMultiplier { get; set; } = 40f;
        public float Friction { get; set; } = 0.98f;
        public float MinDistance { get; set; } = 25f;
        public float RepulsionStrength { get; set; } = 25f;

        /// <summary>Имя реально выбранного устройства ILGPU.</summary>
        public string AcceleratorName => _accelerator.Name;

        /// <summary>Тип реально выбранного ускорителя: CPU, OpenCL или Cuda.</summary>
        public AcceleratorType AcceleratorType => _accelerator.AcceleratorType;

        /// <summary>false, если ILGPU не нашёл GPU и работает через CPU-акселератор.</summary>
        public bool IsRealGpu => _useGpu;

        public int TypeCount => _useGpu ? _typeCount : _cpuFallback!.TypeCount;
        public int ParticleCount => _useGpu ? _particles.Length : _cpuFallback!.ParticleCount;
        public Particle[] Particles => _useGpu ? _particles : _cpuFallback!.Particles;
        public float[,] InteractionMatrix => _useGpu ? _interactionMatrix : _cpuFallback!.InteractionMatrix;

        /// <summary>Пределы связей по парам типов (0 — без ограничения).</summary>
        public int[,] MaxConnections => _useGpu ? _maxConnections : _cpuFallback!.MaxConnections;

        /// <summary>Цвета типов частиц; меняются из UI напрямую.</summary>
        public Color[] TypeColors => _useGpu ? typeColors : _cpuFallback!.TypeColors;

        // --- GPU-состояние ---
        private readonly bool _useGpu;
        private readonly Simulation? _cpuFallback;

        private readonly Context _context;
        private readonly Accelerator _accelerator;

        private int _typeCount;
        private Particle[] _particles = Array.Empty<Particle>();
        private float[,] _interactionMatrix = new float[0, 0];
        private int[,] _maxConnections = new int[0, 0];

        private int screenWidth;
        private int screenHeight;

        private Random random = new Random();
        private Color[] typeColors = Array.Empty<Color>();

        private int gridWidth, gridHeight;
        private float cellSize;
        private int totalCells;

        private MemoryBuffer1D<Particle, Stride1D.Dense>? _particleBuffer;
        private MemoryBuffer1D<Particle, Stride1D.Dense>? _nextParticleBuffer;
        private MemoryBuffer1D<float, Stride1D.Dense>? _matrixBuffer;
        private MemoryBuffer1D<int, Stride1D.Dense>? _maxConnBuffer;
        private MemoryBuffer1D<int, Stride1D.Dense>? _slotBaseBuffer;
        private MemoryBuffer1D<int, Stride1D.Dense>? _selIndexBuffer;
        private MemoryBuffer1D<float, Stride1D.Dense>? _selDistanceBuffer;
        private MemoryBuffer1D<int, Stride1D.Dense>? _selMetaBuffer;
        private MemoryBuffer1D<int, Stride1D.Dense>? _selLockedBuffer;
        private MemoryBuffer1D<int, Stride1D.Dense>? _bondBuffer;
        private MemoryBuffer1D<int, Stride1D.Dense>? _proposalBuffer;
        private MemoryBuffer1D<int, Stride1D.Dense>? _proposalCountBuffer;
        private MemoryBuffer1D<int, Stride1D.Dense>? _proposalCountPrevBuffer;
        private MemoryBuffer1D<int, Stride1D.Dense>? _cellCountBuffer;
        private MemoryBuffer1D<int, Stride1D.Dense>? _cellStartBuffer;
        private MemoryBuffer1D<int, Stride1D.Dense>? _nextPosBuffer;
        private MemoryBuffer1D<int, Stride1D.Dense>? _sortedBuffer;

        private readonly Action<Index1D, ArrayView<int>>? _clearCellsKernel;
        private readonly Action<Index1D, ArrayView<Particle>, ArrayView<int>, SimulationParams>? _countCellsKernel;
        private readonly Action<Index1D, ArrayView<int>, ArrayView<int>, ArrayView<int>, int>? _prefixSumKernel;
        private readonly Action<Index1D, ArrayView<Particle>, ArrayView<int>, ArrayView<int>, SimulationParams>? _scatterKernel;
        private readonly Action<Index1D, ParticleBuffers, ArrayView<float>, ArrayView<int>,
            ArrayView<int>, ArrayView<int>, ArrayView<float>, ArrayView<int>,
            ArrayView<int>, ArrayView<int>,
            ProposalBuffers, ArrayView<int>, ArrayView<int>,
            SimulationParams>? _updateKernel;
        // GPU-буфер частиц — источник истины. Заливать с CPU нужно только после того,
        // как CPU изменил массив (Initialize / Restart / SetParticleCount / смена типов).
        private bool _gpuParticlesDirty = true;
        private float[] _flatMatrix = Array.Empty<float>();
        private int[] _flatMaxConn = Array.Empty<int>();
        private int[] _flatSlotBase = Array.Empty<int>();
        private int[] _flatSlotTotal = Array.Empty<int>();
        private bool _hasConnLimits;
        private int _maxSlots = 1;
        private int _bondHash;

        public SimulationGpu()
        {
            _context = Context.CreateDefault();
            _accelerator = _context.GetPreferredDevice(preferCPU: false).CreateAccelerator(_context);

            // GENESIS_FORCE_ILGPU=1 — отладка: гонять ILGPU-ядра даже на CPU-акселераторе.
            bool force = Environment.GetEnvironmentVariable("GENESIS_FORCE_ILGPU") == "1";
            _useGpu = _accelerator.AcceleratorType != AcceleratorType.CPU || force;

            if (!_useGpu)
            {
                _cpuFallback = new Simulation();
                Console.WriteLine("[Genesis] GPU не выбран, считаю через Simulation (Parallel.For).");
                Console.WriteLine(GpuDiagnostics.Describe());
                return;
            }

            Console.WriteLine($"[Genesis] ILGPU: {_accelerator.AcceleratorType} / {_accelerator.Name}");

            _clearCellsKernel = _accelerator.LoadAutoGroupedStreamKernel
                <Index1D, ArrayView<int>>(ClearCellsKernel);
            _countCellsKernel = _accelerator.LoadAutoGroupedStreamKernel
                <Index1D, ArrayView<Particle>, ArrayView<int>, SimulationParams>(CountCellsKernel);
            _prefixSumKernel = _accelerator.LoadAutoGroupedStreamKernel
                <Index1D, ArrayView<int>, ArrayView<int>, ArrayView<int>, int>(PrefixSumKernel);
            _scatterKernel = _accelerator.LoadAutoGroupedStreamKernel
                <Index1D, ArrayView<Particle>, ArrayView<int>, ArrayView<int>, SimulationParams>(ScatterKernel);
            _updateKernel = _accelerator.LoadAutoGroupedStreamKernel
                <Index1D, ParticleBuffers, ArrayView<float>, ArrayView<int>,
                 ArrayView<int>, ArrayView<int>, ArrayView<float>, ArrayView<int>,
                 ArrayView<int>, ArrayView<int>,
                 ProposalBuffers, ArrayView<int>, ArrayView<int>,
                 SimulationParams>(UpdateParticlesKernel);
        }

        public void Initialize(int particleCount, int typeCount)
        {
            if (!_useGpu)
            {
                _cpuFallback!.Initialize(particleCount, typeCount);
                PushParamsToFallback();
                return;
            }

            screenWidth = Raylib.GetScreenWidth();
            screenHeight = Raylib.GetScreenHeight();

            random = new Random();

            _particles = new Particle[particleCount];
            _typeCount = typeCount;

            cellSize = InteractionRadius;
            gridWidth = GridSize(screenWidth, cellSize);
            gridHeight = GridSize(screenHeight, cellSize);
            totalCells = gridWidth * gridHeight;

            typeColors = new Color[typeCount];
            TypePalette.Reset(typeColors);

            // Нулевая матрица правил: GenerateRules() заменит её случайной, но
            // UI и загрузка пресета не должны зависеть от того, вызывали ли его.
            _interactionMatrix = new float[typeCount, typeCount];

            // По умолчанию связей не ограничиваем: 0 = без ограничения.
            _maxConnections = new int[typeCount, typeCount];

            for (int i = 0; i < particleCount; i++)
            {
                _particles[i] = new Particle
                {
                    Position = new Vector2(
                        random.Next(0, screenWidth),
                        random.Next(0, screenHeight)),
                    Velocity = Vector2.Zero,
                    Type = random.Next(0, typeCount)
                };
            }

            AllocateGpuBuffers();
            _gpuParticlesDirty = true;
        }

        public void Update(float deltaTime)
        {
            if (!_useGpu)
            {
                PushParamsToFallback();
                _cpuFallback!.Update(deltaTime);
                return;
            }

            SyncGridWithSettings();

            // Частицы заливаются только после изменений на стороне CPU, а не каждый кадр.
            if (_gpuParticlesDirty)
            {
                _particleBuffer!.CopyFromCPU(_particles);
                _gpuParticlesDirty = false;
            }

            UploadInteractionMatrix();
            UploadMaxConnections();

            float bondRest = MathF.Max(MinDistance, 1f);
            float bondFormDistance = MathF.Min(bondRest * Bonds.FormDistanceFactor, InteractionRadius);

            var p = new SimulationParams
            {
                GridWidth = gridWidth,
                GridHeight = gridHeight,
                CellSize = cellSize,
                ScreenWidth = screenWidth,
                ScreenHeight = screenHeight,
                InteractionRadius = InteractionRadius,
                MinDistance = MinDistance,
                RepulsionStrength = RepulsionStrength,
                ForceMultiplier = ForceMultiplier,
                Friction = Friction,
                TypeCount = _typeCount,
                DeltaTime = deltaTime,
                UseConnLimits = _hasConnLimits ? 1 : 0,
                MaxSlots = _maxSlots,
                BondBreakDistance = bondRest * Bonds.BreakDistanceFactor,
                BondFormDistance = bondFormDistance,
            };

            // Вся подготовка кадра (сетка + физика) — на устройстве.
            _clearCellsKernel!(totalCells, _cellCountBuffer!.View);
            _countCellsKernel!(_particles.Length, _particleBuffer!.View, _cellCountBuffer.View, p);
            _prefixSumKernel!(1, _cellCountBuffer.View, _cellStartBuffer!.View, _nextPosBuffer!.View, totalCells);
            _scatterKernel!(_particles.Length, _particleBuffer.View, _nextPosBuffer.View, _sortedBuffer!.View, p);
            _updateKernel!(_particles.Length,
                new ParticleBuffers
                {
                    Current = _particleBuffer.View,
                    Next = _nextParticleBuffer!.View,
                },
                _matrixBuffer!.View,
                _maxConnBuffer!.View, _slotBaseBuffer!.View,
                _selIndexBuffer!.View, _selDistanceBuffer!.View,
                _selMetaBuffer!.View, _selLockedBuffer!.View, _bondBuffer!.View,
                new ProposalBuffers
                {
                    PrevProposals = _proposalBuffer!.View,
                    PrevCounts = _proposalCountPrevBuffer!.View,
                    CurCounts = _proposalCountBuffer!.View,
                },
                _sortedBuffer.View, _cellStartBuffer.View, p);

            // Ядро писало в Next, а не в Current: подменяем буферы, чтобы дальше
            // читать и копировать на CPU уже новое состояние кадра.
            (_particleBuffer, _nextParticleBuffer) = (_nextParticleBuffer, _particleBuffer);

            // Текущие заявки лежат в selIndex, прошлые — в proposalBuffer. Меняем
            // их местами: следующий кадр прочитает только что записанные заявки.
            (_selIndexBuffer, _proposalBuffer) = (_proposalBuffer, _selIndexBuffer);
            (_proposalCountBuffer, _proposalCountPrevBuffer) =
                (_proposalCountPrevBuffer, _proposalCountBuffer);

            // Один синк на кадр: CopyToCPU сам дожидается завершения ядер.
            // Копия нужна только потому, что Raylib рисует на CPU.
            _particleBuffer.CopyToCPU(_particles);
        }

        private void PushParamsToFallback()
        {
            var s = _cpuFallback!;
            s.InteractionRadius = InteractionRadius;
            s.ForceMultiplier = ForceMultiplier;
            s.Friction = Friction;
            s.MinDistance = MinDistance;
            s.RepulsionStrength = RepulsionStrength;
        }

        /// <summary>
        /// Обратное к <see cref="PushParamsToFallback"/>: забирает параметры, которые
        /// изменил CPU-бэкенд (например, рандомизатор правил), чтобы UI и следующий
        /// кадр видели актуальные значения.
        /// </summary>
        private void PullParamsFromFallback()
        {
            var s = _cpuFallback!;
            InteractionRadius = s.InteractionRadius;
            ForceMultiplier = s.ForceMultiplier;
            Friction = s.Friction;
            MinDistance = s.MinDistance;
            RepulsionStrength = s.RepulsionStrength;
        }

        private static int GridSize(int screenSize, float cell)
            => Math.Max(1, (int)Math.Ceiling(screenSize / cell));

        private void AllocateGpuBuffers()
        {
            _particleBuffer?.Dispose();
            _nextParticleBuffer?.Dispose();
            _matrixBuffer?.Dispose();
            _maxConnBuffer?.Dispose();
            _slotBaseBuffer?.Dispose();
            _cellCountBuffer?.Dispose();
            _cellStartBuffer?.Dispose();
            _nextPosBuffer?.Dispose();
            _sortedBuffer?.Dispose();

            _particleBuffer = _accelerator.Allocate1D<Particle>(_particles.Length);
            _nextParticleBuffer = _accelerator.Allocate1D<Particle>(_particles.Length);
            _sortedBuffer = _accelerator.Allocate1D<int>(_particles.Length);
            _cellCountBuffer = _accelerator.Allocate1D<int>(totalCells);
            _cellStartBuffer = _accelerator.Allocate1D<int>(totalCells + 1);
            _nextPosBuffer = _accelerator.Allocate1D<int>(totalCells + 1);
            _matrixBuffer = _accelerator.Allocate1D<float>(_typeCount * _typeCount);

            _maxConnBuffer = _accelerator.Allocate1D<int>(_typeCount * _typeCount);
            _slotBaseBuffer = _accelerator.Allocate1D<int>(_typeCount * _typeCount);

            _flatMatrix = new float[_typeCount * _typeCount];
            _flatMaxConn = new int[_typeCount * _typeCount];
            _flatSlotBase = new int[_typeCount * _typeCount];
            _flatSlotTotal = new int[_typeCount];

            AllocateConnectionScratch();
        }

        /// <summary>
        /// Скретч выбора ближайших соседей: строка на частицу, поэтому потоки не
        /// пересекаются и атомики не нужны. Размер зависит от суммы пределов в
        /// матрице связей, поэтому перевыделяется только при её изменении.
        /// </summary>
        private void AllocateConnectionScratch()
        {
            _selIndexBuffer?.Dispose();
            _selDistanceBuffer?.Dispose();
            _selMetaBuffer?.Dispose();
            _selLockedBuffer?.Dispose();
            _bondBuffer?.Dispose();
            _proposalBuffer?.Dispose();
            _proposalCountBuffer?.Dispose();
            _proposalCountPrevBuffer?.Dispose();

            int slots = Math.Max(1, _maxSlots);
            int types = Math.Max(1, _typeCount);
            _selIndexBuffer = _accelerator.Allocate1D<int>(_particles.Length * slots);
            _selDistanceBuffer = _accelerator.Allocate1D<float>(_particles.Length * slots);
            _selMetaBuffer = _accelerator.Allocate1D<int>(_particles.Length * types);
            _selLockedBuffer = _accelerator.Allocate1D<int>(_particles.Length * types);

            // 0 — свободный слот, иначе индекс партнёра + 1. Нулевая заливка
            // обязательна: Allocate1D не гарантирует чистое содержимое.
            int bondCount = _particles.Length * slots;
            _bondBuffer = _accelerator.Allocate1D<int>(bondCount);
            _bondBuffer.CopyFromCPU(new int[bondCount]);

            // Заявки на связи: prev читается, cur (selIndex) пишется, после кадра
            // буферы меняются местами. Счётчики — отдельно, потому что selMeta
            // хранит ещё и занятые существующими связями слоты.
            _proposalBuffer = _accelerator.Allocate1D<int>(bondCount);
            _proposalBuffer.CopyFromCPU(new int[bondCount]);
            _proposalCountBuffer = _accelerator.Allocate1D<int>(_particles.Length * types);
            _proposalCountBuffer.CopyFromCPU(new int[_particles.Length * types]);
            _proposalCountPrevBuffer = _accelerator.Allocate1D<int>(_particles.Length * types);
            _proposalCountPrevBuffer.CopyFromCPU(new int[_particles.Length * types]);
            _bondHash = 0;
        }

        private void UploadInteractionMatrix()
        {
            // Матрицу правил правит и UI (ImGui пишет прямо в массив), поэтому заливаем
            // каждый кадр. Это TypeCount^2 чисел и переиспользуемый буфер — дёшево.
            // Если GenerateRules() ещё не вызывался, заливаем нули: иначе на устройстве
            // останется неинициализированный буфер и физика станет непредсказуемой.
            Array.Clear(_flatMatrix, 0, _flatMatrix.Length);

            if (_interactionMatrix != null &&
                _interactionMatrix.GetLength(0) >= _typeCount &&
                _interactionMatrix.GetLength(1) >= _typeCount)
            {
                for (int i = 0; i < _typeCount; i++)
                    for (int j = 0; j < _typeCount; j++)
                        _flatMatrix[i * _typeCount + j] = _interactionMatrix[i, j];
            }

            _matrixBuffer!.CopyFromCPU(_flatMatrix);
        }

        /// <summary>
        /// Перекладывает матрицу пределов связей в плоские массивы для ядра и заодно
        /// выясняет, есть ли ограничения вообще (иначе ядро не тратит на них время).
        /// </summary>
        private void UploadMaxConnections()
        {
            int need = _typeCount * _typeCount;
            if (_flatMaxConn.Length != need)
            {
                _flatMaxConn = new int[need];
                _flatSlotBase = new int[need];
                _flatSlotTotal = new int[_typeCount];
            }

            _hasConnLimits = ConnectionLimits.Flatten(
                _maxConnections, _typeCount,
                _flatMaxConn, _flatSlotBase, _flatSlotTotal);

            _maxConnBuffer!.CopyFromCPU(_flatMaxConn);
            _slotBaseBuffer!.CopyFromCPU(_flatSlotBase);

            // Размер скретча зависит от матрицы: пользователь может поднять пределы
            // прямо в UI, поэтому проверяем каждый кадр (это T чисел).
            int maxSlots = 1;
            for (int i = 0; i < _typeCount; i++)
                if (_flatSlotTotal[i] > maxSlots) maxSlots = _flatSlotTotal[i];

            bool reallocated = false;
            if (maxSlots != _maxSlots)
            {
                _maxSlots = maxSlots;
                AllocateConnectionScratch();
                reallocated = true;
            }

            // Правка матрицы в UI меняет раскладку слотов: старые связи могут
            // оказаться в чужих блоках, поэтому сбрасываем их.
            int hash = ConnectionLimits.Hash(_flatMaxConn, _flatSlotBase, need);
            if (reallocated)
            {
                _bondHash = hash;
            }
            else if (hash != _bondHash)
            {
                _bondHash = hash;
                ClearBonds();
            }
        }

        private void ClearBonds()
        {
            if (!_useGpu || _bondBuffer == null)
                return;

            int count = _particles.Length * Math.Max(1, _maxSlots);
            _bondBuffer.CopyFromCPU(new int[count]);

            int metaCount = _particles.Length * Math.Max(1, _typeCount);
            _proposalCountBuffer?.CopyFromCPU(new int[metaCount]);
            _proposalCountPrevBuffer?.CopyFromCPU(new int[metaCount]);
        }

        private void SyncGridWithSettings()
        {
            int w = Raylib.GetScreenWidth();
            int h = Raylib.GetScreenHeight();
            int gw = GridSize(w, InteractionRadius);
            int gh = GridSize(h, InteractionRadius);

            if (gw == gridWidth && gh == gridHeight && MathF.Abs(cellSize - InteractionRadius) < 0.001f)
                return;

            screenWidth = w;
            screenHeight = h;
            gridWidth = gw;
            gridHeight = gh;
            cellSize = InteractionRadius;
            totalCells = gw * gh;

            AllocateGpuBuffers();
            _gpuParticlesDirty = true;
        }

        // --- ЯДРА ---

        static int CellIndex(float x, float y, SimulationParams p)
        {
            int gx = (int)(x / p.CellSize);
            int gy = (int)(y / p.CellSize);
            gx = (gx + p.GridWidth) % p.GridWidth;
            gy = (gy + p.GridHeight) % p.GridHeight;
            return gy * p.GridWidth + gx;
        }

        static void ClearCellsKernel(Index1D index, ArrayView<int> cellCount)
        {
            cellCount[index] = 0;
        }

        static void CountCellsKernel(
            Index1D index,
            ArrayView<Particle> particles,
            ArrayView<int> cellCount,
            SimulationParams p)
        {
            Particle a = particles[index];
            int cell = CellIndex(a.Position.X, a.Position.Y, p);
            Atomic.Add(ref cellCount[cell], 1);
        }

        // Один рабочий элемент: ячеек сотни, последовательный проход дешевле отдельного
        // ядра сканирования и не требует синхронизации с CPU.
        static void PrefixSumKernel(
            Index1D _,
            ArrayView<int> cellCount,
            ArrayView<int> cellStart,
            ArrayView<int> nextPos,
            int totalCells)
        {
            int sum = 0;
            cellStart[0] = 0;
            nextPos[0] = 0;

            for (int i = 0; i < totalCells; i++)
            {
                sum += cellCount[i];
                cellStart[i + 1] = sum;
                nextPos[i + 1] = sum;
            }
        }

        static void ScatterKernel(
            Index1D index,
            ArrayView<Particle> particles,
            ArrayView<int> nextPos,
            ArrayView<int> sortedIndexes,
            SimulationParams p)
        {
            Particle a = particles[index];
            int cell = CellIndex(a.Position.X, a.Position.Y, p);
            int slot = Atomic.Add(ref nextPos[cell], 1);
            sortedIndexes[slot] = index;
        }

        static bool OfferConnection(
            int type,
            int partner,
            float distance,
            int limit,
            int locked,
            int slotBase,
            int rowBase,
            int metaBase,
            ArrayView<int> selIndex,
            ArrayView<float> selDistance,
            ArrayView<int> selMeta)
        {
            int meta = selMeta[metaBase + type];
            int count = meta & 0xFFFF;
            int worstSlot = meta >> 16;
            int typeSlotBase = rowBase + slotBase;

            if (count < limit)
            {
                int slot = typeSlotBase + count;
                selDistance[slot] = distance;
                selIndex[slot] = partner;

                // Самый «плохой» новый слот держим в старших битах meta.
                // Существующие связи (0..locked) в пересчёт не входят.
                if (count == locked)
                    worstSlot = count;
                else if (distance > selDistance[typeSlotBase + worstSlot])
                    worstSlot = count;

                selMeta[metaBase + type] = (count + 1) | (worstSlot << 16);
                return true;
            }

            // Свободных слотов нет. Существующие связи не вытесняются;
            // заменяем только самый дальний из новых.
            if (count == locked)
                return false;

            if (distance >= selDistance[typeSlotBase + worstSlot])
                return false;

            int target = typeSlotBase + worstSlot;
            selDistance[target] = distance;
            selIndex[target] = partner;

            float newWorst = selDistance[typeSlotBase + locked];
            int newWorstSlot = locked;
            for (int k = locked + 1; k < limit; k++)
            {
                float d = selDistance[typeSlotBase + k];
                if (d > newWorst)
                {
                    newWorst = d;
                    newWorstSlot = k;
                }
            }

            selMeta[metaBase + type] = count | (newWorstSlot << 16);
            return true;
        }

        static bool IsBonded(ArrayView<int> bonds, int baseSlot, int count, int partner)
        {
            int stored = partner + 1;
            for (int k = 0; k < count; k++)
                if (bonds[baseSlot + k] == stored) return true;

            return false;
        }

        static float Wrapped(float delta, float size)
        {
            if (delta > size / 2f) return delta - size;
            if (delta < -size / 2f) return delta + size;
            return delta;
        }

        /// <summary>
        /// Пружинная сила связи: 0 на длине покоя, ±1 на пределе растяжения/сжатия.
        /// </summary>
        static float BondFactor(float distance, float rest)
        {
            float stretch = (distance - rest) / rest;
            if (stretch > 1f) return 1f;
            if (stretch < -1f) return -1f;
            return stretch;
        }

        static void UpdateParticlesKernel(
            Index1D index,
            ParticleBuffers particles,
            ArrayView<float> interactionMatrix,
            ArrayView<int> maxConnections,
            ArrayView<int> slotBase,
            ArrayView<int> selIndex,
            ArrayView<float> selDistance,
            ArrayView<int> selMeta,
            ArrayView<int> selLocked,
            ArrayView<int> bonds,
            ProposalBuffers proposals,
            ArrayView<int> sortedIndexes,
            ArrayView<int> cellStart,
            SimulationParams p)
        {
            Particle a = particles.Current[index];
            float ax = a.Position.X;
            float ay = a.Position.Y;
            float accX = 0f;
            float accY = 0f;

            int typeCount = p.TypeCount;
            bool limited = p.UseConnLimits != 0;
            int rowBase = index * p.MaxSlots;
            int metaBase = index * typeCount;
            int bondBase = rowBase;
            float breakDistanceSq = p.BondBreakDistance * p.BondBreakDistance;
            float formDistanceSq = p.BondFormDistance * p.BondFormDistance;
            float bondRest = p.MinDistance > 1f ? p.MinDistance : 1f;

            for (int t = 0; t < typeCount; t++)
                proposals.CurCounts[metaBase + t] = 0;

            // Строка выбора связей этой частицы. Обнуляем только её — глобальная
            // зачистка не нужна, каждый поток пишет и читает лишь свою строку.
            if (limited)
            {
                for (int t = 0; t < typeCount; t++)
                {
                    selMeta[metaBase + t] = 0;
                    selLocked[metaBase + t] = 0;
                }

                // Проверяем запомненные связи и уплотняем их: партнёр мог уехать
                // дальше радиуса разрыва или правило могло перестать притягивать.
                for (int t = 0; t < typeCount; t++)
                {
                    int margin = a.Type * typeCount + t;
                    int limit = maxConnections[margin];
                    if (limit <= 0) continue;

                    int baseSlot = slotBase[margin];
                    float rule = interactionMatrix[margin];
                    int write = 0;

                    if (rule > 0f)
                    {
                        for (int k = 0; k < limit; k++)
                        {
                            int stored = bonds[bondBase + baseSlot + k];
                            if (stored == 0) continue;

                            int j = stored - 1;
                            if (j < 0 || j >= particles.Current.Length) continue;

                            Particle b = particles.Current[j];
                            if (b.Type != t) continue;

                            float dxPos = Wrapped(b.Position.X - ax, p.ScreenWidth);
                            float dyPos = Wrapped(b.Position.Y - ay, p.ScreenHeight);
                            float distanceSq = dxPos * dxPos + dyPos * dyPos;
                            if (distanceSq <= 0f || distanceSq > breakDistanceSq) continue;

                            bonds[bondBase + baseSlot + write] = stored;
                            write++;

                            // Пружина с длиной покоя MinDistance.
                            float distance = MathF.Sqrt(distanceSq);
                            float strength = rule * p.ForceMultiplier * BondFactor(distance, bondRest);
                            accX += dxPos / distance * strength;
                            accY += dyPos / distance * strength;
                        }
                    }

                    for (int k = write; k < limit; k++)
                        bonds[bondBase + baseSlot + k] = 0;

                    selLocked[metaBase + t] = write;
                    selMeta[metaBase + t] = write;
                }

                // Заявки прошлого кадра становятся связями только при взаимном
                // выборе. Иначе заявка истекает и слот остаётся свободным.
                for (int t = 0; t < typeCount; t++)
                {
                    int margin = a.Type * typeCount + t;
                    int limit = maxConnections[margin];
                    if (limit <= 0) continue;

                    int baseSlot = slotBase[margin];
                    float rule = interactionMatrix[margin];
                    int locked = selLocked[metaBase + t];
                    int promoted = 0;

                    if (rule > 0f)
                    {
                        int prevCount = proposals.PrevCounts[metaBase + t];
                        for (int k = 0; k < prevCount && locked + promoted < limit; k++)
                        {
                            int stored = proposals.PrevProposals[bondBase + baseSlot + k];
                            if (stored == 0) continue;

                            int j = stored - 1;
                            if (j < 0 || j >= particles.Current.Length) continue;

                            Particle b = particles.Current[j];
                            if (b.Type != t) continue;

                            // Взаимное притяжение: иначе это не связь, а
                            // одностороннее влечение, которое копит мусор.
                            if (interactionMatrix[b.Type * typeCount + a.Type] <= 0f) continue;

                            int partnerBase = slotBase[b.Type * typeCount + a.Type];
                            int partnerCount = proposals.PrevCounts[j * typeCount + a.Type];
                            bool mutual = false;
                            for (int q = 0; q < partnerCount; q++)
                            {
                                if (proposals.PrevProposals[j * p.MaxSlots + partnerBase + q] == index + 1)
                                {
                                    mutual = true;
                                    break;
                                }
                            }
                            if (!mutual) continue;

                            float dxPos = Wrapped(b.Position.X - ax, p.ScreenWidth);
                            float dyPos = Wrapped(b.Position.Y - ay, p.ScreenHeight);
                            float distanceSq = dxPos * dxPos + dyPos * dyPos;
                            if (distanceSq <= 0f || distanceSq > formDistanceSq)
                                continue;

                            if (IsBonded(bonds, bondBase + baseSlot, locked + promoted, j))
                                continue;

                            bonds[bondBase + baseSlot + locked + promoted] = j + 1;
                            promoted++;

                            float distance = MathF.Sqrt(distanceSq);
                            float strength = rule * p.ForceMultiplier * BondFactor(distance, bondRest);
                            accX += dxPos / distance * strength;
                            accY += dyPos / distance * strength;
                        }
                    }

                    selLocked[metaBase + t] = locked + promoted;
                    selMeta[metaBase + t] = locked + promoted;
                }
            }

            int cell = CellIndex(ax, ay, p);
            int gx = cell % p.GridWidth;
            int gy = cell / p.GridWidth;

            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    int nx = (gx + dx + p.GridWidth) % p.GridWidth;
                    int ny = (gy + dy + p.GridHeight) % p.GridHeight;
                    int ncell = ny * p.GridWidth + nx;

                    int start = cellStart[ncell];
                    int end = cellStart[ncell + 1];

                    for (int idx = start; idx < end; idx++)
                    {
                        int j = sortedIndexes[idx];
                        if (index == j) continue;

                        Particle b = particles.Current[j];
                        float dxPos = Wrapped(b.Position.X - ax, p.ScreenWidth);
                        float dyPos = Wrapped(b.Position.Y - ay, p.ScreenHeight);

                        float distanceSq = dxPos * dxPos + dyPos * dyPos;
                        if (distanceSq > 0 && distanceSq < p.InteractionRadius * p.InteractionRadius)
                        {
                            float distance = MathF.Sqrt(distanceSq);
                            float dirX = dxPos / distance;
                            float dirY = dyPos / distance;

                            if (distance < p.MinDistance)
                            {
                                float repel = (1f - distance / p.MinDistance) * p.RepulsionStrength;
                                accX -= dirX * repel;
                                accY -= dirY * repel;
                                continue;
                            }

                            int margin = a.Type * typeCount;
                            int limit = limited ? maxConnections[margin + b.Type] : 0;
                            float rule = interactionMatrix[margin + b.Type];

                            // «none»: связи с этим типом нет. Отрицательное правило
                            // всё ещё отталкивает, положительное не тянет.
                            if (limit < 0)
                            {
                                if (rule < 0f)
                                {
                                    float repelRule = rule * (1f - distance / p.InteractionRadius) * p.ForceMultiplier;
                                    accX += dirX * repelRule;
                                    accY += dirY * repelRule;
                                }

                                continue;
                            }

                            // Связи ограничивают только притяжение (rule > 0).
                            // Отталкивание работает всегда: заполненная структура
                            // должна уметь оттолкнуть лишнюю частицу.
                            if (limit > 0 && rule > 0f)
                            {
                                int baseSlot = slotBase[margin + b.Type];
                                int locked = selLocked[metaBase + b.Type];

                                // Уже связанного партнёра не предлагаем повторно.
                                if (!IsBonded(bonds, bondBase + baseSlot, locked, j))
                                {
                                    OfferConnection(
                                        b.Type, j, distance, limit, locked,
                                        baseSlot, rowBase, metaBase,
                                        selIndex, selDistance, selMeta);
                                }

                                continue;
                            }

                            float strength = rule * (1f - distance / p.InteractionRadius) * p.ForceMultiplier;
                            accX += dirX * strength;
                            accY += dirY * strength;
                        }
                    }
                }
            }

            // Заявки этого кадра пойдут на проверку взаимности в следующем.
            // Сжимаем их к началу блока типа: locked-слоты уже заняты связями.
            if (limited)
            {
                for (int t = 0; t < typeCount; t++)
                {
                    int margin = a.Type * typeCount + t;
                    int limit = maxConnections[margin];
                    if (limit <= 0) continue;

                    int locked = selLocked[metaBase + t];
                    int count = selMeta[metaBase + t] & 0xFFFF;
                    int n = count - locked;
                    if (n < 0) n = 0;

                    int baseSlot = slotBase[margin];
                    proposals.CurCounts[metaBase + t] = n;

                    for (int k = 0; k < n; k++)
                    {
                        int dst = rowBase + baseSlot + k;
                        int src = rowBase + baseSlot + locked + k;
                        selIndex[dst] = selIndex[src] + 1;
                    }
                }
            }

            float vx = a.Velocity.X + accX * p.DeltaTime;
            float vy = a.Velocity.Y + accY * p.DeltaTime;
            vx *= p.Friction;
            vy *= p.Friction;

            float maxSpeed = 200f;
            float speedSq = vx * vx + vy * vy;
            if (speedSq > maxSpeed * maxSpeed)
            {
                float speed = MathF.Sqrt(speedSq);
                vx = (vx / speed) * maxSpeed;
                vy = (vy / speed) * maxSpeed;
            }

            ax += vx * p.DeltaTime;
            ay += vy * p.DeltaTime;

            if (ax < 0) ax += p.ScreenWidth;
            if (ax > p.ScreenWidth) ax -= p.ScreenWidth;
            if (ay < 0) ay += p.ScreenHeight;
            if (ay > p.ScreenHeight) ay -= p.ScreenHeight;

            Vector2 newPos = default;
            newPos.X = ax;
            newPos.Y = ay;
            a.Position = newPos;

            Vector2 newVel = default;
            newVel.X = vx;
            newVel.Y = vy;
            a.Velocity = newVel;

            particles.Next[index] = a;
        }

        public void Draw()
        {
            if (!_useGpu)
            {
                foreach (var p in _cpuFallback!.Particles)
                    Raylib.DrawCircleV(p.Position, 3f, _cpuFallback.TypeColors[p.Type]);

                Overlay.Draw(_cpuFallback.ParticleCount, _cpuFallback.TypeCount,
                    $"Mode: CPU fallback ({AcceleratorName})", Color.Orange);
                return;
            }

            foreach (var p in _particles)
                Raylib.DrawCircleV(p.Position, 3f, typeColors[p.Type]);

            Overlay.Draw(_particles.Length, _typeCount,
                $"Mode: {AcceleratorType} / {AcceleratorName}", Color.Green);
        }

        public void GenerateRules()
        {
            if (!_useGpu)
            {
                // Рандомизатор меняет и скалярные параметры — их нужно забрать назад,
                // иначе PushParamsToFallback() в Update перезапишет их старыми.
                _cpuFallback!.GenerateRules();
                PullParamsFromFallback();
                return;
            }

            // Рандомизируем не только матрицу, но и скалярные параметры — иначе
            // «случайные правила» всегда одинаковы по динамике. Маска Parts
            // позволяет оставить часть параметров как есть (см. меню у кнопки).
            RuleRandomizer.ApplyScalars(this, random);

            bool randomMatrix = RuleRandomizer.Parts.HasFlag(RuleRandomParts.Matrix);
            bool randomConnections = RuleRandomizer.Parts.HasFlag(RuleRandomParts.Connections);

            if (randomConnections)
            {
                int[,] connections = RuleRandomizer.NewConnectionTree(random, _typeCount);
                _maxConnections = connections;
                _interactionMatrix = randomMatrix
                    ? RuleRandomizer.NewRuleMatrix(random, connections, _typeCount)
                    : SimulationSettings.ToRealMatrix(_interactionMatrix, null, _typeCount);
            }
            else
            {
                _maxConnections = SimulationSettings.ResizeConnections(_maxConnections, _typeCount);
                _interactionMatrix = randomMatrix
                    ? RuleRandomizer.NewRuleMatrix(random, _typeCount)
                    : SimulationSettings.ToRealMatrix(_interactionMatrix, null, _typeCount);
            }
        }

        public void ApplySettings(SimulationSettings settings)
        {
            if (!_useGpu)
            {
                // Параметры на GPU-обёртке дублируют запасной бэкенд; применяем
                // снимок там, где реально считается физика, и забираем обратно.
                _cpuFallback!.ApplySettings(settings);
                PullParamsFromFallback();
                return;
            }

            InteractionRadius = settings.InteractionRadius;
            ForceMultiplier = settings.ForceMultiplier;
            Friction = settings.Friction;
            MinDistance = settings.MinDistance;
            RepulsionStrength = settings.RepulsionStrength;

            _interactionMatrix = SimulationSettings.ToRealMatrix(
                settings.RealMatrix, settings.ComplexMatrix, _typeCount);

            _maxConnections = SimulationSettings.ResizeConnections(
                settings.Connections, _typeCount);

            TypePalette.Apply(typeColors, settings.Colors);
        }

        public void GenerateColors()
        {
            if (!_useGpu) { _cpuFallback!.GenerateColors(); return; }

            for (int i = 0; i < _typeCount; i++)
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
            if (!_useGpu) { _cpuFallback!.AddType(); return; }

            // См. Simulation.AddType: смена числа типов не должна менять правила.
            Color[] previousColors = typeColors;
            float[,] previousMatrix = _interactionMatrix;
            int[,] previousConnections = _maxConnections;

            Initialize(_particles.Length, _typeCount + 1);

            TypePalette.Apply(typeColors, previousColors);
            _interactionMatrix = SimulationSettings.ToRealMatrix(previousMatrix, null, _typeCount);
            _maxConnections = SimulationSettings.ResizeConnections(previousConnections, _typeCount);
        }

        public void RemoveType()
        {
            if (!_useGpu) { _cpuFallback!.RemoveType(); return; }

            if (_typeCount > 1)
            {
                Color[] previousColors = typeColors;
                float[,] previousMatrix = _interactionMatrix;
                int[,] previousConnections = _maxConnections;

                Initialize(_particles.Length, _typeCount - 1);

                TypePalette.Apply(typeColors, previousColors);
                _interactionMatrix = SimulationSettings.ToRealMatrix(previousMatrix, null, _typeCount);
                _maxConnections = SimulationSettings.ResizeConnections(previousConnections, _typeCount);
            }
        }

        public void Restart()
        {
            if (!_useGpu) { _cpuFallback!.Restart(); return; }

            for (int i = 0; i < _particles.Length; i++)
            {
                _particles[i].Position = new Vector2(
                    random.Next(0, screenWidth),
                    random.Next(0, screenHeight));
                _particles[i].Velocity = Vector2.Zero;
            }

            ClearBonds();
            _gpuParticlesDirty = true;
        }

        public void RestoreParticles(Particle[] source)
        {
            if (!_useGpu) { _cpuFallback!.RestoreParticles(source); return; }

            int count = Math.Min(source.Length, _particles.Length);
            Array.Copy(source, _particles, count);
            ClearBonds();
            _gpuParticlesDirty = true;
        }

        public void SetParticleCount(int newCount)
        {
            if (!_useGpu) { _cpuFallback!.SetParticleCount(newCount); return; }

            if (newCount == _particles.Length) return;

            Particle[] newParticles = new Particle[newCount];
            int copyCount = Math.Min(newCount, _particles.Length);
            Array.Copy(_particles, newParticles, copyCount);

            for (int i = copyCount; i < newCount; i++)
            {
                newParticles[i] = new Particle
                {
                    Position = new Vector2(random.Next(0, screenWidth), random.Next(0, screenHeight)),
                    Velocity = Vector2.Zero,
                    Type = random.Next(0, _typeCount)
                };
            }

            _particles = newParticles;
            AllocateGpuBuffers();
            _gpuParticlesDirty = true;
        }

        public void Dispose()
        {
            if (_useGpu)
            {
                _particleBuffer?.Dispose();
                _nextParticleBuffer?.Dispose();
                _matrixBuffer?.Dispose();
                _maxConnBuffer?.Dispose();
                _slotBaseBuffer?.Dispose();
                _selIndexBuffer?.Dispose();
                _selDistanceBuffer?.Dispose();
                _selMetaBuffer?.Dispose();
                _selLockedBuffer?.Dispose();
                _bondBuffer?.Dispose();
                _proposalBuffer?.Dispose();
                _proposalCountBuffer?.Dispose();
                _proposalCountPrevBuffer?.Dispose();
                _cellCountBuffer?.Dispose();
                _cellStartBuffer?.Dispose();
                _nextPosBuffer?.Dispose();
                _sortedBuffer?.Dispose();
            }

            _accelerator?.Dispose();
            _context?.Dispose();
        }
    }
}
