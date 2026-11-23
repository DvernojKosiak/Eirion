using System.Collections.Generic;
using UnityEngine;

// Одна деталь постройки: стена, ворота, помост, лестница, башня...
// У неё есть прочность (здоровье). Если прочность кончилась — деталь
// разваливается на обломки. Если под ней пропала опора — тоже падает.
// Точка (pivot) детали всегда внизу по центру.
public class BuildPiece : MonoBehaviour
{
    public string displayName = "Постройка";
    public float maxHealth = 300f;
    public Vector3 size = new Vector3(4f, 4f, 0.5f); // габариты детали в метрах
    public bool buildable = true;                    // показывать ли в меню стройки

    public float Health { get; private set; }

    // Список всех деталей в мире — нужен для проверки опоры
    public static readonly List<BuildPiece> All = new List<BuildPiece>();

    Renderer[] renderers;
    Color[] baseColors;
    MaterialPropertyBlock block;
    static readonly Collider[] buffer = new Collider[64];

    // Центр детали в мире
    public Vector3 Center => transform.position + transform.up * (size.y * 0.5f);

    void Awake()
    {
        Health = maxHealth;
        renderers = GetComponentsInChildren<Renderer>();
        baseColors = new Color[renderers.Length];
        for (int i = 0; i < renderers.Length; i++)
        {
            Material m = renderers[i].sharedMaterial;
            baseColors[i] = m == null ? Color.white
                          : m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor")
                          : m.HasProperty("_Color") ? m.color : Color.white;
        }
        block = new MaterialPropertyBlock();
    }

    void OnEnable() { All.Add(this); }
    void OnDisable() { All.Remove(this); }

    // Не вызывай напрямую — используй BuildingManager.Damage (так проще будет сделать сеть)
    public void ApplyDamage(float amount)
    {
        if (Health <= 0f) return;
        Health = Mathf.Max(0f, Health - amount);
        UpdateDamageLook();
        if (Health <= 0f) Break();
    }

    // Чем меньше прочность — тем темнее деталь
    void UpdateDamageLook()
    {
        float t = Health / maxHealth;
        for (int i = 0; i < renderers.Length; i++)
        {
            Color c = Color.Lerp(baseColors[i] * 0.3f, baseColors[i], t);
            c.a = baseColors[i].a;
            renderers[i].GetPropertyBlock(block);
            block.SetColor("_BaseColor", c);
            block.SetColor("_Color", c);
            renderers[i].SetPropertyBlock(block);
        }
    }

    // Развалиться на обломки
    public void Break()
    {
        if (!gameObject.activeSelf) return;
        SpawnDebris();
        gameObject.SetActive(false); // сразу убираем из мира и из списка All
        Destroy(gameObject);
        BuildingManager.RequestStabilityCheck();
    }

    void SpawnDebris()
    {
        Material mat = renderers.Length > 0 ? renderers[0].sharedMaterial : null;
        float volume = size.x * size.y * size.z;
        int count = Mathf.Clamp(Mathf.RoundToInt(volume / 1.5f), 4, 14);
        for (int i = 0; i < count; i++)
        {
            GameObject d = GameObject.CreatePrimitive(PrimitiveType.Cube);
            d.name = "Debris";
            d.layer = 2; // слой "Ignore Raycast" — удары и камера сквозь обломки
            float s = Random.Range(0.3f, 0.8f);
            d.transform.localScale = new Vector3(s, s * Random.Range(0.5f, 1.5f), s);
            Vector3 local = new Vector3(Random.Range(-0.5f, 0.5f) * size.x,
                                        Random.Range(-0.5f, 0.5f) * size.y,
                                        Random.Range(-0.5f, 0.5f) * size.z);
            d.transform.position = Center + transform.rotation * local;
            d.transform.rotation = Random.rotation;
            if (mat != null) d.GetComponent<Renderer>().sharedMaterial = mat;
            Rigidbody rb = d.AddComponent<Rigidbody>();
            rb.mass = s * 20f;
            rb.AddForce(Random.insideUnitSphere * 4f + Vector3.up * 2f, ForceMode.VelocityChange);
            Destroy(d, Random.Range(3f, 5f));
        }
    }

    // Детали, которые касаются этой
    public List<BuildPiece> GetNeighbours()
    {
        var result = new List<BuildPiece>();
        int n = Physics.OverlapBoxNonAlloc(Center, size * 0.5f + Vector3.one * 0.1f, buffer,
                                           transform.rotation, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            BuildPiece bp = buffer[i].GetComponentInParent<BuildPiece>();
            if (bp != null && bp != this && !result.Contains(bp)) result.Add(bp);
        }
        return result;
    }
}
