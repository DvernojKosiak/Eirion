using System.Collections.Generic;
using UnityEngine;

// Источник ресурсов в мире: дерево (рубить топором) или камень (бить киркой).
// Каждый удар даёт немного ресурса, последний — бонус. Дерево падает, камень рассыпается.
// Удары идут через BuildingManager, чтобы у всех игроков было одинаково.
public class ResourceNode : MonoBehaviour
{
    public int netId;                          // номер в мире (одинаковый у всех)
    public string displayName = "Дерево";
    public ResourceType resource = ResourceType.Wood;
    public Tool requiredTool = Tool.Axe;
    public int maxHits = 4;
    public int yieldPerHit = 2;                // за каждый удар
    public int bonusOnBreak = 6;               // за последний удар

    public int HitsLeft { get; private set; }

    public static readonly Dictionary<int, ResourceNode> ById = new Dictionary<int, ResourceNode>();

    Vector3 baseScale;
    Quaternion baseRotation;
    float shake;
    bool broken;

    public Vector3 DropPoint => transform.position + Vector3.up * 1.2f;

    void Awake()
    {
        HitsLeft = maxHits;
        baseScale = transform.localScale;
        baseRotation = transform.rotation;
    }

    void OnEnable() { if (netId != 0) ById[netId] = this; }
    void OnDisable() { Unregister(); }

    void Unregister()
    {
        if (ById.TryGetValue(netId, out ResourceNode n) && n == this) ById.Remove(netId);
    }

    public void ApplyHit(int hitsLeft)
    {
        if (broken) return;
        HitsLeft = hitsLeft;
        shake = 1f;
        if (resource == ResourceType.Stone) // камень становится меньше
            transform.localScale = baseScale * Mathf.Lerp(0.45f, 1f, (float)hitsLeft / maxHits);
        if (hitsLeft <= 0) Break();
    }

    void Update()
    {
        if (broken || shake <= 0f) return;
        shake = Mathf.Max(0f, shake - Time.deltaTime * 4f);
        float a = Mathf.Sin(Time.time * 60f) * shake * 2f;
        transform.rotation = baseRotation * Quaternion.Euler(a, 0f, a * 0.5f);
    }

    void Break()
    {
        broken = true;
        Unregister();
        foreach (Transform t in GetComponentsInChildren<Transform>()) t.gameObject.layer = 2; // больше не цель

        if (resource == ResourceType.Wood)
        {
            // Дерево падает в случайную сторону и исчезает
            transform.rotation = baseRotation;
            Rigidbody rb = gameObject.AddComponent<Rigidbody>();
            rb.mass = 200f;
            Vector3 side = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f) * Vector3.forward;
            rb.AddForceAtPosition(side * 600f, transform.position + Vector3.up * 5f, ForceMode.Impulse);
            Destroy(gameObject, 4f);
        }
        else
        {
            // Камень рассыпается на обломки
            Renderer r = GetComponentInChildren<Renderer>();
            Material mat = r != null ? r.sharedMaterial : null;
            for (int i = 0; i < 6; i++)
            {
                GameObject d = GameObject.CreatePrimitive(PrimitiveType.Cube);
                d.layer = 2;
                d.transform.position = transform.position + Random.insideUnitSphere * 0.6f + Vector3.up * 0.4f;
                d.transform.localScale = Vector3.one * Random.Range(0.2f, 0.45f);
                d.transform.rotation = Random.rotation;
                if (mat != null) d.GetComponent<Renderer>().sharedMaterial = mat;
                Rigidbody rb = d.AddComponent<Rigidbody>();
                rb.mass = 1f;
                rb.AddForce(Random.insideUnitSphere * 3f + Vector3.up * 2f, ForceMode.VelocityChange);
                Destroy(d, Random.Range(6f, 9f));
            }
            gameObject.SetActive(false);
            Destroy(gameObject);
        }
    }

    // Для тех, кто зашёл позже: этого ресурса уже нет
    public void RemoveInstant()
    {
        broken = true;
        Unregister();
        gameObject.SetActive(false);
        Destroy(gameObject);
    }
}
