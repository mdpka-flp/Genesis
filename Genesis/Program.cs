using System;
using Raylib_cs;

static class Program
{
    private static ISimulation? currentSim;
    
    static void Main()
    {
        Raylib.InitWindow(1600, 900, "Genesis");
        Raylib.SetTargetFPS(60);
        
        currentSim = new Simulation();
        currentSim.Initialize(5000,3);
        currentSim.GenerateRules();
        
        var ui = new GenesisUI(() => currentSim, OnSimulationTypeChanged);
        ui.Setup();
        
        var input = new InputManager();
        input.RegAction(KeyboardKey.G, () => currentSim?.GenerateRules());
        input.RegAction(KeyboardKey.Q, () => currentSim?.RemoveType());
        input.RegAction(KeyboardKey.E, () => currentSim?.AddType());
        input.RegAction(KeyboardKey.F, () => currentSim?.GenerateColors());
        input.RegAction(KeyboardKey.R, () => currentSim?.Restart());
        
        while (!Raylib.WindowShouldClose())
        {
            float dt = Raylib.GetFrameTime();
            
            currentSim?.Update(dt); 
            input.Update();
            ui.Update();
            
            Raylib.BeginDrawing();
            Raylib.ClearBackground(Color.Black);
            currentSim?.Draw();
            ui.Draw();
            Raylib.EndDrawing();
        }
        
        Raylib.CloseWindow();
    }
    
    private static void OnSimulationTypeChanged(Type newSimulationType)
    {
        int oldCount = currentSim?.ParticleCount ?? 5000;
        int oldTypes = currentSim?.TypeCount ?? 3;
        
        if (newSimulationType == typeof(Simulation))
            currentSim = new Simulation();
        else if (newSimulationType == typeof(Simulation_ComplexNumbers))
            currentSim = new Simulation_ComplexNumbers();
        else
            return;
        
        currentSim.Initialize(oldCount, oldTypes);
        currentSim.GenerateRules();
    }
}