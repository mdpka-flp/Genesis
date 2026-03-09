using System.Numerics;

public interface ISimulation
{
    int TypeCount { get; }
    int ParticleCount { get; }
    
    float InteractionRadius { get; set; }
    float ForceMultiplier { get; set; }
    float Friction { get; set; }
    float MinDistance { get; set; }
    float RepulsionStrength { get; set; }
    
    void Initialize(int particleCount, int typeCount);
    void Update(float deltaTime);
    void Draw();
    
    void GenerateRules();
    void GenerateColors();
    void AddType();
    void RemoveType();
    void SetParticleCount(int newCount);
    void Restart();
}