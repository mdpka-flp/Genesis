using System;
using System.IO;
using System.Reflection;
using Raylib_cs;
using Genesis.Core;
using Genesis.UI;
using Genesis.Input;

static class Program
{
    private static ISimulation? currentSim;
    private static GenesisUI? currentUi;
    
    static void Main()
    {
        // ImGui пишет imgui.ini в рабочий каталог процесса. Держим там каталог
        // настроек, чтобы файл не появлялся рядом с exe; других относительных
        // путей в приложении нет.
        Directory.SetCurrentDirectory(AppPaths.ConfigDirectory);

        Raylib.InitWindow(1600, 900, "Genesis");
        Raylib.SetTargetFPS(60);

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            string resourceName = "logo.png"; 
            
            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using MemoryStream ms = new MemoryStream();
                stream.CopyTo(ms);
                byte[] iconData = ms.ToArray();

                Image icon = Raylib.LoadImageFromMemory(".png", iconData);
                Raylib.SetWindowIcon(icon);
                Raylib.UnloadImage(icon); 
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Не удалось загрузить иконку: {ex.Message}");
        }
        
        currentSim = new Simulation();
        currentSim.Initialize(1000,3);
        currentSim.GenerateRules();
        
        var ui = new GenesisUI(() => currentSim, OnSimulationTypeChanged);
        currentUi = ui;
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

        currentUi?.Shutdown();

        if (currentSim is IDisposable disposable)
            disposable.Dispose();

        Raylib.CloseWindow();
    }
    
    private static void OnSimulationTypeChanged(Type newSimulationType)
    {
        int oldCount = currentSim?.ParticleCount ?? 5000;
        int oldTypes = currentSim?.TypeCount ?? 3;

        // Снимок правил, связей и цветов до освобождения старого бэкенда: новый режим
        // должен стартовать на тех же настройках, иначе один эксперимент нельзя
        // посмотреть в вещественном и комплексном режимах.
        SimulationSettings? settings = currentSim == null
            ? null
            : SimulationSettings.Capture(currentSim);

        // Позиции и скорости тоже переживают смену бэкенда: так одно и то же правило
        // видно в двух режимах на одном и том же кадре, без нового разброса.
        Particle[]? previousParticles = currentSim?.Particles;

        if (currentSim is IDisposable disposable)
            disposable.Dispose();
        
        if (newSimulationType == typeof(Simulation))
            currentSim = new Simulation();
        else if (newSimulationType == typeof(Simulation_ComplexNumbers))
            currentSim = new Simulation_ComplexNumbers();
        else if (newSimulationType == typeof(SimulationGpu))
        {
            try
            {
                currentSim = new SimulationGpu();
            }
            catch (Exception ex)
            {
                // Драйвер может не найтись: вместо падения уходим на CPU и сообщаем
                // об этом в консоль, UI покажет фактический режим.
                Console.WriteLine($"[Genesis] Не удалось создать GPU-режим: {ex.Message}");
                Console.WriteLine("[Genesis] Откат на Simulation (CPU).");
                currentSim = new Simulation();
            }
        }
        else
            return;
        
        currentSim.Initialize(oldCount, oldTypes);

        // Initialize() расставляет частицы заново и обнуляет правила, поэтому снимок
        // применяем после него.
        if (settings != null)
            currentSim.ApplySettings(settings);

        if (previousParticles != null)
            currentSim.RestoreParticles(previousParticles);

        currentUi?.SyncSimulationType(currentSim);
    }
}