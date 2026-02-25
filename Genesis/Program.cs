using Raylib_cs;

static class Program
{
    static void Main()
    {
        Raylib.InitWindow(1600, 900, "Genesis");
        Raylib.SetTargetFPS(60);
        
        int ParticleCount = 2000;
        int TypeCount = 3;
        
        Simulation sim = new Simulation();
        sim.Initialize(ParticleCount, TypeCount);
        
        InputManager input = new InputManager();
        input.RegAction(KeyboardKey.KEY_G, () => sim.GenerateRules());
        input.RegAction(KeyboardKey.KEY_Q, () => sim.RemoveType());
        input.RegAction(KeyboardKey.KEY_E, () => sim.AddType());
        input.RegAction(KeyboardKey.KEY_F, () => sim.GenerateColors());
        
        sim.GenerateRules();
        
        while (!Raylib.WindowShouldClose())
        {
            float dt = Raylib.GetFrameTime();
            
            sim.Update(dt);
            input.Update();
            
            Raylib.BeginDrawing();
            Raylib.ClearBackground(Color.BLACK);
            sim.Draw();
            Raylib.EndDrawing();
        }
        
        Raylib.CloseWindow();
    }
}