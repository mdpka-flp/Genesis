using System;
using System.Numerics;
using System.IO;
using System.Text.Json;
using System.Linq;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Raylib_cs;
using rlImGui_cs;
using ImGuiNET;
using TinyDialogsNet;
using Genesis.Core;

namespace Genesis.UI
{
    public class GenesisUI
    {
        private Func<ISimulation> getCurrentSim;
        private bool showSettings = false;
        private bool centerSettingsWindow;
        private int targetTypeCount;
        private int targetParticleCount;
        private string lastPresetDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        // Диалог пресета работает в фоновой задаче: TinyDialogs на Linux запускает
        // zenity/kdialog отдельным процессом, и держать его в кадре рендера нельзя —
        // окно перестаёт отвечать и приложение падает. Пока задача жива, симуляция
        // продолжает считаться, а результат забирается в Update().
        private Task<(bool Canceled, string Path)>? presetDialogTask;
        private bool presetDialogIsSave;

        private const string UnlimitedToken = "inf";
        private const string NoneToken = "none";

        private readonly Type[] _simulationTypes = new Type[]
        {
            typeof(Simulation),
            typeof(Simulation_ComplexNumbers),
            typeof(SimulationGpu)
        };
        private string[] _simulationNames;
        private int _currentSimIndex;

        private int _randomParamIndex;
        private int _colorTypeIndex;
        
        private Action<Type> _onSimulationTypeChanged;

        /// <summary>
        /// Пункты меню «что рандомизировать». Порядок и подписи — только для UI,
        /// сама маска живёт в <see cref="RuleRandomizer.Parts"/>.
        /// </summary>
        private static readonly (RuleRandomParts Part, string Label)[] RandomPartItems =
        {
            (RuleRandomParts.Radius, "Radius"),
            (RuleRandomParts.ForceMultiplier, "Force multiplier"),
            (RuleRandomParts.Friction, "Friction"),
            (RuleRandomParts.MinDistance, "Min distance"),
            (RuleRandomParts.RepulsionStrength, "Repulsion strength"),
            (RuleRandomParts.Matrix, "Interaction matrix"),
            (RuleRandomParts.Connections, "Max connections"),
        };

        /// <summary>
        /// Подписи параметров в том же порядке, что и <see cref="RandomParameter"/>:
        /// индекс массива приводится к enum напрямую.
        /// </summary>
        private static readonly string[] RandomParameterNames =
        {
            "Radius",
            "Force multiplier",
            "Friction",
            "Min distance",
            "Repulsion strength",
            "Interaction rule",
            "Max connections",
        };
        
        /// <summary>
        /// Пункты меню «что показывать в оверлее». Порядок и подписи — только для UI,
        /// сама маска живёт в <see cref="OverlaySettings.Parts"/>.
        /// </summary>
        private static readonly (OverlayParts Part, string Label)[] OverlayItems =
        {
            (OverlayParts.ParticleCount, "Particle count"),
            (OverlayParts.TypeCount, "Type count"),
            (OverlayParts.Fps, "FPS"),
            (OverlayParts.Mode, "Render mode"),
        };

        private class ComplexData
        {
            public double Real { get; set; }
            public double Imaginary { get; set; }
        }

        private class PresetData
        {
            /// <summary>
            /// Версия Genesis, которой сохранён файл (<see cref="AppInfo.Version"/>).
            /// В старых пресетах поля нет — это и есть признак «файл до версионирования».
            /// </summary>
            public string? AppVersion { get; set; }

            public string SimulationTypeName { get; set; }
            public int ParticleCount { get; set; }
            public int TypeCount { get; set; }
            public float InteractionRadius { get; set; }
            public float ForceMultiplier { get; set; }
            public float Friction { get; set; }
            public float MinDistance { get; set; }
            public float RepulsionStrength { get; set; }
            public float[][] FloatMatrix { get; set; }
            public ComplexData[][] ComplexMatrix { get; set; }
            public int[][] ConnectionMatrix { get; set; }

            /// <summary>
            /// Цвета типов в виде "RRGGBB". В старых пресетах поля нет — тогда берутся
            /// цвета по умолчанию из <see cref="TypePalette"/>.
            /// </summary>
            public string[]? TypeColors { get; set; }
        }
        
        public GenesisUI(Func<ISimulation> getCurrentSim, Action<Type> onSimulationTypeChanged)
        {
            this.getCurrentSim = getCurrentSim;
            _onSimulationTypeChanged = onSimulationTypeChanged;
            
            var sim = getCurrentSim();
            targetTypeCount = sim.TypeCount;
            targetParticleCount = sim.ParticleCount;
            
            _simulationNames = new string[_simulationTypes.Length];
            for (int i = 0; i < _simulationTypes.Length; i++)
                _simulationNames[i] = _simulationTypes[i].Name;
                
            _currentSimIndex = Array.FindIndex(_simulationTypes, t => t == sim.GetType());
        }

        /// <summary>
        /// Синхронизирует выбранный в UI режим с фактическим бэкендом. Нужна, если
        /// Program не смог создать запрошенный режим и откатился на CPU.
        /// </summary>
        public void SyncSimulationType(ISimulation sim)
        {
            int index = Array.FindIndex(_simulationTypes, t => t == sim.GetType());
            if (index >= 0)
                _currentSimIndex = index;

            targetTypeCount = sim.TypeCount;
            targetParticleCount = sim.ParticleCount;
        }
        
        public void Setup()
        {
            rlImGui.Setup();

            var io = ImGui.GetIO();
            io.Fonts.Build();
            rlImGui.ReloadFonts();
            io.FontGlobalScale = 1.4f;
        }

        public void Update()
        {
            if (Raylib.IsKeyPressed(KeyboardKey.Tab))
            {
                showSettings = !showSettings;
                if (showSettings)
                    centerSettingsWindow = true;
            }

            // Результат диалога забираем здесь, а не в Draw: окно настроек можно
            // закрыть (Tab), пока диалог ещё открыт, и Draw перестанет вызываться.
            PollPresetDialog();
        }

        public void Draw()
        {
            var sim = getCurrentSim();
            if (sim == null) return;
            
            rlImGui.Begin();

            if (showSettings)
            {
                if (centerSettingsWindow)
                {
                    var size = new Vector2(650, 500);
                    ImGui.SetNextWindowSize(size, ImGuiCond.Always);
                    ImGui.SetNextWindowPos((ImGui.GetIO().DisplaySize - size) * 0.5f, ImGuiCond.Always);
                    centerSettingsWindow = false;
                }

                ImGui.Begin("Simulation Settings", ref showSettings);
                
                ImGui.Text("Presets");
                ImGui.SameLine();

                bool dialogOpen = presetDialogTask != null;
                if (dialogOpen) ImGui.BeginDisabled();
                if (ImGui.Button("Save Preset"))
                    StartSaveDialog();
                ImGui.SameLine();
                if (ImGui.Button("Load Preset"))
                    StartLoadDialog();
                if (dialogOpen) ImGui.EndDisabled();

                if (dialogOpen)
                    ImGui.TextDisabled("Waiting for the file dialog...");
                
                ImGui.Separator();
                ImGui.Separator();
                ImGui.Separator();
                
                ImGui.Text("Simulation type:");
                ImGui.SameLine();
                if (ImGui.ArrowButton("##prevSim", ImGuiDir.Left))
                {
                    _currentSimIndex = (_currentSimIndex - 1 + _simulationTypes.Length) % _simulationTypes.Length;
                    _onSimulationTypeChanged?.Invoke(_simulationTypes[_currentSimIndex]);
                    var newSim = getCurrentSim();
                    targetTypeCount = newSim.TypeCount;
                    targetParticleCount = newSim.ParticleCount;
                }
                ImGui.SameLine();
                ImGui.Text(_simulationNames[_currentSimIndex]);
                ImGui.SameLine();
                if (ImGui.ArrowButton("##nextSim", ImGuiDir.Right))
                {
                    _currentSimIndex = (_currentSimIndex + 1) % _simulationTypes.Length;
                    _onSimulationTypeChanged?.Invoke(_simulationTypes[_currentSimIndex]);
                    var newSim = getCurrentSim();
                    targetTypeCount = newSim.TypeCount;
                    targetParticleCount = newSim.ParticleCount;
                }
                
                ImGui.Spacing();

                ImGui.Text("Overlay:");
                ImGui.SameLine();
                DrawOverlayPartsCombo();

                ImGui.Separator();

                ImGui.Text("Interaction Radius");
                float radius = sim.InteractionRadius;
                if (ImGui.InputFloat("##radius", ref radius, 1f, 10f, "%.1f"))
                    sim.InteractionRadius = radius;

                ImGui.Text("Force Multiplier");
                float force = sim.ForceMultiplier;
                if (ImGui.InputFloat("##force", ref force, 1f, 10f, "%.1f"))
                    sim.ForceMultiplier = force;

                ImGui.Text("Friction");
                float friction = sim.Friction;
                if (ImGui.InputFloat("##friction", ref friction, 0.01f, 0.1f, "%.3f"))
                    sim.Friction = friction;

                ImGui.Text("Min Distance");
                float minDist = sim.MinDistance;
                if (ImGui.InputFloat("##minDist", ref minDist, 1f, 10f, "%.1f"))
                    sim.MinDistance = minDist;

                ImGui.Text("Repulsion Strength");
                float repulsion = sim.RepulsionStrength;
                if (ImGui.InputFloat("##repulsion", ref repulsion, 1f, 10f, "%.1f"))
                    sim.RepulsionStrength = repulsion;

                ImGui.Text("Number of particles");
                int newParticleCount = targetParticleCount;
                ImGui.InputInt("##particles_input", ref newParticleCount);
                if (newParticleCount < 1) newParticleCount = 1;

                if (newParticleCount != targetParticleCount)
                {
                    targetParticleCount = newParticleCount;
                    sim.SetParticleCount(targetParticleCount);
                }

                ImGui.Spacing();

                ImGui.Text("Number of particle types");
                int newTypeCount = targetTypeCount;
                ImGui.InputInt("##types_input", ref newTypeCount);
                if (newTypeCount < 1) newTypeCount = 1;

                if (newTypeCount != targetTypeCount)
                {
                    targetTypeCount = newTypeCount;
                    while (sim.TypeCount < targetTypeCount)
                        sim.AddType();
                    while (sim.TypeCount > targetTypeCount)
                        sim.RemoveType();
                }
                
                ImGui.Separator();

                if (ImGui.Button("Generate random rules"))
                {
                    sim.GenerateRules();
                }

                // Справа от кнопки — какие параметры вообще трогать. Маска общая
                // для всех бэкендов, поэтому нажимать её можно один раз.
                ImGui.SameLine();
                DrawRandomPartsCombo();

                ImGui.Spacing();

                if (ImGui.Button("Generate random colors"))
                {
                    sim.GenerateColors();
                }

                ImGui.SameLine();
                DrawTypeColorEditor(sim);
                
                if (ImGui.Button("Restart"))
                {
                    sim.Restart();
                }
                
                ImGui.Separator();
                
                ImGui.Text("Interaction Matrix:");

                if (sim is Simulation simFloat)
                    DrawFloatMatrix(simFloat.InteractionMatrix, simFloat.TypeCount);
                else if (sim is SimulationGpu simGpu)
                    DrawFloatMatrix(simGpu.InteractionMatrix, simGpu.TypeCount);
                else if (sim is Simulation_ComplexNumbers simComplex)
                    DrawComplexMatrix(simComplex.InteractionMatrix, simComplex.TypeCount);

                ImGui.Separator();

                ImGui.Text("Max connections (inf = unlimited):");
                ImGui.TextDisabled("Nearest K neighbours per type.");
                ImGui.TextDisabled("Number 1-" + ConnectionLimits.MaxPerType + ", \"inf\" (any), or \"none\".");
                DrawIntMatrix(sim.MaxConnections, sim.TypeCount);

                ImGui.Separator();
                DrawRandomRangeEditor();

                ImGui.End();
            }

            rlImGui.End();
        }

        public void Shutdown()
        {
            rlImGui.Shutdown();
        }
        
        private void DrawFloatMatrix(float[,] matrix, int typeCount) 
        {
            if (matrix == null) return;
        
            if (ImGui.BeginTable("FloatMatrix", typeCount + 1, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingFixedFit))
            {
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 80);
                for (int j = 0; j < typeCount; j++)
                    ImGui.TableSetupColumn($"Type {j}", ImGuiTableColumnFlags.WidthFixed, 80);
                ImGui.TableHeadersRow();

                for (int i = 0; i < typeCount; i++)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text($"Type {i}");

                    for (int j = 0; j < typeCount; j++)
                    {
                        ImGui.TableSetColumnIndex(j + 1);
                        float val = matrix[i, j];
                        ImGui.SetNextItemWidth(60f);
                        if (ImGui.InputFloat($"##{i}_{j}", ref val, 0f, 0f, "%.2f"))
                        {
                            matrix[i, j] = val;
                        }
                    }
                }
                ImGui.EndTable();
            }
        } 
        
        private void DrawComplexMatrix(Complex[,] matrix, int typeCount)
        {            if (matrix == null) return;

            if (ImGui.BeginTable("ComplexMatrix", typeCount + 1, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingFixedFit))
            {
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 130);
                for (int j = 0; j < typeCount; j++)
                    ImGui.TableSetupColumn($"Type {j}", ImGuiTableColumnFlags.WidthFixed, 130);
                ImGui.TableHeadersRow();

                for (int i = 0; i < typeCount; i++)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text($"Type {i}");

                    for (int j = 0; j < typeCount; j++)
                    {
                        ImGui.TableSetColumnIndex(j + 1);
                        Complex c = matrix[i, j];
                        double real = c.Real;
                        double imag = c.Imaginary;

                        ImGui.PushID($"{i}_{j}");

                        ImGui.SetNextItemWidth(60f);
                        bool changed = ImGui.InputDouble("##real", ref real, 0, 0, "%.2f");
                        ImGui.SameLine();
                    
                        ImGui.SetNextItemWidth(60f);
                        changed |= ImGui.InputDouble("##imag", ref imag, 0, 0, "%.2f");

                        if (changed)
                        {
                            matrix[i, j] = new Complex(real, imag);
                        }
                        ImGui.PopID();
                    }
                }
                ImGui.EndTable();
            }
        }

        /// <summary>
        /// Редактор целочисленной матрицы пределов связей: [свой тип, тип соседа].
        ///
        /// Внутренний формат хранит 0 как «без ограничения» — так читаются старые
        /// пресеты, где матрицы не было. Но показывать 0 нельзя: «0 связей» и
        /// «связей сколько угодно» — разные вещи, и ноль в матрице путал.
        /// Поэтому неограниченность вводится словом <c>inf</c>, а числа — это
        /// реальные пределы.
        /// </summary>
        private void DrawIntMatrix(int[,] matrix, int typeCount)
        {
            if (matrix == null ||
                matrix.GetLength(0) < typeCount ||
                matrix.GetLength(1) < typeCount)
                return;

            if (ImGui.BeginTable("IntMatrix", typeCount + 1, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingFixedFit))
            {
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 80);
                for (int j = 0; j < typeCount; j++)
                    ImGui.TableSetupColumn($"Type {j}", ImGuiTableColumnFlags.WidthFixed, 80);
                ImGui.TableHeadersRow();

                for (int i = 0; i < typeCount; i++)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text($"Type {i}");

                    for (int j = 0; j < typeCount; j++)
                    {
                        ImGui.TableSetColumnIndex(j + 1);
                        ImGui.PushID(i * 128 + j);

                        int value = matrix[i, j];
                        string text = value > 0
                            ? value.ToString(CultureInfo.InvariantCulture)
                            : value < 0
                                ? NoneToken
                                : UnlimitedToken;

                        ImGui.SetNextItemWidth(60f);
                        ImGui.InputText("##conn", ref text, 8, ImGuiInputTextFlags.AutoSelectAll);

                        // Коммитим не на каждый символ, а когда поле покинуто или нажат Enter,
                        // иначе промежуточные «1» из «12» применялись бы к симуляции.
                        if (ImGui.IsItemDeactivatedAfterEdit())
                            matrix[i, j] = ParseConnectionLimit(text);

                        ImGui.PopID();
                    }
                }
                ImGui.EndTable();
            }
        }

        /// <summary>
        /// Разбор ячейки матрицы связей. <c>inf</c>/<c>unlimited</c>/<c>0</c> —
        /// «без ограничения» (внутреннее значение 0), <c>none</c>/<c>-1</c> — «связей
        /// нет» (внутреннее <see cref="ConnectionLimits.None"/>), числа ограничиваются
        /// диапазоном 1..<see cref="ConnectionLimits.MaxPerType"/>.
        /// </summary>
        private static int ParseConnectionLimit(string? text)
        {
            string value = text?.Trim() ?? string.Empty;
            if (value.Length == 0)
                return 0;

            if (value.Equals(NoneToken, StringComparison.OrdinalIgnoreCase) ||
                value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                value == "-1")
                return ConnectionLimits.None;

            if (value.Equals(UnlimitedToken, StringComparison.OrdinalIgnoreCase) ||
                value.Equals("infinite", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("unlimited", StringComparison.OrdinalIgnoreCase) ||
                value == "0")
                return 0;

            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                return Math.Clamp(parsed, 1, ConnectionLimits.MaxPerType);

            return 0;
        }

        /// <summary>
        /// Меню «что рандомизировать» рядом с кнопкой генерации правил. Это именно
        /// чекбоксы в выпадающем меню: отмечать можно несколько пунктов, а не
        /// выбирать один режим. По умолчанию включено всё.
        /// </summary>
        private static void DrawRandomPartsCombo()
        {
            // Превью показывает состояние: по умолчанию «All», после снятия галочек —
            // «Custom», если сняты все — «None».
            string preview = RuleRandomizer.Parts == RuleRandomParts.All
                ? "All"
                : RuleRandomizer.Parts == RuleRandomParts.None
                    ? "None"
                    : "Custom";

            ImGui.SetNextItemWidth(90f);
            if (!ImGui.BeginCombo("##ruleParts", preview))
                return;

            foreach (var (part, label) in RandomPartItems)
            {
                bool enabled = (RuleRandomizer.Parts & part) != 0;
                if (ImGui.Checkbox(label, ref enabled))
                    RuleRandomizer.Parts = enabled
                        ? RuleRandomizer.Parts | part
                        : RuleRandomizer.Parts & ~part;
            }

            ImGui.EndCombo();
        }

        /// <summary>
        /// Меню «что показывать в оверлее»: галочки, как у маски рандома.
        /// Настройка общая для всех бэкендов.
        /// </summary>
        private static void DrawOverlayPartsCombo()
        {
            string preview = OverlaySettings.Parts == OverlayParts.All
                ? "All"
                : OverlaySettings.Parts == OverlayParts.None
                    ? "None"
                    : "Custom";

            ImGui.SetNextItemWidth(120f);
            if (!ImGui.BeginCombo("##overlayParts", preview))
                return;

            foreach (var (part, label) in OverlayItems)
            {
                bool enabled = OverlaySettings.Has(part);
                if (ImGui.Checkbox(label, ref enabled))
                    OverlaySettings.Parts = enabled
                        ? OverlaySettings.Parts | part
                        : OverlaySettings.Parts & ~part;
            }

            ImGui.EndCombo();
        }

        /// <summary>
        /// Выбор типа частицы и цвета для него. Стандартные цвета заданы в
        /// <see cref="TypePalette"/> и совпадают между запусками.
        /// </summary>
        private void DrawTypeColorEditor(ISimulation sim)
        {
            if (_colorTypeIndex >= sim.TypeCount)
                _colorTypeIndex = 0;

            ImGui.SetNextItemWidth(95f);
            if (ImGui.BeginCombo("##colorType", $"Type {_colorTypeIndex}"))
            {
                for (int i = 0; i < sim.TypeCount; i++)
                {
                    if (ImGui.Selectable($"Type {i}", i == _colorTypeIndex))
                        _colorTypeIndex = i;
                }
                ImGui.EndCombo();
            }

            Color[] colors = sim.TypeColors;
            if (colors.Length == 0)
                return;

            Color color = colors[_colorTypeIndex];
            var rgb = new Vector3(color.R / 255f, color.G / 255f, color.B / 255f);

            ImGui.SameLine();
            if (ImGui.ColorEdit3("##typeColor", ref rgb, ImGuiColorEditFlags.NoInputs))
            {
                colors[_colorTypeIndex] = new Color(
                    (byte)Math.Clamp(rgb.X * 255f + 0.5f, 0f, 255f),
                    (byte)Math.Clamp(rgb.Y * 255f + 0.5f, 0f, 255f),
                    (byte)Math.Clamp(rgb.Z * 255f + 0.5f, 0f, 255f),
                    (byte)255);
            }
        }

        /// <summary>
        /// Диапазоны случайных значений. Полное дублирование всех параметров раздуло
        /// бы окно вдвое, поэтому: выпадающий список параметров + два поля под ним.
        /// Значения применяются сразу, а нормализацию (min ≤ max, безопасные пределы)
        /// делает <see cref="RuleRandomizer.SetRange"/>.
        /// </summary>
        private void DrawRandomRangeEditor()
        {
            ImGui.Text("Random ranges");
            ImGui.TextDisabled("Used by \"Generate random rules\".");

            ImGui.SetNextItemWidth(200f);
            if (ImGui.BeginCombo("##randomParameter", RandomParameterNames[_randomParamIndex]))
            {
                for (int i = 0; i < RandomParameterNames.Length; i++)
                {
                    bool selected = i == _randomParamIndex;
                    if (ImGui.Selectable(RandomParameterNames[i], selected))
                        _randomParamIndex = i;
                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            var parameter = (RandomParameter)_randomParamIndex;
            RandomRange range = RuleRandomizer.GetRange(parameter);
            float min = range.Min;
            float max = range.Max;
            bool minChanged;
            bool maxChanged;

            if (parameter == RandomParameter.Connections)
            {
                // Связи — целые: дробное «3.7 связи» ничего не значит.
                int intMin = (int)MathF.Round(min);
                int intMax = (int)MathF.Round(max);

                ImGui.Text("min");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(90f);
                minChanged = ImGui.InputInt("##randomMin", ref intMin);

                ImGui.SameLine();
                ImGui.Text("max");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(90f);
                maxChanged = ImGui.InputInt("##randomMax", ref intMax);

                min = intMin;
                max = intMax;
            }
            else
            {
                float step = parameter == RandomParameter.Friction ? 0.001f : 0.5f;
                float fastStep = parameter == RandomParameter.Friction ? 0.01f : 5f;
                string format = parameter switch
                {
                    RandomParameter.Friction => "%.3f",
                    RandomParameter.Rule => "%.2f",
                    _ => "%.1f",
                };

                ImGui.Text("min");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(115f);
                minChanged = ImGui.InputFloat("##randomMin", ref min, step, fastStep, format);

                ImGui.SameLine();
                ImGui.Text("max");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(115f);
                maxChanged = ImGui.InputFloat("##randomMax", ref max, step, fastStep, format);
            }

            // Правка одного поля не должна перескакивать в другое: если подняли min
            // выше max, подтягиваем max (и наоборот), а не меняем местами значения.
            if (minChanged)
                max = MathF.Max(min, max);
            if (maxChanged)
                min = MathF.Min(min, max);

            if (minChanged || maxChanged)
                RuleRandomizer.SetRange(parameter, min, max);

            if (parameter == RandomParameter.Connections)
                ImGui.TextDisabled("0 = unlimited");
        }

        private void StartSaveDialog()
        {
            if (presetDialogTask != null) return;

            string directory = lastPresetDirectory;
            presetDialogIsSave = true;
            presetDialogTask = RunPresetDialog(() =>
            {
                var filter = new FileFilter("Genesis Preset", new[] { "*.gsp" });
                return TinyDialogs.SaveFileDialog("Save Preset", directory, filter);
            });
        }

        private void StartLoadDialog()
        {
            if (presetDialogTask != null) return;

            string directory = lastPresetDirectory;
            presetDialogIsSave = false;
            presetDialogTask = RunPresetDialog(() =>
            {
                var filter = new FileFilter("Genesis Preset", new[] { "*.gsp" });
                var (canceled, paths) =
                    TinyDialogs.OpenFileDialog("Load Preset", directory, false, filter);
                string first = paths?.FirstOrDefault() ?? string.Empty;
                return (canceled, first);
            });
        }

        /// <summary>
        /// Выполняет диалог на отдельном потоке и возвращает задачу с результатом.
        ///
        /// Поток именно свой, а не из пула: на Windows диалог — нативное окно, и ему
        /// нужен STA (на MTA-потоке из пула <c>CoInitializeEx(STA)</c> вернёт
        /// <c>RPC_E_CHANGED_MODE</c>). На Linux апартаменты не поддерживаются, там
        /// диалог — отдельный процесс zenity/kdialog, и поток любой.
        /// </summary>
        private static Task<(bool Canceled, string Path)> RunPresetDialog(
            Func<(bool Canceled, string Path)> dialog)
        {
            var completion = new TaskCompletionSource<(bool, string)>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var thread = new Thread(() =>
            {
                try { completion.SetResult(dialog()); }
                catch (Exception ex) { completion.SetException(ex); }
            })
            {
                IsBackground = true,
                Name = "preset-dialog"
            };

            if (OperatingSystem.IsWindows())
                thread.SetApartmentState(ApartmentState.STA);

            thread.Start();
            return completion.Task;
        }

        /// <summary>
        /// Забирает результат фонового диалога. Вызывается каждый кадр; пока диалог
        /// открыт, задача не завершена и симуляция продолжает работать.
        /// </summary>
        private void PollPresetDialog()
        {
            var task = presetDialogTask;
            if (task == null || !task.IsCompleted)
                return;

            presetDialogTask = null;
            bool isSave = presetDialogIsSave;

            try
            {
                var (canceled, path) = task.GetAwaiter().GetResult();
                if (canceled || string.IsNullOrEmpty(path))
                    return;

                string fullPath = isSave ? EnsurePresetExtension(path) : path;
                string? directory = System.IO.Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory))
                    lastPresetDirectory = directory!;

                if (isSave)
                    SavePresetToFile(fullPath);
                else
                    LoadPresetFromFile(fullPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Genesis] Ошибка в диалоге пресета: {ex.Message}");
            }
        }

        /// <summary>
        /// Linux-реализация TinyDialogs (zenity/kdialog) не дописывает расширение из
        /// фильтра: без этого «my rules» сохранялся в файл без <c>.gsp</c> и пропадал
        /// из проводника. Windows делает это сам, но проверка безвредна и там.
        /// </summary>
        private static string EnsurePresetExtension(string path)
        {
            return path.EndsWith(".gsp", StringComparison.OrdinalIgnoreCase)
                ? path
                : path + ".gsp";
        }

        private void SavePresetToFile(string path)
        {
            var sim = getCurrentSim();
            if (sim == null) return;

            var data = new PresetData
            {
                AppVersion = AppInfo.Version,
                SimulationTypeName = sim.GetType().Name,
                ParticleCount = sim.ParticleCount,
                TypeCount = sim.TypeCount,
                InteractionRadius = sim.InteractionRadius,
                ForceMultiplier = sim.ForceMultiplier,
                Friction = sim.Friction,
                MinDistance = sim.MinDistance,
                RepulsionStrength = sim.RepulsionStrength
            };

            if (sim is Simulation simFloat)
            {
                int n = simFloat.TypeCount;
                data.FloatMatrix = new float[n][];
                for (int i = 0; i < n; i++)
                {
                    data.FloatMatrix[i] = new float[n];
                    for (int j = 0; j < n; j++)
                        data.FloatMatrix[i][j] = simFloat.InteractionMatrix[i, j];
                }
            }
            else if (sim is SimulationGpu simGpu)
            {
                int n = simGpu.TypeCount;
                data.FloatMatrix = new float[n][];
                for (int i = 0; i < n; i++)
                {
                    data.FloatMatrix[i] = new float[n];
                    for (int j = 0; j < n; j++)
                        data.FloatMatrix[i][j] = simGpu.InteractionMatrix[i, j];
                }
            }
            else if (sim is Simulation_ComplexNumbers simComplex)
            {
                int n = simComplex.TypeCount;
                data.ComplexMatrix = new ComplexData[n][];
                for (int i = 0; i < n; i++)
                {
                    data.ComplexMatrix[i] = new ComplexData[n];
                    for (int j = 0; j < n; j++)
                    {
                        var c = simComplex.InteractionMatrix[i, j];
                        data.ComplexMatrix[i][j] = new ComplexData { Real = c.Real, Imaginary = c.Imaginary };
                    }
                }
            }

            if (sim.MaxConnections != null)
            {
                int n = sim.TypeCount;
                data.ConnectionMatrix = new int[n][];
                for (int i = 0; i < n; i++)
                {
                    data.ConnectionMatrix[i] = new int[n];
                    for (int j = 0; j < n; j++)
                        data.ConnectionMatrix[i][j] = sim.MaxConnections[i, j];
                }
            }

            // Цвета тоже сохраняем: иначе подобранная вручную палитра терялась бы
            // при загрузке пресета и заменялась стандартной.
            data.TypeColors = new string[sim.TypeColors.Length];
            for (int i = 0; i < sim.TypeColors.Length; i++)
                data.TypeColors[i] = ToHex(sim.TypeColors[i]);

            string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        private void LoadPresetFromFile(string path)
        {
            if (!File.Exists(path))
                return;

            string json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<PresetData>(json);
            if (data == null) return;

            // Версия нужна только для разбора проблем: формат читается терпимо,
            // отсутствие поля — это просто пресет, сохранённый до версионирования.
            Console.WriteLine(
                $"[Genesis] Пресет {System.IO.Path.GetFileName(path)}: " +
                $"версия {(string.IsNullOrEmpty(data.AppVersion) ? "не указана (старый)" : data.AppVersion)}");
            
            Type targetType = _simulationTypes.FirstOrDefault(t => t.Name == data.SimulationTypeName);
            if (targetType != null && getCurrentSim().GetType() != targetType)
            {
                _onSimulationTypeChanged?.Invoke(targetType);
            }

            var sim = getCurrentSim();
            
            sim.SetParticleCount(data.ParticleCount);
            
            while (sim.TypeCount < data.TypeCount)
                sim.AddType();
            while (sim.TypeCount > data.TypeCount)
                sim.RemoveType();
            
            sim.InteractionRadius = data.InteractionRadius;
            sim.ForceMultiplier = data.ForceMultiplier;
            sim.Friction = data.Friction;
            sim.MinDistance = data.MinDistance;
            sim.RepulsionStrength = data.RepulsionStrength;
            
            if (sim is Simulation simFloat && data.FloatMatrix != null)
            {
                int n = simFloat.TypeCount;
                for (int i = 0; i < n && i < data.FloatMatrix.Length; i++)
                {
                    var row = data.FloatMatrix[i];
                    for (int j = 0; j < n && j < row.Length; j++)
                    {
                        simFloat.InteractionMatrix[i, j] = row[j];
                    }
                }
            }
            else if (sim is SimulationGpu simGpu && data.FloatMatrix != null)
            {
                int n = simGpu.TypeCount;
                for (int i = 0; i < n && i < data.FloatMatrix.Length; i++)
                {
                    var row = data.FloatMatrix[i];
                    for (int j = 0; j < n && j < row.Length; j++)
                    {
                        simGpu.InteractionMatrix[i, j] = row[j];
                    }
                }
            }
            else if (sim is Simulation_ComplexNumbers simComplex && data.ComplexMatrix != null)
            {
                int n = simComplex.TypeCount;
                for (int i = 0; i < n && i < data.ComplexMatrix.Length; i++)
                {
                    var row = data.ComplexMatrix[i];
                    for (int j = 0; j < n && j < row.Length; j++)
                    {
                        var cd = row[j];
                        simComplex.InteractionMatrix[i, j] = new Complex(cd.Real, cd.Imaginary);
                    }
                }
            }
            
            if (sim.MaxConnections != null)
            {
                if (data.ConnectionMatrix != null)
                {
                    int n = sim.TypeCount;
                    for (int i = 0; i < n; i++)
                        for (int j = 0; j < n; j++)
                            sim.MaxConnections[i, j] = ConnectionLimits.None;

                    for (int i = 0; i < n && i < data.ConnectionMatrix.Length; i++)
                    {
                        var row = data.ConnectionMatrix[i];
                        for (int j = 0; j < n && j < row.Length; j++)
                        {
                            // 0 — «без ограничения», -1 — «связей нет»; остальное
                            // держим в тех же пределах, что и редактор матрицы.
                            sim.MaxConnections[i, j] = row[j] < 0
                                ? ConnectionLimits.None
                                : Math.Clamp(row[j], 0, ConnectionLimits.MaxPerType);
                        }
                    }
                }
                else
                {
                    // В старых пресетах связей нет — значит, ограничений не было.
                    Array.Clear(sim.MaxConnections, 0, sim.MaxConnections.Length);
                }
            }

            // В старых пресетах цветов нет, да и список может быть короче: всё, чего
            // нет в файле, заменяем палитрой, иначе останутся цвета от предыдущей
            // симуляции.
            for (int i = 0; i < sim.TypeColors.Length; i++)
            {
                if (data.TypeColors != null && i < data.TypeColors.Length &&
                    TryParseHex(data.TypeColors[i], out Color color))
                    sim.TypeColors[i] = color;
                else
                    sim.TypeColors[i] = TypePalette.Get(i);
            }

            targetParticleCount = data.ParticleCount;
            targetTypeCount = data.TypeCount;
        }

        private static string ToHex(Color color)
            => $"{color.R:X2}{color.G:X2}{color.B:X2}";

        private static bool TryParseHex(string? hex, out Color color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(hex))
                return false;

            string value = hex.Trim().TrimStart('#');
            if (value.Length != 6 ||
                !byte.TryParse(value.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) ||
                !byte.TryParse(value.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) ||
                !byte.TryParse(value.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                return false;

            color = new Color((byte)r, (byte)g, (byte)b, (byte)255);
            return true;
        }
    }
}