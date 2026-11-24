using UnityEngine;

// Стрела. Летит сама (гравитация + сопротивление воздуха), нос всегда смотрит по полёту.
// Попала в тварь — входит в ту часть тела, куда попала, и торчит из неё, покачиваясь.
// Попала в стену или землю — втыкается (если летела быстро и не вскользь), иначе отскакивает.
// Свои воткнутые стрелы можно подобрать — просто подойди.
public class Arrow : MonoBehaviour
{
    const float Gravity = 9.81f;
    const float Drag = 0.012f;        // сопротивление воздуха
    const float Length = 0.8f;

    Vector3 velocity;
    bool authoritative;               // эта стрела решает урон (у того, кто стрелял, или у хоста)
    bool heavy;
    Transform shooterRoot;
    bool mine;                        // моя стрела — могу подобрать
    bool stuck;
    bool collectable;
    float bornAt, stuckAt;
    float wobble;                     // покачивание после попадания
    Transform model;

    public static Arrow Spawn(Vector3 origin, Vector3 vel, bool authoritative, bool heavy, Transform shooterRoot, bool mine)
    {
        GameObject go = new GameObject("Arrow");
        go.transform.position = origin;
        go.transform.rotation = Quaternion.LookRotation(vel.normalized);
        Arrow a = go.AddComponent<Arrow>();
        a.velocity = vel;
        a.authoritative = authoritative;
        a.heavy = heavy;
        a.shooterRoot = shooterRoot;
        a.mine = mine;
        a.bornAt = Time.time;
        a.model = BuildModel(go.transform, heavy);
        return a;
    }

    // Модель стрелы: древко, наконечник, оперение. Без коллайдеров (слой 2 — лучи её не видят)
    public static Transform BuildModel(Transform parent, bool heavy)
    {
        GameObject root = new GameObject("ArrowModel");
        root.layer = 2;
        root.transform.SetParent(parent, false);
        // остриё — в точке (0,0,0), древко тянется назад по -z
        Part(root.transform, new Vector3(0f, 0f, -Length * 0.5f), new Vector3(0.018f, 0.018f, Length), new Color(0.55f, 0.4f, 0.25f));
        Part(root.transform, new Vector3(0f, 0f, -0.03f), new Vector3(0.035f, 0.035f, 0.07f), heavy ? new Color(0.9f, 0.75f, 0.35f) : new Color(0.6f, 0.62f, 0.65f));
        Part(root.transform, new Vector3(0f, 0.02f, -Length + 0.07f), new Vector3(0.004f, 0.04f, 0.12f), new Color(0.85f, 0.2f, 0.2f));
        Part(root.transform, new Vector3(0.02f, 0f, -Length + 0.07f), new Vector3(0.04f, 0.004f, 0.12f), new Color(0.95f, 0.95f, 0.9f));
        return root.transform;
    }

    static void Part(Transform parent, Vector3 pos, Vector3 scale, Color c)
    {
        GameObject p = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Object.Destroy(p.GetComponent<Collider>());
        p.layer = 2;
        p.transform.SetParent(parent, false);
        p.transform.localPosition = pos;
        p.transform.localScale = scale;
        Renderer r = p.GetComponent<Renderer>();
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.material.color = c;
    }

    void Update()
    {
        if (stuck) { UpdateStuck(); return; }
        if (Time.time - bornAt > 8f) { Destroy(gameObject); return; }

        float dt = Time.deltaTime;
        if (dt <= 0f) return;
        Vector3 from = transform.position;
        velocity += Vector3.down * Gravity * dt;
        velocity -= velocity * velocity.magnitude * Drag * dt;
        Vector3 to = from + velocity * dt;

        if (Fly(from, to)) return;
        transform.position = to;
        if (velocity.sqrMagnitude > 0.01f) transform.rotation = Quaternion.LookRotation(velocity.normalized);
    }

    // true — во что-то попала
    bool Fly(Vector3 from, Vector3 to)
    {
        Vector3 d = to - from;
        float len = d.magnitude;
        if (len < 0.0001f) return false;
        RaycastHit[] hits = Physics.RaycastAll(from, d / len, len, ~(1 << 2), QueryTriggerInteraction.Collide);
        System.Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
        foreach (RaycastHit h in hits)
        {
            Transform root = h.transform.root;
            if (shooterRoot != null && root == shooterRoot) continue;
            BodyPart bp = h.collider.GetComponentInParent<BodyPart>();
            if (bp != null)
            {
                if (!bp.enabled || bp.lost || bp.owner == null) continue;
                HitEnemy(bp, h);
                return true;
            }
            if (h.collider.isTrigger) continue;
            if (h.collider.GetComponentInParent<Enemy>() != null) continue;     // капсула твари — ждём часть тела
            if (h.collider.GetComponentInParent<PlayerMovement>() != null || h.collider.GetComponentInParent<NetworkPlayer>() != null) continue;
            HitWorld(h);
            return true;
        }
        return false;
    }

    void HitEnemy(BodyPart bp, RaycastHit h)
    {
        float speed = velocity.magnitude;
        Vector3 dir = velocity / Mathf.Max(0.01f, speed);
        Enemy e = bp.owner;
        if (authoritative)
        {
            EnemyManager.RequestHit(e, new HitInfo { damage = speed * 0.9f * (heavy ? 1.25f : 1f), dir = dir, part = bp.type, type = HitType.Arrow, heavy = heavy });
            if (mine) PlayerBow.HitMarker(bp.type, heavy);
        }
        e.LocalReact((int)bp.type, dir, heavy ? 1.4f : 1.1f);
        CombatFx.Ichor(h.point, -dir, heavy ? 10 : 5);
        CombatFx.Play(CombatFx.Snd.Flesh, h.point, 0.8f, 1.3f);
        if (mine) CombatFx.Hitstop(heavy ? 0.05f : 0.02f);

        // входит в тело: чем быстрее летела, тем глубже
        float depth = Mathf.Clamp(speed / 80f, 0.1f, 0.35f);
        StickTo(bp.transform, h.point + dir * depth, dir, false);
    }

    void HitWorld(RaycastHit h)
    {
        float speed = velocity.magnitude;
        Vector3 dir = velocity / Mathf.Max(0.01f, speed);
        float along = Vector3.Dot(dir, -h.normal);   // 1 — прямо в стену, 0 — вскользь

        Rigidbody body = h.collider.attachedRigidbody;
        if (body != null && !body.isKinematic) body.AddForceAtPosition(velocity * 0.02f, h.point, ForceMode.Impulse);

        CombatFx.Sparks(h.point, new Color(0.8f, 0.7f, 0.5f), 3);
        CombatFx.Play(CombatFx.Snd.Thunk, h.point, 0.7f);

        if (speed < 9f || along < 0.3f)
        {
            // отскок: теряет скорость и кувыркается дальше
            velocity = Vector3.Reflect(velocity, h.normal) * 0.3f + Random.insideUnitSphere * 1.5f;
            transform.position = h.point + h.normal * 0.05f;
            if (velocity.sqrMagnitude > 0.01f) transform.rotation = Quaternion.LookRotation(velocity.normalized);
            if (velocity.magnitude < 2f) StickTo(h.collider.transform, h.point + h.normal * 0.02f, Vector3.Cross(h.normal, Random.onUnitSphere).normalized, true);
            return;
        }
        float depth = Mathf.Clamp(speed / 120f, 0.05f, 0.25f) * along;
        StickTo(h.collider.transform, h.point + dir * depth, dir, true);
    }

    void StickTo(Transform target, Vector3 tip, Vector3 dir, bool world)
    {
        stuck = true;
        stuckAt = Time.time;
        collectable = world && mine;
        velocity = Vector3.zero;
        transform.position = tip;
        transform.rotation = Quaternion.LookRotation(dir);
        // не цепляемся к объекту с неровным масштабом напрямую — через пустышку, чтобы стрела не сплющилась
        GameObject anchor = new GameObject("ArrowAnchor");
        anchor.transform.SetParent(target, true);
        anchor.transform.position = tip;
        anchor.transform.rotation = transform.rotation;
        transform.SetParent(null, true);
        anchorT = anchor.transform;
        wobble = 1f;
    }

    Transform anchorT;

    void UpdateStuck()
    {
        float life = collectable ? 25f : 12f;
        if (anchorT == null || Time.time - stuckAt > life) { if (anchorT != null) Destroy(anchorT.gameObject); Destroy(gameObject); return; }
        // следуем за тем, во что воткнулись (часть тела двигается, отрубается и т.д.)
        wobble = Mathf.MoveTowards(wobble, 0f, Time.deltaTime * 1.6f);
        float t = (Time.time - stuckAt) * 28f;
        Quaternion shake = Quaternion.Euler(Mathf.Sin(t) * 7f * wobble, Mathf.Cos(t * 0.8f) * 5f * wobble, 0f);
        transform.SetPositionAndRotation(anchorT.position, anchorT.rotation * shake);

        if (collectable && Time.time - stuckAt > 1f)
        {
            Transform p = PlayerBow.Local != null ? PlayerBow.Local.transform : null;
            if (p != null && Vector3.Distance(p.position + Vector3.up * 0.9f, transform.position) < 2.2f)
            {
                Inventory inv = p.GetComponent<Inventory>();
                if (inv != null) inv.Add(ResourceType.Arrow, 1);
                Destroy(anchorT.gameObject);
                Destroy(gameObject);
            }
        }
    }

    void OnDestroy()
    {
        if (anchorT != null) Destroy(anchorT.gameObject);
    }
}
