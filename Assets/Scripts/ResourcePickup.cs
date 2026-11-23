using System.Collections.Generic;
using UnityEngine;

// Все виды ресурсов в игре
public enum ResourceType { Wood, Stone, Plank, StoneBlock, Lailillium }

// Ресурс, лежащий на земле. Его можно пинать.
// Подойди поближе — он сам прилетит в инвентарь.
public class ResourcePickup : MonoBehaviour
{
    public ResourceType type;
    public int amount = 1;

    public static readonly List<ResourcePickup> All = new List<ResourcePickup>();
    static readonly Dictionary<ResourceType, Material> mats = new Dictionary<ResourceType, Material>();

    float canCollectAt; // небольшая пауза, чтобы ресурс успел красиво выпасть

    public bool CanCollect => Time.time >= canCollectAt;

    void OnEnable() { All.Add(this); }
    void OnDisable() { All.Remove(this); }

    public static string Name(ResourceType t)
    {
        switch (t)
        {
            case ResourceType.Wood: return "Бревно";
            case ResourceType.Stone: return "Камень";
            case ResourceType.Plank: return "Доски";
            case ResourceType.StoneBlock: return "Кам. блок";
            default: return "Лайлилиум";
        }
    }

    public static Color ColorOf(ResourceType t)
    {
        switch (t)
        {
            case ResourceType.Wood: return new Color(0.5f, 0.34f, 0.18f);
            case ResourceType.Stone: return new Color(0.55f, 0.55f, 0.57f);
            case ResourceType.Plank: return new Color(0.78f, 0.6f, 0.36f);
            case ResourceType.StoneBlock: return new Color(0.75f, 0.74f, 0.7f);
            default: return new Color(0.65f, 0.4f, 1f);
        }
    }

    // Насыпать ресурсы кучкой (до 5 штук, общая сумма = amount)
    public static void Spawn(ResourceType type, int amount, Vector3 position)
    {
        if (amount <= 0) return;
        int pieces = Mathf.Clamp(amount, 1, 5);
        int left = amount;
        for (int i = 0; i < pieces; i++)
        {
            int a = i == pieces - 1 ? left : amount / pieces;
            left -= a;
            if (a > 0) SpawnOne(type, a, position + Random.insideUnitSphere * 0.5f);
        }
    }

    static void SpawnOne(ResourceType type, int amount, Vector3 pos)
    {
        PrimitiveType shape = type == ResourceType.Wood ? PrimitiveType.Cylinder : PrimitiveType.Cube;
        GameObject go = GameObject.CreatePrimitive(shape);
        go.name = "Pickup_" + type;
        go.layer = 2;
        go.transform.position = pos;
        go.transform.rotation = Random.rotation;
        switch (type)
        {
            case ResourceType.Wood: go.transform.localScale = new Vector3(0.25f, 0.4f, 0.25f); break;
            case ResourceType.Plank: go.transform.localScale = new Vector3(0.7f, 0.06f, 0.25f); break;
            case ResourceType.Lailillium: go.transform.localScale = new Vector3(0.15f, 0.35f, 0.15f); break;
            default: go.transform.localScale = Vector3.one * 0.35f; break;
        }
        if (type == ResourceType.Wood)
        {
            // у цилиндра коллайдер-капсула — заменим на коробку, чтобы не катался бесконечно
            Object.Destroy(go.GetComponent<Collider>());
            go.AddComponent<BoxCollider>().size = new Vector3(1f, 2f, 1f);
        }
        go.GetComponent<Renderer>().sharedMaterial = MatFor(type);

        Rigidbody rb = go.AddComponent<Rigidbody>();
        rb.mass = 0.5f;
        rb.AddForce(Random.insideUnitSphere * 2.5f + Vector3.up * 3f, ForceMode.VelocityChange);

        ResourcePickup p = go.AddComponent<ResourcePickup>();
        p.type = type;
        p.amount = amount;
        p.canCollectAt = Time.time + 0.8f;
    }

    static Material MatFor(ResourceType t)
    {
        if (mats.TryGetValue(t, out Material m) && m != null) return m;
        Shader s = Shader.Find("Universal Render Pipeline/Lit");
        if (s == null) s = Shader.Find("Standard");
        m = new Material(s);
        Color c = ColorOf(t);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        if (t == ResourceType.Lailillium && m.HasProperty("_EmissionColor"))
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", c * 3f);
        }
        mats[t] = m;
        return m;
    }
}
