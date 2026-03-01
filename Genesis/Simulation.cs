using System;
using System.Numerics;
using System.Collections.Generic;
using System.Threading.Tasks;
using Raylib_cs;

public class Simulation
{
    public Particle[] Particles;
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
    
    private int gridWidth, gridHeight;
    private float cellSize;
    //private List<int>[,] grid;
    private int[] sortedIndexes;
    private int[] cellStart;
    private int[] cellCount;
    private int[] nextPos;

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
        cellSize = InteractionRadius;
        
        gridWidth = (int)Math.Ceiling(screenWidth / cellSize);
        gridHeight = (int)Math.Ceiling(screenHeight / cellSize);

        sortedIndexes = new int[particleCount];
        cellStart = new int[gridWidth * gridHeight + 1];
        cellCount = new int[gridWidth * gridHeight];
        nextPos = new int[gridWidth * gridHeight + 1];

        typeColors = new Color[typeCount];
        GenerateColors();
        
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

    public void Update(float deltaTime)
    {
        BuildSpatialLookup();
        
        Parallel.For(0, Particles.Length, i =>
        {
            Particle a = Particles[i];
            Vector2 acceleration = Vector2.Zero;
            
            int gx = (int)(a.Position.X / cellSize);
            int gy = (int)(a.Position.Y / cellSize);
            gx = (gx + gridWidth) % gridWidth;
            gy = (gy + gridHeight) % gridHeight;

            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    int nx = (gx + dx + gridWidth) % gridWidth;
                    int ny = (gy + dy + gridHeight) % gridHeight;
                    int ncell = ny * gridWidth + nx;

                    int start = cellStart[ncell];
                    int end = cellStart[ncell + 1];

                    for (int idx = start; idx < end; idx++)
                    {
                        int j = sortedIndexes[idx];
                        if (i == j) continue;
                        Particle b = Particles[j];

                        float dxPos = b.Position.X - a.Position.X;
                        float dyPos = b.Position.Y - a.Position.Y;
                        
                        if (dxPos > screenWidth / 2f) dxPos -= screenWidth;
                        else if (dxPos < -screenWidth / 2f) dxPos += screenWidth;
                        if (dyPos > screenHeight / 2f) dyPos -= screenHeight;
                        else if (dyPos < -screenHeight / 2f) dyPos += screenHeight;

                        float distanceSq = dxPos * dxPos + dyPos * dyPos;
                        if (distanceSq > 0 && distanceSq < InteractionRadius * InteractionRadius)
                        {
                            float distance = (float)MathF.Sqrt(distanceSq);
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
            
            a.Velocity += acceleration * deltaTime;
            a.Velocity *= Friction;

            float maxSpeed = 200f;
            if (a.Velocity.LengthSquared() > maxSpeed * maxSpeed)
                a.Velocity = Vector2.Normalize(a.Velocity) * maxSpeed;

            a.Position += a.Velocity * deltaTime;
            
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
        
        Raylib.DrawText("particle count: " + Particles.Length, 10, 10, 50, Color.White);
        Raylib.DrawText("type count: " + TypeCount, 10, 70, 50, Color.White);
        int fps = Raylib.GetFPS();
        Raylib.DrawText($"FPS: {fps}", 10, 130, 50, Color.White);
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

    public void SetParticleCount(int newCount)
    {
        if (newCount == Particles.Length) return;

        // Создаём новый массив нужного размера
        Particle[] newParticles = new Particle[newCount];

        // Копируем столько старых частиц, сколько влезет
        int copyCount = Math.Min(newCount, Particles.Length);
        Array.Copy(Particles, newParticles, copyCount);

        // Если нужно больше частиц – добавляем случайные
        for (int i = copyCount; i < newCount; i++)
        {
            newParticles[i] = new Particle
            {
                Position = new Vector2(random.Next(0, screenWidth), random.Next(0, screenHeight)),
                Velocity = Vector2.Zero,
                Type = random.Next(0, TypeCount)
            };
        }

        Particles = newParticles;

        // Пересоздаём вспомогательные массивы для spatial lookup
        sortedIndexes = new int[newCount];
        // Массивы cellStart, cellCount, nextPos пересоздавать не нужно – их размер зависит только от сетки,
        // но они будут заполнены заново при следующем BuildSpatialLookup().
    }

    public void BuildSpatialLookup()
    {
        int totalCells = gridWidth * gridHeight;
        
        Array.Clear(cellCount, 0, totalCells);
        
        for (int i = 0; i < Particles.Length; i++)
        {
            int gx = (int)(Particles[i].Position.X / cellSize);
            int gy = (int)(Particles[i].Position.Y / cellSize);
            gx = (gx + gridWidth) % gridWidth;
            gy = (gy + gridHeight) % gridHeight;
            int cellId = gy * gridWidth + gx;
            cellCount[cellId]++;
        }
        
        cellStart[0] = 0;
        for (int c = 0; c < totalCells; c++)
        {
            cellStart[c + 1] = cellStart[c] + cellCount[c];
        }
        
        Array.Copy(cellStart, nextPos, cellStart.Length);
        
        for (int i = 0; i < Particles.Length; i++)
        {
            int gx = (int)(Particles[i].Position.X / cellSize);
            int gy = (int)(Particles[i].Position.Y / cellSize);
            gx = (gx + gridWidth) % gridWidth;
            gy = (gy + gridHeight) % gridHeight;
            int cellId = gy * gridWidth + gx;

            int pos = nextPos[cellId];
            sortedIndexes[pos] = i;
            nextPos[cellId]++;
        }
    }
}