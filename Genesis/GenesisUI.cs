using System.Numerics;
using Raylib_cs;
using rlImGui_cs;
using ImGuiNET;

public class GenesisUI
{
    private bool showSettings = false;
    private Simulation simulation;

    private int targetTypeCount;
    private int targetParticleCount;

    public GenesisUI(Simulation sim)
    {
        simulation = sim;
        targetTypeCount = sim.TypeCount;
        targetParticleCount = sim.Particles.Length;
    }

    public void Setup()
    {
        rlImGui.Setup();
        var io = ImGui.GetIO();
        io.Fonts.AddFontFromFileTTF("C:\\Windows\\Fonts\\arial.ttf", 18.0f, null, io.Fonts.GetGlyphRangesCyrillic());
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
        rlImGui.Begin();

        if (showSettings)
        {
            ImGui.SetNextWindowSize(new Vector2(400, 0), ImGuiCond.FirstUseEver);
            ImGui.Begin("Simulation Settings", ref showSettings);

            ImGui.Separator();
            ImGui.Text("Number of particles");

            int newParticleCount = targetParticleCount;
            //ImGui.DragInt("##particles", ref newParticleCount, 10f, 1, 20000, "%d");
            ImGui.InputInt("##particles_input", ref newParticleCount);
            if (newParticleCount < 1) newParticleCount = 1;

            if (newParticleCount != targetParticleCount)
            {
                targetParticleCount = newParticleCount;
                simulation.SetParticleCount(targetParticleCount);
            }

            ImGui.Spacing();

            ImGui.Text("Number of particle types");
            int newTypeCount = targetTypeCount;
            //ImGui.DragInt("##types", ref newTypeCount, 1f, 1, 15, "%d");
            ImGui.InputInt("##types_input", ref newTypeCount);
            if (newTypeCount < 1) newTypeCount = 1;

            if (newTypeCount != targetTypeCount)
            {
                targetTypeCount = newTypeCount;
                while (simulation.TypeCount < targetTypeCount)
                    simulation.AddType();
                while (simulation.TypeCount > targetTypeCount)
                    simulation.RemoveType();
            }

            ImGui.Spacing();

            if (ImGui.Button("Generate random rules"))
            {
                simulation.GenerateRules();
            }

            ImGui.Spacing();

            if (ImGui.Button("Generate random colors"))
            {
                simulation.GenerateColors();
            }

            ImGui.End();
        }

        rlImGui.End();
    }

    public void Shutdown()
    {
        rlImGui.Shutdown();
    }
}