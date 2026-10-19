using System;
using System.Numerics;
using System.IO;
using System.Text.Json;
using System.Linq;
using Raylib_cs;
using rlImGui_cs;
using ImGuiNET;
using TinyDialogsNet;

public class GenesisUI
{
    private Func<ISimulation> getCurrentSim;
    private bool showSettings = false;
    private int targetTypeCount;
    private int targetParticleCount;
    private string lastPresetDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    private readonly Type[] _simulationTypes = new Type[]
    {
        typeof(Simulation),
        typeof(Simulation_ComplexNumbers)
    };
    private string[] _simulationNames;
    private int _currentSimIndex;
    
    private Action<Type> _onSimulationTypeChanged;
    
    private class ComplexData
    {
        public double Real { get; set; }
        public double Imaginary { get; set; }
    }

    private class PresetData
    {
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
    
    public void Setup()
    {
        rlImGui.Setup();
        
        var io = ImGui.GetIO();
        //io.Fonts.AddFontFromFileTTF("C:\\Windows\\Fonts\\arial.ttf", 18.0f, null, io.Fonts.GetGlyphRangesCyrillic());
        io.Fonts.Build();
        rlImGui.ReloadFonts();

        io.FontGlobalScale = 1.4f;
    }

    public void Update()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Tab))
        {
            showSettings = !showSettings;
        }
    }

    public void Draw()
    {
        var sim = getCurrentSim();
        if (sim == null) return;
        
        rlImGui.Begin();

        if (showSettings)
        {
            ImGui.SetNextWindowSize(new Vector2(400, 0), ImGuiCond.FirstUseEver);
            ImGui.Begin("Simulation Settings", ref showSettings);
            
            ImGui.Text("Presets");
            ImGui.SameLine();
            if (ImGui.Button("Save Preset"))
            {
                SavePreset();
            }
            ImGui.SameLine();
            if (ImGui.Button("Load Preset"))
            {
                LoadPreset();
            }
            
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

            ImGui.Spacing();

            if (ImGui.Button("Generate random colors"))
            {
                sim.GenerateColors();
            }
            
            if (ImGui.Button("Restart"))
            {
                sim.Restart();
            }
            
            ImGui.Separator();
            
            ImGui.Text("Interaction Matrix:");

            if (sim is Simulation simFloat)
                DrawFloatMatrix(simFloat.InteractionMatrix, simFloat.TypeCount);
            else if (sim is Simulation_ComplexNumbers simComplex)
                DrawComplexMatrix(simComplex.InteractionMatrix, simComplex.TypeCount);

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
    {
        if (matrix == null) return;

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

    private void SavePreset()
    {
        var filter = new FileFilter("Genesis Preset", new[] { "*.gsp" });
        var (canceled, path) = TinyDialogs.SaveFileDialog(
            "Save Preset",
            lastPresetDirectory,
            filter
        );

        if (!canceled && !string.IsNullOrEmpty(path))
        {
            lastPresetDirectory = System.IO.Path.GetDirectoryName(path);
            SavePresetToFile(path);
        }
    }

    private void LoadPreset()
    {
        var filter = new FileFilter("Genesis Preset", new[] { "*.gsp" });
        var allowMultipleSelections = false;
        var (canceled, paths) = TinyDialogs.OpenFileDialog(
            "Load Preset",
            lastPresetDirectory,
            allowMultipleSelections,
            filter
        );

        if (!canceled && paths != null)
        {
            string path = paths.FirstOrDefault();
            if (!string.IsNullOrEmpty(path))
            {
                lastPresetDirectory = System.IO.Path.GetDirectoryName(path);
                LoadPresetFromFile(path);
            }
        }
    }

    private void SavePresetToFile(string path)
    {
        var sim = getCurrentSim();
        if (sim == null) return;

        var data = new PresetData
        {
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
        
        Type targetType = data.SimulationTypeName == typeof(Simulation).Name ? typeof(Simulation) : typeof(Simulation_ComplexNumbers);
        if (getCurrentSim().GetType() != targetType)
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
        
        targetParticleCount = data.ParticleCount;
        targetTypeCount = data.TypeCount;
    }
}