using Raylib_cs;

static class Program
{
    static void Main()
    {
        Raylib.InitWindow(1600, 900, "Genesis");
        Raylib.SetTargetFPS(60);
        
        int ParticleCount = 5000;
        int TypeCount = 3;
        
        Simulation sim = new Simulation();
        sim.Initialize(ParticleCount, TypeCount);
        
        GenesisUI ui = new GenesisUI(sim);
        ui.Setup();
        
        InputManager input = new InputManager();
        input.RegAction(KeyboardKey.G, () => sim.GenerateRules());
        input.RegAction(KeyboardKey.Q, () => sim.RemoveType());
        input.RegAction(KeyboardKey.E, () => sim.AddType());
        input.RegAction(KeyboardKey.F, () => sim.GenerateColors());
        
        sim.GenerateRules();
        
        while (!Raylib.WindowShouldClose())
        {
            float dt = Raylib.GetFrameTime();
            
            sim.Update(dt); 
            input.Update();
            ui.Update();
            
            Raylib.BeginDrawing();
            Raylib.ClearBackground(Color.Black);
            sim.Draw();
            ui.Draw();
            Raylib.EndDrawing();
        }
        
        Raylib.CloseWindow();
    }
}