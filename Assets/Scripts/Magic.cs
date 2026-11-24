using System.Collections.Generic;
using UnityEngine;

// Заклинания посоха и выстрелы бластера.
//   0 — огненный шар: летит, взрывается, отбрасывает и поджигает тварей
//   1 — молния: бьёт с неба в точку, оглушает, перескакивает на соседних тварей
//   2 — луч бластера: квадратный пучок света, прожигает дыру в теле, по стенам растекается
// У того, кто стреляет, заклинание «настоящее» (считает урон). У остальных — только картинка.
public static class Magic
{
    public const int Fireball = 0, Lightning = 1, Bolt = 2;

    public static void Spawn(int kind, Vector3 a, Vector3 b, float power, bool authoritative, Transform shooterRoot)
    {
        switch (kind)
        {
            case Fireball: FireballShot.Launch(a, b, power, authoritative, shooterRoot); break;
            case Lightning: LightningStrike.Begin(a, power, authoritative); break;
            case Bolt: BlasterBolt.Launch(a, b, authoritative, shooterRoot); break;
        }
    }

    // ---------- Общие помощники ----------
    static readonly Dictionary<int, Material> mats = new Dictionary<int, Material>();

    // Светящийся материал (кэшируется по цвету)
    public static Material Glow(Color c, float intensity)
    {
        int key = Mathf.RoundToInt(c.r * 255) | Mathf.RoundToInt(c.g * 255) << 8 | Mathf.RoundToInt(c.b * 255) << 16 | Mathf.RoundToInt(intensity * 10) << 24;
        if (mats.TryGetValue(key, out Material m) && m != null) return m;
        Shader s = Shader.Find("Universal Render Pipeline/Lit");
        m = new Material(s != null ? s : Shader.Find("Standard"));
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        if (intensity > 0f && m.HasProperty("_EmissionColor")) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", c * intensity); }
        mats[key] = m;
        return m;
    }

    public static GameObject Piece(PrimitiveType type, Vector3 pos, Quaternion rot, Vector3 scale, Material m, Transform parent = null)
    {
        GameObject g = GameObject.CreatePrimitive(type);
        g.layer = 2;
        Object.Destroy(g.GetComponent<Collider>());
        if (parent != null) g.transform.SetParent(parent, false);
        g.transform.SetPositionAndRotation(pos, rot);
        g.transform.localScale = scale;
        Renderer r = g.GetComponent<Renderer>();
        r.sharedMaterial = m;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return g;
    }

    // Вспышка света, которая быстро гаснет
    public static void Flash(Vector3 at, Color c, float intensity, float range, float time)
    {
        GameObject g = new GameObject("Flash");
        g.transform.position = at;
        Light l = g.AddComponent<Light>();
        l.type = LightType.Point;
        l.color = c;
        l.intensity = intensity;
        l.range = range;
        l.shadows = LightShadows.None;
        g.AddComponent<FadeLight>().life = time;
    }

    // Луч: во что попал на пути от from до to. bp — часть тела, если попал в тварь
    public static bool Trace(Vector3 from, Vector3 to, Transform shooterRoot, out RaycastHit hit, out BodyPart bp)
    {
        hit = default; bp = null;
        Vector3 d = to - from;
        float len = d.magnitude;
        if (len < 0.0001f) return false;
        RaycastHit[] hits = Physics.RaycastAll(from, d / len, len, ~(1 << 2), QueryTriggerInteraction.Collide);
        System.Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
        foreach (RaycastHit h in hits)
        {
            if (shooterRoot != null && h.transform.root == shooterRoot) continue;
            BodyPart p = h.collider.GetComponentInParent<BodyPart>();
            if (p != null)
            {
                if (!p.enabled || p.lost || p.owner == null) continue;
                hit = h; bp = p; return true;
            }
            if (h.collider.isTrigger) continue;
            if (h.collider.GetComponentInParent<Enemy>() != null) continue;
            if (h.collider.GetComponentInParent<PlayerMovement>() != null || h.collider.GetComponentInParent<NetworkPlayer>() != null) continue;
            hit = h; return true;
        }
        return false;
    }

    public static bool NearLocalPlayer(Vector3 at, float r)
    {
        PlayerHealth me = PlayerHealth.Local;
        return me != null && Vector3.Distance(me.transform.position, at) < r;
    }
}

// Гаснущий свет
public class FadeLight : MonoBehaviour
{
    public float life = 0.2f;
    Light l;
    float start, age;
    void Start() { l = GetComponent<Light>(); start = l != null ? l.intensity : 0f; }
    void Update()
    {
        age += Time.deltaTime;
        if (l != null) l.intensity = start * Mathf.Clamp01(1f - age / life);
        if (age >= life) Destroy(gameObject);
    }
}

// =====================================================================
// Огненный шар
// =====================================================================
public class FireballShot : MonoBehaviour
{
    Vector3 vel;
    float power;
    bool authoritative;
    Transform shooterRoot;
    float born, nextTrail;
    Light glow;

    public static void Launch(Vector3 origin, Vector3 velocity, float power, bool authoritative, Transform shooterRoot)
    {
        float size = Mathf.Lerp(0.25f, 0.6f, power);
        GameObject g = Magic.Piece(PrimitiveType.Sphere, origin, Quaternion.identity, Vector3.one * size, Magic.Glow(new Color(1f, 0.5f, 0.12f), 5f));
        g.name = "Fireball";
        Magic.Piece(PrimitiveType.Sphere, origin, Quaternion.identity, Vector3.one * 0.55f, Magic.Glow(new Color(1f, 0.95f, 0.6f), 6f), g.transform).transform.localPosition = Vector3.zero;
        FireballShot f = g.AddComponent<FireballShot>();
        f.vel = velocity; f.power = power; f.authoritative = authoritative; f.shooterRoot = shooterRoot; f.born = Time.time;
        f.glow = g.AddComponent<Light>();
        f.glow.color = new Color(1f, 0.55f, 0.2f);
        f.glow.range = 6f + 6f * power;
        f.glow.intensity = 3f;
        f.glow.shadows = LightShadows.None;
    }

    void Update()
    {
        float dt = Time.deltaTime;
        if (Time.time - born > 6f) { Explode(transform.position, Vector3.up); return; }
        Vector3 from = transform.position;
        vel += Vector3.down * 3f * dt;                 // чуть падает — тяжёлый огонь
        Vector3 to = from + vel * dt;
        if (Magic.Trace(from, to, shooterRoot, out RaycastHit h, out BodyPart _)) { Explode(h.point, h.normal); return; }
        transform.position = to;
        transform.Rotate(Random.insideUnitSphere * 600f * dt);
        if (Time.time >= nextTrail)
        {
            nextTrail = Time.time + 0.025f;
            CombatFx.Flame(from + Random.insideUnitSphere * 0.15f, 0.8f + power);
            if (Random.value < 0.3f) CombatFx.Smoke(from, 0.4f);
        }
        if (glow != null) glow.intensity = 2.5f + Mathf.PerlinNoise(Time.time * 20f, 0f) * 2f;
    }

    void Explode(Vector3 at, Vector3 normal)
    {
        float radius = Mathf.Lerp(2.2f, 4.5f, power);
        float damage = Mathf.Lerp(14f, 42f, power);
        Vector3 c = at + normal * 0.3f;

        // картинка и звук — у всех
        for (int i = 0; i < 14 + 16 * power; i++) CombatFx.Flame(c + Random.insideUnitSphere * radius * 0.35f, 2.5f + 2f * power);
        for (int i = 0; i < 6; i++) CombatFx.Smoke(c + Random.insideUnitSphere * radius * 0.3f, 1.5f + power);
        CombatFx.Sparks(c, new Color(1f, 0.6f, 0.2f), 12);
        Magic.Flash(c, new Color(1f, 0.55f, 0.2f), 8f, radius * 4f, 0.35f);
        CombatFx.Play(CombatFx.Snd.Boom, c, 1f, Mathf.Lerp(1.15f, 0.85f, power));
        if (Magic.NearLocalPlayer(c, radius * 5f)) PlayerCamera.Shake(Mathf.Lerp(0.2f, 0.5f, power));

        // поджигаем тварей рядом (видно у всех)
        foreach (Enemy e in Enemy.All)
        {
            if (e == null) continue;
            Vector3 ep = e.transform.position + Vector3.up;
            float d = Vector3.Distance(ep, c);
            if (d > radius) continue;
            e.ShowBurn(power > 0.8f ? 4f : 2.5f);
            Vector3 dir = ep - c; dir.y = 0f;
            dir = dir.sqrMagnitude > 0.01f ? dir.normalized : Vector3.forward;
            if (authoritative)
            {
                float k = 1f - 0.6f * d / radius;
                EnemyManager.RequestHit(e, new HitInfo { damage = damage * k, dir = dir, part = BodyPartType.Torso, type = HitType.Fire, heavy = power > 0.8f });
            }
            e.LocalReact((int)BodyPartType.Torso, dir, 1.2f);
        }

        // разбрасывает обломки и ресурсы
        foreach (Collider col in Physics.OverlapSphere(c, radius, ~(1 << 2), QueryTriggerInteraction.Ignore))
        {
            Rigidbody rb = col.attachedRigidbody;
            if (rb != null && !rb.isKinematic) rb.AddExplosionForce(400f + 600f * power, c, radius, 0.6f);
        }

        // слишком близко — и тебя обожжёт
        if (authoritative && Magic.NearLocalPlayer(c, radius * 0.5f))
        {
            PlayerHealth.Local.TakeDamage(Mathf.Lerp(4f, 12f, power), "Сжёг себя", false);
            PlayerMovement pm = PlayerHealth.Local.GetComponent<PlayerMovement>();
            Vector3 away = PlayerHealth.Local.transform.position - c; away.y = 0f;
            if (pm != null) pm.Push(away.normalized * 7f, 0.3f);
        }
        Destroy(gameObject);
    }
}

// =====================================================================
// Молния
// =====================================================================
public class LightningStrike : MonoBehaviour
{
    Vector3 point;
    float power;
    bool authoritative;
    float start;
    bool struck;
    readonly List<LineRenderer> lines = new List<LineRenderer>();
    GameObject mark;

    public const float Delay = 0.22f;
    public const float Radius = 2.6f;

    public static void Begin(Vector3 at, float power, bool authoritative)
    {
        GameObject g = new GameObject("Lightning");
        g.transform.position = at;
        LightningStrike l = g.AddComponent<LightningStrike>();
        l.point = at; l.power = power; l.authoritative = authoritative; l.start = Time.time;
        // предупреждение: светится круг на земле, трещит
        l.mark = Magic.Piece(PrimitiveType.Cylinder, at + Vector3.up * 0.03f, Quaternion.identity, new Vector3(Radius * 2f, 0.01f, Radius * 2f), Magic.Glow(new Color(0.55f, 0.6f, 1f), 2f));
        CombatFx.Play(CombatFx.Snd.Hum, at, 0.5f, 1.6f);
    }

    void Update()
    {
        float t = Time.time - start;
        if (!struck && t >= Delay) Strike();
        if (struck)
        {
            // молния мерцает и гаснет
            bool on = Mathf.Sin(t * 90f) > -0.3f && t < Delay + 0.3f;
            foreach (LineRenderer lr in lines) if (lr != null) lr.enabled = on;
            if (t > Delay + 0.35f) { if (mark != null) Destroy(mark); Destroy(gameObject); }
        }
    }

    void Strike()
    {
        struck = true;
        Bolt(point + Vector3.up * 40f + Random.insideUnitSphere * 4f, point, 0.18f);
        Bolt(point + Vector3.up * 25f + Random.insideUnitSphere * 6f, point, 0.08f);
        Magic.Flash(point + Vector3.up * 2f, new Color(0.7f, 0.75f, 1f), 12f, 30f, 0.3f);
        CombatFx.Sparks(point, new Color(0.7f, 0.8f, 1f), 16);
        for (int i = 0; i < 4; i++) CombatFx.Smoke(point + Random.insideUnitSphere * 0.6f, 0.8f);
        CombatFx.Play(CombatFx.Snd.Zap, point, 1f);
        if (Magic.NearLocalPlayer(point, 25f)) PlayerCamera.Shake(0.35f);
        if (mark != null) mark.transform.localScale = new Vector3(Radius * 2.4f, 0.01f, Radius * 2.4f);

        // бьёт тварей в круге и перескакивает на соседей
        List<Enemy> hit = new List<Enemy>();
        foreach (Enemy e in Enemy.All)
            if (e != null && Vector3.Distance(Flat(e.transform.position), Flat(point)) < Radius) hit.Add(e);
        int chains = 0;
        foreach (Enemy e in hit.ToArray())
        {
            Damage(e, 1f);
            if (chains >= 2) continue;
            Enemy next = Nearest(e.transform.position, 6f, hit);
            if (next != null)
            {
                hit.Add(next);
                chains++;
                Bolt(e.transform.position + Vector3.up * 1.2f, next.transform.position + Vector3.up * 1.2f, 0.06f);
                Damage(next, 0.5f);
            }
        }
    }

    void Damage(Enemy e, float k)
    {
        Vector3 dir = e.transform.position - point; dir.y = 0f;
        dir = dir.sqrMagnitude > 0.01f ? dir.normalized : Vector3.forward;
        if (authoritative)
            EnemyManager.RequestHit(e, new HitInfo { damage = Mathf.Lerp(28f, 40f, power) * k, dir = dir, part = BodyPartType.Torso, type = HitType.Lightning });
        e.LocalReact((int)BodyPartType.Torso, Vector3.up + dir, 1.3f);
        CombatFx.Sparks(e.transform.position + Vector3.up * 1.2f, new Color(0.7f, 0.8f, 1f), 8);
    }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    static Enemy Nearest(Vector3 from, float max, List<Enemy> skip)
    {
        Enemy best = null; float bd = max;
        foreach (Enemy e in Enemy.All)
        {
            if (e == null || skip.Contains(e)) continue;
            float d = Vector3.Distance(e.transform.position, from);
            if (d < bd) { bd = d; best = e; }
        }
        return best;
    }

    // Ломаная линия — разряд
    void Bolt(Vector3 from, Vector3 to, float width)
    {
        GameObject g = new GameObject("Bolt");
        g.transform.SetParent(transform, false);
        LineRenderer lr = g.AddComponent<LineRenderer>();
        int n = 14;
        lr.positionCount = n;
        lr.useWorldSpace = true;
        lr.startWidth = width; lr.endWidth = width * 0.5f;
        lr.material = Magic.Glow(new Color(0.8f, 0.85f, 1f), 6f);
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        float jag = Vector3.Distance(from, to) * 0.04f;
        for (int i = 0; i < n; i++)
        {
            float k = i / (n - 1f);
            Vector3 p = Vector3.Lerp(from, to, k);
            if (i > 0 && i < n - 1) p += Random.insideUnitSphere * jag;
            lr.SetPosition(i, p);
        }
        lines.Add(lr);
    }
}

// =====================================================================
// Луч бластера — квадратный пучок света
// =====================================================================
public class BlasterBolt : MonoBehaviour
{
    Vector3 vel;
    bool authoritative;
    Transform shooterRoot;
    float born;

    static readonly Color BoltColor = new Color(0.4f, 0.95f, 1f);

    public static void Launch(Vector3 origin, Vector3 velocity, bool authoritative, Transform shooterRoot)
    {
        Quaternion rot = Quaternion.LookRotation(velocity.normalized) * Quaternion.Euler(0f, 0f, 45f);
        GameObject g = Magic.Piece(PrimitiveType.Cube, origin, rot, new Vector3(0.09f, 0.09f, 0.7f), Magic.Glow(BoltColor, 7f));
        g.name = "BlasterBolt";
        BlasterBolt b = g.AddComponent<BlasterBolt>();
        b.vel = velocity; b.authoritative = authoritative; b.shooterRoot = shooterRoot; b.born = Time.time;
    }

    void Update()
    {
        if (Time.time - born > 3f) { Destroy(gameObject); return; }
        Vector3 from = transform.position;
        Vector3 to = from + vel * Time.deltaTime;
        if (Magic.Trace(from, to, shooterRoot, out RaycastHit h, out BodyPart bp))
        {
            if (bp != null) HitBody(bp, h);
            else HitSurface(h);
            Destroy(gameObject);
            return;
        }
        transform.position = to;
    }

    void HitBody(BodyPart bp, RaycastHit h)
    {
        Vector3 dir = vel.normalized;
        Enemy e = bp.owner;
        if (authoritative)
        {
            EnemyManager.RequestHit(e, new HitInfo { damage = 11f, dir = dir, part = bp.type, type = HitType.Blaster });
            if (PlayerBlaster.Local != null) PlayerBlaster.Local.HitMarker(bp.type);
        }
        e.LocalReact((int)bp.type, dir, 0.95f);
        CombatFx.Play(CombatFx.Snd.Sizzle, h.point, 0.8f);
        CombatFx.Ichor(h.point, -dir, 3);
        for (int i = 0; i < 3; i++) CombatFx.Smoke(h.point, 0.25f);
        Magic.Flash(h.point - dir * 0.2f, BoltColor, 2.5f, 3f, 0.12f);

        // выжженная дыра: тёмный квадрат с раскалённым краем, остаётся на теле
        Quaternion rot = Quaternion.LookRotation(h.normal.sqrMagnitude > 0.01f ? -h.normal : -dir) * Quaternion.Euler(0f, 0f, Random.Range(0f, 90f));
        GameObject hole = new GameObject("BurnHole");
        hole.transform.SetParent(bp.transform, true);
        hole.transform.SetPositionAndRotation(h.point - h.normal * 0.005f, rot);
        Magic.Piece(PrimitiveType.Cube, hole.transform.position, rot, new Vector3(0.09f, 0.09f, 0.04f), Magic.Glow(new Color(0.03f, 0.02f, 0.02f), 0f), hole.transform);
        GameObject rim = Magic.Piece(PrimitiveType.Cube, hole.transform.position + h.normal * -0.01f, rot, new Vector3(0.13f, 0.13f, 0.02f), Magic.Glow(new Color(1f, 0.45f, 0.1f), 4f), hole.transform);
        rim.AddComponent<Shrink>().Setup(Vector3.zero, 2.5f);   // раскалённый край остывает
    }

    void HitSurface(RaycastHit h)
    {
        Rigidbody rb = h.collider.attachedRigidbody;
        if (rb != null && !rb.isKinematic) rb.AddForceAtPosition(vel.normalized * 3f, h.point, ForceMode.Impulse);
        CombatFx.Play(CombatFx.Snd.Sizzle, h.point, 0.6f, 1.3f);
        Magic.Flash(h.point + h.normal * 0.3f, BoltColor, 3f, 4f, 0.15f);
        SurfaceSplash.Spawn(h.point, h.normal, h.collider.transform);
    }
}

// Свет растекается по твёрдой поверхности: квадрат вспыхивает и расползается, остаётся подпалина
public class SurfaceSplash : MonoBehaviour
{
    Vector3 normal;
    float age;
    Transform core;
    readonly List<Transform> drops = new List<Transform>();
    readonly List<Vector3> dropVel = new List<Vector3>();

    public static void Spawn(Vector3 at, Vector3 normal, Transform surface)
    {
        GameObject g = new GameObject("BlasterSplash");
        g.transform.position = at + normal * 0.01f;
        g.transform.rotation = Quaternion.LookRotation(normal) * Quaternion.Euler(0f, 0f, Random.Range(0f, 90f));
        SurfaceSplash s = g.AddComponent<SurfaceSplash>();
        s.normal = normal;
        s.core = Magic.Piece(PrimitiveType.Cube, g.transform.position, g.transform.rotation, new Vector3(0.1f, 0.1f, 0.004f), Magic.Glow(new Color(0.4f, 0.95f, 1f), 6f), g.transform).transform;

        // капли света ползут по поверхности в стороны
        Vector3 t1 = Vector3.Cross(normal, Mathf.Abs(normal.y) > 0.9f ? Vector3.right : Vector3.up).normalized;
        Vector3 t2 = Vector3.Cross(normal, t1);
        int n = Random.Range(5, 8);
        for (int i = 0; i < n; i++)
        {
            float a = (i / (float)n + Random.Range(-0.08f, 0.08f)) * Mathf.PI * 2f;
            Vector3 dir = t1 * Mathf.Cos(a) + t2 * Mathf.Sin(a);
            float sz = Random.Range(0.04f, 0.08f);
            Transform d = Magic.Piece(PrimitiveType.Cube, g.transform.position, g.transform.rotation, new Vector3(sz, sz, 0.004f), Magic.Glow(new Color(0.4f, 0.95f, 1f), 5f), g.transform).transform;
            s.drops.Add(d);
            s.dropVel.Add(dir * Random.Range(0.8f, 2f) + (Vector3.Dot(Vector3.down, normal) < 0.9f ? Vector3.ProjectOnPlane(Vector3.down, normal) * 0.6f : Vector3.zero));
        }

        // подпалина остаётся надолго
        GameObject scorch = Magic.Piece(PrimitiveType.Cube, at + normal * 0.006f, g.transform.rotation, new Vector3(0.28f, 0.28f, 0.003f), Magic.Glow(new Color(0.05f, 0.04f, 0.04f), 0f));
        if (surface != null) scorch.transform.SetParent(surface, true);
        Object.Destroy(scorch, 12f);
    }

    void Update()
    {
        age += Time.deltaTime;
        float k = age / 0.5f;
        if (k >= 1f) { Destroy(gameObject); return; }
        // центр вспыхивает и растёт, потом гаснет
        float size = Mathf.Lerp(0.1f, 0.55f, Mathf.Sqrt(Mathf.Clamp01(age / 0.15f))) * (1f - k * k);
        if (core != null) core.localScale = new Vector3(size, size, 0.004f);
        for (int i = 0; i < drops.Count; i++)
        {
            if (drops[i] == null) continue;
            drops[i].position += dropVel[i] * Time.deltaTime;
            dropVel[i] *= 1f - 3f * Time.deltaTime;
            float s = drops[i].localScale.x * (1f - 1.6f * Time.deltaTime);
            drops[i].localScale = new Vector3(s, s, 0.004f);
        }
    }
}
