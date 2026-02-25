using System;
using System.Numerics;
using System.Collections.Generic;
using System.Threading.Tasks;
using Raylib_cs;

class Simulation
{
    public Particle[] Particles;          // Массив для лучшей локальности кэша
    public float[,] InteractionMatrix;

    public float InteractionRadius;
    public float ForceMultiplier;
    public float Friction;
    public float MinDistance;
    public float RepulsionStrength;

    public int TypeCount;

    private int screenWidth;
    private int screenHeight;

    private Random random;
    private Color[] typeColors;

    // Параметры пространственной сетки
    private int gridWidth, gridHeight;
    private float cellSize;
    private List<int>[,] grid;             // В каждой ячейке список индексов частиц

    public void Initialize(int particleCount, int typeCount)
    {
        screenWidth = Raylib.GetScreenWidth();
        screenHeight = Raylib.GetScreenHeight();

        random = new Random();

        Particles = new Particle[particleCount];
        this.TypeCount = typeCount;

        InteractionRadius = 80f;
        ForceMultiplier = 40f;
        Friction = 0.98f;
        MinDistance = 25f;
        RepulsionStrength = 25f;
        cellSize = InteractionRadius;       // Размер ячейки равен радиусу взаимодействия

        // Расчёт размеров сетки
        gridWidth = (int)Math.Ceiling(screenWidth / cellSize);
        gridHeight = (int)Math.Ceiling(screenHeight / cellSize);
        grid = new List<int>[gridWidth, gridHeight];
        for (int x = 0; x < gridWidth; x++)
            for (int y = 0; y < gridHeight; y++)
                grid[x, y] = new List<int>();

        /*InteractionMatrix = new float[typeCount, typeCount];
        InteractionMatrix[0, 0] = -5;
        InteractionMatrix[0, 1] = -5;
        InteractionMatrix[0, 2] = 5;
        InteractionMatrix[0, 3] = 5;
        InteractionMatrix[0, 4] = -5;

        InteractionMatrix[1, 0] = -5;
        InteractionMatrix[1, 1] = -5;
        InteractionMatrix[1, 2] = -5;
        InteractionMatrix[1, 3] = 5;
        InteractionMatrix[1, 4] = 5;

        InteractionMatrix[2, 0] = 5;
        InteractionMatrix[2, 1] = 5;
        InteractionMatrix[2, 2] = 5;
        InteractionMatrix[2, 3] = -5;
        InteractionMatrix[2, 4] = -5;

        InteractionMatrix[3, 0] = 5;
        InteractionMatrix[3, 1] = 5;
        InteractionMatrix[3, 2] = 5;
        InteractionMatrix[3, 3] = -5;
        InteractionMatrix[3, 4] = 5;

        InteractionMatrix[4, 0] = 5;
        InteractionMatrix[4, 1] = -5;
        InteractionMatrix[4, 2] = 5;
        InteractionMatrix[4, 3] = 5;
        InteractionMatrix[4, 4] = -5;*/

        typeColors = new Color[typeCount];
        GenerateColors();

        /*typeColors = new Color[]
        {
            new Color(255, 80, 80, 255),
            new Color(80, 255, 120, 255),
            new Color(80, 120, 255, 255),
            new Color(89, 241, 255, 255),
            new Color(142, 89, 255, 255),
            new Color(255, 238, 89, 255)
        };*/

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

    // Обновление сетки — распределяем частицы по ячейкам
    private void UpdateGrid()
    {
        // Очищаем ячейки
        for (int x = 0; x < gridWidth; x++)
            for (int y = 0; y < gridHeight; y++)
                grid[x, y].Clear();

        // Заполняем заново
        for (int i = 0; i < Particles.Length; i++)
        {
            var p = Particles[i];
            int gx = (int)(p.Position.X / cellSize);
            int gy = (int)(p.Position.Y / cellSize);
            // Обработка выхода за границы (с учётом периодичности)
            gx = (gx + gridWidth) % gridWidth;
            gy = (gy + gridHeight) % gridHeight;
            grid[gx, gy].Add(i);
        }
    }

    public void Update(float deltaTime)
    {
        UpdateGrid();

        // Многопоточное обновление частиц
        Parallel.For(0, Particles.Length, i =>
        {
            Particle a = Particles[i];
            Vector2 acceleration = Vector2.Zero;

            // Определяем ячейку частицы a
            int gx = (int)(a.Position.X / cellSize);
            int gy = (int)(a.Position.Y / cellSize);
            gx = (gx + gridWidth) % gridWidth;
            gy = (gy + gridHeight) % gridHeight;

            // Проверяем 3x3 окрестность ячеек (с учётом периодичности)
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    int nx = (gx + dx + gridWidth) % gridWidth;
                    int ny = (gy + dy + gridHeight) % gridHeight;
                    foreach (int j in grid[nx, ny])
                    {
                        if (i == j) continue;
                        Particle b = Particles[j];

                        float dxPos = b.Position.X - a.Position.X;
                        float dyPos = b.Position.Y - a.Position.Y;

                        // Периодические границы
                        if (dxPos > screenWidth / 2f) dxPos -= screenWidth;
                        else if (dxPos < -screenWidth / 2f) dxPos += screenWidth;
                        if (dyPos > screenHeight / 2f) dyPos -= screenHeight;
                        else if (dyPos < -screenHeight / 2f) dyPos += screenHeight;

                        float distanceSq = dxPos * dxPos + dyPos * dyPos;
                        if (distanceSq > 0 && distanceSq < InteractionRadius * InteractionRadius)
                        {
                            float distance = (float)Math.Sqrt(distanceSq);
                            Vector2 dir = new Vector2(dxPos, dyPos) / distance;
                            float rule = InteractionMatrix[a.Type, b.Type];

                            if (distance < MinDistance)
                            {
                                float repel = (1f - distance / MinDistance) * RepulsionStrength;
                                acceleration -= dir * repel;
                            }
                            else
                            {
                                float strength = rule * (1f - distance / InteractionRadius) * ForceMultiplier;
                                acceleration += dir * strength;
                            }
                        }
                    }
                }
            }

            // Интегрирование
            a.Velocity += acceleration * deltaTime;
            a.Velocity *= Friction;

            float maxSpeed = 200f;
            if (a.Velocity.LengthSquared() > maxSpeed * maxSpeed)
                a.Velocity = Vector2.Normalize(a.Velocity) * maxSpeed;

            a.Position += a.Velocity * deltaTime;

            // Периодические границы
            if (a.Position.X < 0) a.Position.X += screenWidth;
            if (a.Position.X > screenWidth) a.Position.X -= screenWidth;
            if (a.Position.Y < 0) a.Position.Y += screenHeight;
            if (a.Position.Y > screenHeight) a.Position.Y -= screenHeight;

            Particles[i] = a;
        });
    }

    public void Draw()
    {
        foreach (var p in Particles)
            Raylib.DrawCircleV(p.Position, 3f, typeColors[p.Type]);
        
        // Статистика (не обязательно, можно оставить)
        Raylib.DrawText("particle count: " + Particles.Length, 10, 10, 50, Color.WHITE);
        Raylib.DrawText("type count: " + TypeCount, 10, 70, 50, Color.WHITE);
    }

    public void GenerateRules()
    {
        InteractionMatrix = new float[TypeCount, TypeCount];
        for (int i = 0; i < TypeCount; i++)
            for (int j = 0; j < TypeCount; j++)
                InteractionMatrix[i, j] = (float)(random.Next(-10, 10));
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
        int newTypeCount = TypeCount + 1;
        Initialize(Particles.Length, newTypeCount);
        GenerateRules();
    }
    
    public void RemoveType()
    {
        if (TypeCount > 1)
        {
            int newTypeCount = TypeCount - 1;
            Initialize(Particles.Length, newTypeCount);
            GenerateRules();
        }
    }
}