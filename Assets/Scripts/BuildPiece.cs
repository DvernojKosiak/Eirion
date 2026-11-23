using System.Collections.Generic;
using UnityEngine;

// Одна деталь конструктора: стена, окно, дверь, помост, крыша...
// Прочность есть, но её будут отнимать только враги (игрок свои постройки разбирает молотом).
// Точка (pivot) детали всегда внизу по центру.
public class BuildPiece : MonoBehaviour
{
    public string pieceId = "";              // имя детали (совпадает с именем префаба)
    public int netId;                        // номер детали в мире (одинаковый у всех игроков)
    public string displayName = "Постройка";
    public string category = "Дерево";
    public float maxHealth = 300f;
    public Vector3 size = new Vector3(4f, 3f, 0.3f); // габариты в метрах
    public int costWood = 0;                 // сколько стоит построить
    public int costStone = 0;
    public bool buildable = true;            // показывать ли в меню молота

    public float Health { get; private set; }

    public static readonly List<BuildPiece> All = new List<BuildPiece>();
    public static readonly Dictionary<int, BuildPiece> ById = new Dictionary<int, BuildPiece>();

    Renderer[] renderers;
    Color[] baseColors;
    MaterialPropertyBlock block;
    static readonly Collider[] buffer = new Collider[64];

    public Vector3 Center => transform.position + transform.up * (size.y * 0.5f);

    public string CostText
    {
        get
        {
            string s = "";
            if (costWood > 0) s += costWood + " дерева";
            if (costStone > 0) s += (s.Length > 0 ? ", " : "") + costStone + " камня";
            return s.Length > 0 ? s : "бесплатно";
        }
    }

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

    void OnEnable()
    {
        All.Add(this);
        if (netId != 0) ById[netId] = this;
    }

    void OnDisable()
    {
        All.Remove(this);
        if (ById.TryGetValue(netId, out BuildPiece p) && p == this) ById.Remove(netId);
    }

    public void AssignId(int id)
    {
        if (ById.TryGetValue(netId, out BuildPiece p) && p == this) ById.Remove(netId);
        netId = id;
        ById[id] = this;
    }

    // Урон (для врагов). Вызывать через BuildingManager.Damage — он и уберёт деталь, когда прочность кончится
    public void ApplyDamage(float amount)
    {
        if (Health <= 0f) return;
        Health = Mathf.Max(0f, Health - amount);
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

    // Убрать деталь из мира. debris = рассыпаться на обломки
    public void Remove(bool debris)
    {
        if (!gameObject.activeSelf) return;
        if (debris) SpawnDebris();
        gameObject.SetActive(false); // сразу пропадает из мира и из списка All
        Destroy(gameObject);
    }

    void SpawnDebris()
    {
        Material mat = renderers.Length > 0 ? renderers[0].sharedMaterial : null;
        float volume = size.x * size.y * Mathf.Max(size.z, 0.5f);
        int count = Mathf.Clamp(Mathf.RoundToInt(volume / 1.5f), 4, 14);
        for (int i = 0; i < count; i++)
        {
            GameObject d = GameObject.CreatePrimitive(PrimitiveType.Cube);
            d.name = "Debris";
            d.layer = 2; // "Ignore Raycast": прицел и камера сквозь обломки
            float s = Random.Range(0.25f, 0.6f);
            d.transform.localScale = new Vector3(s, s * Random.Range(0.5f, 1.5f), s);
            Vector3 local = new Vector3(Random.Range(-0.5f, 0.5f) * size.x,
                                        Random.Range(-0.5f, 0.5f) * size.y,
                                        Random.Range(-0.5f, 0.5f) * size.z);
            d.transform.position = Center + transform.rotation * local;
            d.transform.rotation = Random.rotation;
            if (mat != null) d.GetComponent<Renderer>().sharedMaterial = mat;
            Rigidbody rb = d.AddComponent<Rigidbody>();
            rb.mass = s * 3f;
            rb.AddForce(Random.insideUnitSphere * 3f + Vector3.up * 2f, ForceMode.VelocityChange);
            Destroy(d, Random.Range(8f, 12f));
        }
    }

    // Детали, которые касаются этой
    public List<BuildPiece> GetNeighbours()
    {
        var result = new List<BuildPiece>();
        int n = Physics.OverlapBoxNonAlloc(Center, size * 0.5f + Vector3.one * 0.1f, buffer,
                                           transform.rotation, ~(1 << 2), QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            BuildPiece bp = buffer[i].GetComponentInParent<BuildPiece>();
            if (bp != null && bp != this && !result.Contains(bp)) result.Add(bp);
        }
        return result;
    }
}
