using System.Collections.Generic;
using UnityEngine;

// Предмет, который можно поднять (E) и швырнуть (ЛКМ): камень, бочка, ящик.
// ЛОГИКА отдельно от картинки: на этом объекте — Rigidbody, коллайдер и этот скрипт,
// а то, как он выглядит, — дочерний объект (кубики сейчас, настоящая модель потом).
// Брошенный предмет бьёт тварь тем сильнее, чем он тяжелее и быстрее летит.
// Бочки и ящики от сильного удара разлетаются в щепки.
[RequireComponent(typeof(Rigidbody))]
public class Throwable : MonoBehaviour
{
    public static readonly List<Throwable> All = new List<Throwable>();

    public string displayName = "Камень";
    public bool breakable;            // ящик, бочка — ломается от сильного удара
    public float breakSpeed = 9f;     // с какой скорости удара ломается

    public Rigidbody Body { get; private set; }
    public bool Carried { get; set; }
    float thrownAt = -100f;
    bool mine;                        // бросил я — урон считаю я
    readonly HashSet<Enemy> hitThisFlight = new HashSet<Enemy>();

    void Awake() { Body = GetComponent<Rigidbody>(); }
    void OnEnable() { All.Add(this); }
    void OnDisable() { All.Remove(this); }

    public void OnThrown(bool byMe)
    {
        thrownAt = Time.time;
        mine = byMe;
        hitThisFlight.Clear();
    }

    bool Flying => Time.time - thrownAt < 3f;

    void OnCollisionEnter(Collision c)
    {
        if (Carried) return;
        float speed = c.relativeVelocity.magnitude;
        Vector3 at = c.contactCount > 0 ? c.GetContact(0).point : transform.position;

        Enemy e = c.collider.GetComponentInParent<Enemy>();
        if (e != null && Flying && mine && speed > 4f && !hitThisFlight.Contains(e) && e.HealthFraction > 0f)
        {
            hitThisFlight.Add(e);
            float m = Body.mass;
            float dmg = Mathf.Clamp(speed * (0.6f + 0.08f * m), 5f, 45f);
            bool head = at.y - e.transform.position.y > 1.3f * e.bodyScale;
            Vector3 dir = Body.linearVelocity.sqrMagnitude > 0.1f ? Body.linearVelocity.normalized : -c.relativeVelocity.normalized;
            // тяжёлое (бочка, большой камень) сбивает с ног, лёгкое — просто больно
            HitType type = m >= 12f ? HitType.Kick : HitType.Tool;
            EnemyManager.RequestHit(e, new HitInfo { damage = dmg, dir = dir, part = head ? BodyPartType.Head : BodyPartType.Torso, type = type, heavy = m >= 12f });
            e.LocalReact(head ? (int)BodyPartType.Head : (int)BodyPartType.Torso, dir, Mathf.Clamp(m / 8f, 0.6f, 1.6f));
            CombatFx.Play(CombatFx.Snd.Thunk, at, 1f, Mathf.Lerp(1.2f, 0.7f, m / 20f));
            PlayerCamera.Shake(0.15f);
            Body.linearVelocity *= 0.3f;   // отскакивает от тела, а не пролетает дальше
        }
        else if (speed > 5f)
            CombatFx.Play(CombatFx.Snd.Thunk, at, Mathf.Clamp01(speed / 15f), breakable ? 1.1f : 0.8f);

        if (breakable && speed > breakSpeed && Flying) Break(at);
    }

    // Разлетается в щепки
    public void Break(Vector3 at)
    {
        Renderer first = GetComponentInChildren<Renderer>();
        Material chip = first != null ? first.sharedMaterial : null;   // щепки того же цвета, что и сам предмет
        Bounds b = new Bounds(transform.position, Vector3.one * 0.5f);
        foreach (Renderer r in GetComponentsInChildren<Renderer>()) b.Encapsulate(r.bounds);
        for (int i = 0; i < 7; i++)
        {
            GameObject d = GameObject.CreatePrimitive(PrimitiveType.Cube);
            d.layer = 2;
            d.transform.position = b.center + Vector3.Scale(Random.insideUnitSphere, b.extents);
            d.transform.localScale = new Vector3(Random.Range(0.05f, 0.12f), Random.Range(0.15f, 0.4f), Random.Range(0.04f, 0.08f));
            d.transform.rotation = Random.rotation;
            if (chip != null) d.GetComponent<Renderer>().sharedMaterial = chip;
            Rigidbody rb = d.AddComponent<Rigidbody>();
            rb.mass = 0.5f;
            rb.linearVelocity = Body.linearVelocity * 0.4f + Random.insideUnitSphere * 4f + Vector3.up * 2f;
            rb.angularVelocity = Random.insideUnitSphere * 12f;
            Destroy(d, 5f);
        }
        CombatFx.Play(CombatFx.Snd.Clang, at, 0.8f, 0.6f);
        Destroy(gameObject);
    }
}
