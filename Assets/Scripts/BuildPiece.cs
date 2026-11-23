using System.Collections.Generic;
using UnityEngine;

public enum StationType { None, Workbench, Stonecutter }

// Одна деталь конструктора: стена, окно, дверь, помост, крыша, станок...
// Прочность отнимают твари. Игрок свои постройки разбирает молотом.
// Если под деталью пропала опора — она по-настоящему падает и разбивается.
// Точка (pivot) детали всегда внизу по центру.
public class BuildPiece : MonoBehaviour
{
    public string pieceId = "";              // имя детали (совпадает с именем префаба)
    public int netId;                        // номер детали в мире (одинаковый у всех игроков)
    public string displayName = "Постройка";
    public string category = "Дерево";
    public float maxHealth = 300f;
    public Vector3 size = new Vector3(4f, 3f, 0.3f); // габариты в метрах
    public ResourceType[] costTypes = new ResourceType[0]; // из чего строится
    public int[] costAmounts = new int[0];
    public StationType requiredStation = StationType.None; // рядом с каким станком можно строить
    public StationType station = StationType.None;         // эта деталь сама станок?
    public bool buildable = true;            // показывать ли в меню молота
    public bool village;                     // часть деревни (её защищаем)
    public bool locked;                      // нельзя разобрать или взять молотом

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
            for (int i = 0; i < costTypes.Length && i < costAmounts.Length; i++)
                s += (s.Length > 0 ? ", " : "") + costAmounts[i] + " " + ResourcePickup.Name(costTypes[i]).ToLower();
            return s.Length > 0 ? s : "бесплатно";
        }
    }

    public static string StationName(StationType t) => t == StationType.Workbench ? "Верстак" : t == StationType.Stonecutter ? "Каменотёс" : "";

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

    // Урон (от тварей). Вызывать через BuildingManager.Damage — он и уберёт деталь, когда прочность кончится
    public void ApplyDamage(float amount)
    {
        if (Health <= 0f) return;
        SetHealth(Health - amount);
    }

    // Задать прочность (урон, починка, сеть). Чем меньше — тем темнее деталь
    public void SetHealth(float value)
    {
        Health = Mathf.Clamp(value, 0f, maxHealth);
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

    // Деталь без опоры: становится физическим телом, падает и разбивается от удара
    public void Fall()
    {
        if (!enabled) return;
        enabled = false; // убирает из списков All и ById
        foreach (Transform t in GetComponentsInChildren<Transform>()) t.gameObject.layer = 2;
        Rigidbody rb = gameObject.AddComponent<Rigidbody>();
        rb.mass = Mathf.Max(20f, size.x * size.y * size.z * 150f);
        rb.AddTorque(Random.insideUnitSphere * rb.mass * 0.5f, ForceMode.Impulse);
        gameObject.AddComponent<FallingPiece>().piece = this;
    }

    public void SpawnDebris()
    {
        Material mat = renderers != null && renderers.Length > 0 ? renderers[0].sharedMaterial : null;
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

    // Стоит ли на земле / скале / плато (на чём-то, что не постройка)
    public bool IsGrounded()
    {
        Vector3 half = new Vector3(size.x * 0.45f, 0.12f, size.z * 0.45f);
        int n = Physics.OverlapBoxNonAlloc(transform.position, half, buffer, transform.rotation, ~(1 << 2), QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            Collider c = buffer[i];
            if (c.GetComponentInParent<BuildPiece>() != null) continue;
            if (c is CharacterController) continue;           // игроки и твари не опора
            if (c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic) continue;
            return true;
        }
        return false;
    }
}
