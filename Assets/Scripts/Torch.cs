using UnityEngine;

// Факел: ночью горит, днём гаснет (включает GameDirector). В подземелье (always) горит всегда.
public class Torch : MonoBehaviour
{
    public Light torchLight;
    public Renderer flame;
    public bool always;

    float baseIntensity;

    void Awake()
    {
        if (torchLight != null) baseIntensity = torchLight.intensity;
    }

    public void SetLit(bool lit)
    {
        if (always) lit = true;
        if (torchLight != null) torchLight.enabled = lit;
        if (flame != null) flame.enabled = lit;
    }

    void Update()
    {
        // Лёгкое мерцание огня
        if (torchLight != null && torchLight.enabled)
            torchLight.intensity = baseIntensity * (0.85f + Mathf.PerlinNoise(Time.time * 3f, transform.position.x) * 0.3f);
    }
}
