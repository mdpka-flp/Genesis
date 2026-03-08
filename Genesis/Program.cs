using Raylib_cs;

static class Program
{
    static void Main()
    {
        Raylib.InitWindow(1600, 900, "Genesis");
        Raylib.SetTargetFPS(60);
        
        int ParticleCount = 10000;
        int TypeCount = 3;
        
        Simulation sim = new Simulation();
        sim.Initialize(ParticleCount, TypeCount);
        
        Simulation_ComplexNumbers sim_CN = new Simulation_ComplexNumbers();
        sim_CN.Initialize(ParticleCount, TypeCount);
        
        GenesisUI ui = new GenesisUI(sim);
        ui.Setup();
        
        InputManager input = new InputManager();
        input.RegAction(KeyboardKey.G, () => sim.GenerateRules());
        input.RegAction(KeyboardKey.G, () => sim_CN.GenerateRules());
        input.RegAction(KeyboardKey.Q, () => sim.RemoveType());
        input.RegAction(KeyboardKey.Q, () => sim_CN.RemoveType());
        input.RegAction(KeyboardKey.E, () => sim.AddType());
        input.RegAction(KeyboardKey.E, () => sim_CN.AddType());
        input.RegAction(KeyboardKey.F, () => sim.GenerateColors());
        input.RegAction(KeyboardKey.F, () => sim_CN.GenerateColors());
        
        //sim.GenerateRules();
        sim_CN.GenerateRules();
        
        while (!Raylib.WindowShouldClose())
        {
            float dt = Raylib.GetFrameTime();
            
            //sim.Update(dt); 
            sim_CN.Update(dt); 
            input.Update();
            ui.Update();
            
            Raylib.BeginDrawing();
            Raylib.ClearBackground(Color.Black);
            //sim.Draw();
            sim_CN.Draw();
            ui.Draw();
            Raylib.EndDrawing();
        }
        
        Raylib.CloseWindow();
    }
}