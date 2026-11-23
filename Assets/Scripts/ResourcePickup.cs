using System.Collections.Generic;
using UnityEngine;

public enum ResourceType { Wood, Stone }

// Ресурс, лежащий на земле (бревно или камень). Его можно пинать.
// Подойди поближе — он сам прилетит в инвентарь.
public class ResourcePickup : MonoBehaviour
{
    public ResourceType type;
    public int amount = 1;

    public static readonly List<ResourcePickup> All = new List<ResourcePickup>();
    static Material woodMat, stoneMat;

    float canCollectAt; // небольшая пауза, чтобы ресурс успел красиво выпасть

    public bool CanCollect => Time.time >= canCollectAt;

    void OnEnable() { All.Add(this); }
    void OnDisable() { All.Remove(this); }

    public static string Name(ResourceType t) => t == ResourceType.Wood ? "Дерево" : "Камень";

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
        bool wood = type == ResourceType.Wood;
        GameObject go = GameObject.CreatePrimitive(wood ? PrimitiveType.Cylinder : PrimitiveType.Cube);
        go.name = wood ? "Pickup_Wood" : "Pickup_Stone";
        go.layer = 2;
        go.transform.position = pos;
        go.transform.localScale = wood ? new Vector3(0.25f, 0.4f, 0.25f) : Vector3.one * 0.4f;
        go.transform.rotation = Random.rotation;
        if (wood)
        {
            // у цилиндра коллайдер-капсула — заменим на коробку, чтобы не катался бесконечно
            Object.Destroy(go.GetComponent<Collider>());
            go.AddComponent<BoxCollider>().size = new Vector3(1f, 2f, 1f);
        }

        Renderer r = go.GetComponent<Renderer>();
        r.sharedMaterial = wood ? (woodMat ??= MakeMat(new Color(0.55f, 0.38f, 0.2f)))
                                : (stoneMat ??= MakeMat(new Color(0.6f, 0.6f, 0.62f)));

        Rigidbody rb = go.AddComponent<Rigidbody>();
        rb.mass = 0.5f;
        rb.AddForce(Random.insideUnitSphere * 2.5f + Vector3.up * 3f, ForceMode.VelocityChange);

        ResourcePickup p = go.AddComponent<ResourcePickup>();
        p.type = type;
        p.amount = amount;
        p.canCollectAt = Time.time + 0.8f;
    }

    static Material MakeMat(Color c)
    {
        Shader s = Shader.Find("Universal Render Pipeline/Lit");
        if (s == null) s = Shader.Find("Standard");
        Material m = new Material(s);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        return m;
    }
}
