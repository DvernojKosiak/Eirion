using UnityEngine;
using UnityEngine.InputSystem;

// Посох с волшебным камнем (клавиша 6). Тратит «силу камня» (фиолетовая полоска), она восстанавливается сама.
//   ЛКМ зажать — посох отводится назад, в камне растёт огонь. Отпустить — огненный шар. Чем дольше держал, тем он больше.
//   ПКМ зажать — целишься молнией: на земле светится круг. Отпустить — с неба бьёт молния, перескакивает на соседних тварей.
//   F зажать, глядя на тварь, — поднимаешь её телекинезом. Двигай взглядом, колёсико — ближе/дальше.
//       Резко поведи и отпусти F — швырнёшь (об стену, на колья, в пропасть). ЛКМ — отшвырнуть вперёд.
//       Тварь сопротивляется: дёргается и вырывается. Громила тяжёлый — почти не отрывается от земли.
public class PlayerStaff : MonoBehaviour
{
    public static PlayerStaff Local { get; private set; }
    public static int LocalTkId { get; private set; } = -1;   // какую тварь я держу телекинезом
    public static Vector3 LocalTkPoint { get; private set; }

    [Header("Сила камня")]
    public float maxMana = 100f;
    public float manaRegen = 16f;
    public float regenDelay = 0.7f;

    [Header("Огненный шар")]
    public float chargeTime = 1.1f;
    public float fireMinCost = 10f, fireMaxCost = 32f;

    [Header("Молния")]
    public float lightningCost = 30f;
    public float lightningRange = 45f;
    public float lightningCooldown = 1f;

    [Header("Телекинез")]
    public float tkRange = 20f;
    public float tkCost = 13f;          // в секунду
    public float tkFlingCost = 10f;

    public float Mana { get; private set; }

    PlayerHotbar hotbar;
    PlayerCombat combat;
    Camera cam;
    Transform staff, stone;
    float aimStart;
    Renderer stoneRenderer;
    MaterialPropertyBlock block;
    Vector3 restPos;
    Quaternion restRot;
    Transform fireOrb;
    LineRenderer tkBeam;
    GameObject aimMark;

    float lastSpend = -100f;
    bool charging;
    float chargeStart, charge;
    bool aiming;
    float nextLightning;
    float castKick;
    string message;
    float messageUntil;

    // телекинез
    bool tkWaiting, tkHolding;
    int tkId = -1;
    Enemy tkEnemy;
    Rigidbody tkBody;
    float tkDist, tkStart, tkSend;
    Vector3 tkPoint, tkPrev, tkVel;

    // красота: камень парит и вращается, вокруг кружат искры, светит
    Vector3 stoneBase;
    Light stoneLight;
    Transform[] motes;

    void OnEnable() { Local = this; EnemyManager.TkResult += OnTkResult; }
    void OnDisable()
    {
        if (Local == this) Local = null;
        EnemyManager.TkResult -= OnTkResult;
        EndTk(Vector3.zero);
        if (aimMark != null) aimMark.SetActive(false);
    }

    void Start()
    {
        hotbar = GetComponent<PlayerHotbar>();
        combat = GetComponent<PlayerCombat>();
        cam = Camera.main;
        Mana = maxMana;
        block = new MaterialPropertyBlock();
        if (hotbar != null && hotbar.staffModel != null)
        {
            staff = hotbar.staffModel;
            restPos = staff.localPosition;
            restRot = staff.localRotation;
            stone = staff.Find("Stone");
            if (stone != null)
            {
                stoneRenderer = stone.GetComponent<Renderer>();
                fireOrb = Magic.Piece(PrimitiveType.Sphere, stone.position, Quaternion.identity, Vector3.one * 0.01f, Magic.Glow(new Color(1f, 0.5f, 0.12f), 5f), staff).transform;
                fireOrb.localPosition = stone.localPosition + Vector3.up * 0.02f;
                fireOrb.gameObject.SetActive(false);
                stoneBase = stone.localPosition;
                GameObject lg = new GameObject("StoneLight");
                lg.transform.SetParent(stone, false);
                stoneLight = lg.AddComponent<Light>();
                stoneLight.type = LightType.Point;
                stoneLight.color = new Color(0.65f, 0.4f, 1f);
                stoneLight.range = 2.5f;
                stoneLight.intensity = 1f;
                stoneLight.shadows = LightShadows.None;
                motes = new Transform[5];
                for (int i = 0; i < motes.Length; i++)
                    motes[i] = Magic.Piece(PrimitiveType.Cube, stone.position, Quaternion.identity, Vector3.one * 0.015f, Magic.Glow(new Color(0.75f, 0.55f, 1f), 6f), staff).transform;
            }
            GameObject b = new GameObject("TkBeam");
            b.layer = 2;
            tkBeam = b.AddComponent<LineRenderer>();
            tkBeam.positionCount = 12;
            tkBeam.startWidth = 0.03f; tkBeam.endWidth = 0.1f;
            tkBeam.material = Magic.Glow(new Color(0.6f, 0.35f, 1f), 4f);
            tkBeam.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            tkBeam.enabled = false;
        }
        aimMark = Magic.Piece(PrimitiveType.Cylinder, Vector3.zero, Quaternion.identity, new Vector3(LightningStrike.Radius * 2f, 0.01f, LightningStrike.Radius * 2f), Magic.Glow(new Color(0.55f, 0.4f, 1f), 2.5f));
        aimMark.name = "LightningAim";
        aimMark.SetActive(false);
    }

    bool Selected => hotbar != null && hotbar.Current == Tool.Staff;

    bool Spend(float amount)
    {
        if (Mana < amount) { Say("Камень пуст — подожди, сила вернётся"); return false; }
        Mana -= amount;
        lastSpend = Time.time;
        return true;
    }

    void Say(string text) { message = text; messageUntil = Time.time + 1.4f; }

    void Update()
    {
        if (Time.time - lastSpend > regenDelay && !tkHolding) Mana = Mathf.Min(maxMana, Mana + manaRegen * Time.deltaTime);
        if (staff == null) return;
        if (!Selected)
        {
            charging = false; aiming = false;
            if (tkHolding || tkWaiting) EndTk(Vector3.zero);
            if (aimMark != null) aimMark.SetActive(false);
            if (fireOrb != null) fireOrb.gameObject.SetActive(false);
            staff.localPosition = restPos; staff.localRotation = restRot;
            return;
        }

        Mouse mouse = Mouse.current;
        Keyboard kb = Keyboard.current;
        bool locked = Cursor.lockState == CursorLockMode.Locked;
        bool can = mouse != null && kb != null && locked && cam != null && (combat == null || (!combat.IsHolding && combat.BashP < 0f));

        if (!can) { charging = false; aiming = false; }
        else
        {
            // --- Телекинез ---
            if (kb.fKey.wasPressedThisFrame && !tkHolding && !tkWaiting) StartTk();
            if (tkHolding) UpdateTk(mouse, kb);
            else if (tkWaiting && !kb.fKey.isPressed) tkWaiting = false;

            if (!tkHolding)
            {
                // --- Огненный шар ---
                if (mouse.leftButton.wasPressedThisFrame && !aiming) { charging = true; chargeStart = Time.time; }
                if (charging)
                {
                    charge = Mathf.Clamp01((Time.time - chargeStart) / chargeTime);
                    if (!mouse.leftButton.isPressed) { charging = false; CastFireball(); }
                }

                // --- Молния ---
                if (mouse.rightButton.wasPressedThisFrame && !charging) { aiming = true; aimStart = Time.time; }
                if (aiming)
                {
                    bool has = AimGround(out Vector3 p);
                    aimMark.SetActive(has);
                    if (has) aimMark.transform.position = p + Vector3.up * 0.04f;
                    if (!mouse.rightButton.isPressed)
                    {
                        aiming = false;
                        aimMark.SetActive(false);
                        if (has && Time.time - aimStart >= 0.2f) CastLightning(p);   // короткое нажатие — это удар посохом, не молния
                    }
                }
            }
        }
        if (!charging) charge = Mathf.MoveTowards(charge, 0f, Time.deltaTime * 4f);
        castKick = Mathf.MoveTowards(castKick, 0f, Time.deltaTime * 4f);
        Pose();
    }

    // ---------------- Огненный шар ----------------
    void CastFireball()
    {
        float power = Mathf.Clamp01((charge - 0.1f) / 0.9f);
        if (!Spend(Mathf.Lerp(fireMinCost, fireMaxCost, power))) return;
        Vector3 origin = cam.transform.position + cam.transform.forward * 0.9f + cam.transform.right * 0.12f;
        Vector3 aim = AimPoint(80f);
        Vector3 dir = (aim - origin).normalized;
        EnemyManager.CastSpell(Magic.Fireball, origin, dir * Mathf.Lerp(20f, 34f, power), power, transform.root);
        CombatFx.Play(CombatFx.Snd.Whoosh, origin, 0.8f, 0.6f);
        PlayerCamera.Punch(new Vector3(-1.5f - 2f * power, 0f, 0f));
        castKick = 1f;
        charge = 0f;
    }

    // ---------------- Молния ----------------
    bool AimGround(out Vector3 p)
    {
        p = Vector3.zero;
        Ray r = new Ray(cam.transform.position, cam.transform.forward);
        foreach (RaycastHit h in SortedHits(r, lightningRange))
        {
            if (h.transform.root == transform.root || h.collider.isTrigger) continue;
            // под тварью — бьём в землю под ней
            if (h.collider.GetComponentInParent<Enemy>() != null)
            {
                Vector3 at = h.collider.GetComponentInParent<Enemy>().transform.position;
                p = at; return true;
            }
            p = h.point; return true;
        }
        return false;
    }

    void CastLightning(Vector3 p)
    {
        if (Time.time < nextLightning) { Say("Камень ещё не готов"); return; }
        if (!Spend(lightningCost)) return;
        nextLightning = Time.time + lightningCooldown;
        EnemyManager.CastSpell(Magic.Lightning, p, Vector3.zero, 1f, transform.root);
        castKick = 1f;
        PlayerCamera.Punch(new Vector3(2f, 0f, 0f));
    }

    // ---------------- Телекинез ----------------
    void StartTk()
    {
        if (Mana < tkCost * 0.4f) { Say("Камень пуст — подожди, сила вернётся"); return; }
        Ray r = new Ray(cam.transform.position, cam.transform.forward);
        foreach (RaycastHit h in SortedHits(r, tkRange))
        {
            if (h.transform.root == transform.root) continue;
            Enemy e = h.collider.GetComponentInParent<Enemy>();
            if (e != null)
            {
                if (e.HealthFraction <= 0f) continue;
                tkEnemy = e;
                tkDist = Mathf.Clamp(Vector3.Distance(cam.transform.position, e.transform.position + Vector3.up), 3f, 9f);
                tkWaiting = true;
                EnemyManager.RequestTk(e);
                return;
            }
            if (h.collider.isTrigger) continue;
            Rigidbody rb = h.collider.attachedRigidbody;
            if (rb != null && !rb.isKinematic)
            {
                // обломки и ресурсы двигаем прямо у себя
                tkBody = rb;
                tkDist = Mathf.Clamp(h.distance, 2.5f, 9f);
                BeginHold();
                return;
            }
            break;
        }
        Say("Наведи посох на тварь или предмет");
    }

    void OnTkResult(int id, bool ok)
    {
        if (!tkWaiting) return;
        tkWaiting = false;
        if (!ok || tkEnemy == null || tkEnemy.id != id) { Say("Не вышло поднять"); tkEnemy = null; return; }
        Keyboard kb = Keyboard.current;
        tkId = id;
        BeginHold();
        if (kb != null && !kb.fKey.isPressed) EndTk(Vector3.zero); // уже отпустил — просто бросаем
    }

    void BeginHold()
    {
        tkHolding = true;
        tkStart = Time.time;
        tkPoint = tkPrev = cam.transform.position + cam.transform.forward * tkDist;
        tkVel = Vector3.zero;
        LocalTkId = tkId;
        LocalTkPoint = tkPoint;
        CombatFx.Play(CombatFx.Snd.Hum, tkPoint, 0.7f, 0.8f);
    }

    void UpdateTk(Mouse mouse, Keyboard kb)
    {
        float dt = Mathf.Max(0.0001f, Time.deltaTime);
        float scroll = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) > 0.01f) tkDist = Mathf.Clamp(tkDist + Mathf.Sign(scroll) * 0.6f, 2.5f, 10f);
        tkPoint = cam.transform.position + cam.transform.forward * tkDist;
        tkVel = Vector3.Lerp(tkVel, (tkPoint - tkPrev) / dt, 1f - Mathf.Exp(-12f * dt));
        tkPrev = tkPoint;
        LocalTkPoint = tkPoint;

        bool brute = tkEnemy != null && tkEnemy.kind == EnemyKind.Brute;
        Mana -= tkCost * (brute ? 1.6f : 1f) * dt;
        lastSpend = Time.time;

        // тварь вырвалась или умерла
        bool lost = tkBody == null && (tkEnemy == null || tkEnemy.HealthFraction <= 0f ||
                    (Time.time - tkStart > 0.4f && tkEnemy.CurrentState != Enemy.State.Grabbed));
        if (lost) { if (tkEnemy != null && tkEnemy.HealthFraction > 0f) Say("Вырвалась!"); EndTk(Vector3.zero, false); return; }
        if (Mana <= 0f) { Mana = 0f; Say("Сила камня кончилась"); EndTk(Vector3.down * 2f); return; }

        if (tkBody != null)
        {
            tkBody.linearVelocity = (tkPoint - tkBody.worldCenterOfMass) * 10f;
            tkBody.angularVelocity *= 0.9f;
        }
        else if (Time.time >= tkSend)
        {
            tkSend = Time.time + 0.05f;
            EnemyManager.TkMove(tkId, tkPoint);
        }

        if (mouse.leftButton.wasPressedThisFrame && Spend(tkFlingCost))
        {
            EndTk(cam.transform.forward * 20f + Vector3.up * 2f);
            PlayerCamera.Punch(new Vector3(-2f, 0f, 0f));
            castKick = 1f;
            return;
        }
        if (!kb.fKey.isPressed)
        {
            // швыряем туда, куда вели посох
            Vector3 v = Vector3.ClampMagnitude(tkVel * 1.2f, 22f);
            EndTk(v);
        }
    }

    void EndTk(Vector3 velocity, bool throwIt = true)
    {
        if (tkHolding && throwIt)
        {
            if (tkBody != null) tkBody.linearVelocity = velocity;
            else if (tkId >= 0) EnemyManager.RequestThrow(tkId, velocity, Mathf.Clamp(velocity.magnitude * 0.4f, 1f, 6f));
        }
        tkHolding = false;
        tkWaiting = false;
        tkId = -1;
        tkEnemy = null;
        tkBody = null;
        LocalTkId = -1;
        if (tkBeam != null) tkBeam.enabled = false;
    }

    // ---------------- Помощники ----------------
    static RaycastHit[] SortedHits(Ray r, float range)
    {
        RaycastHit[] hits = Physics.RaycastAll(r, range, ~(1 << 2), QueryTriggerInteraction.Collide);
        System.Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
        return hits;
    }

    Vector3 AimPoint(float range)
    {
        Ray r = new Ray(cam.transform.position, cam.transform.forward);
        foreach (RaycastHit h in SortedHits(r, range))
        {
            if (h.transform.root == transform.root) continue;
            if (h.collider.isTrigger && h.collider.GetComponentInParent<BodyPart>() == null) continue;
            return h.point;
        }
        return r.GetPoint(range);
    }

    void Pose()
    {
        float c = charge * charge * (3f - 2f * charge);
        Vector3 pos = restPos;
        Quaternion rot = restRot;
        // огненный шар: посох отводится назад над плечом — замах
        pos += new Vector3(0.05f, 0.12f, -0.18f) * c;
        rot *= Quaternion.Euler(38f * c, 0f, -10f * c);
        // молния: посох поднят вверх
        if (aiming) { pos += new Vector3(-0.05f, 0.22f, 0.05f); rot *= Quaternion.Euler(-28f, 0f, 0f); }
        // телекинез: посох вытянут вперёд
        if (tkHolding) { pos += new Vector3(-0.12f, 0.15f, 0.12f); rot *= Quaternion.Euler(-45f, 0f, 6f); }
        // выброс после заклинания
        pos += Vector3.forward * 0.12f * Mathf.Sin(castKick * Mathf.PI);
        rot *= Quaternion.Euler(-25f * castKick, 0f, 0f);
        if (charge >= 0.99f || tkHolding) pos += Random.insideUnitSphere * 0.003f;
        if (combat != null && combat.IsHolding)
        {
            // левая рука держит тварь — посох опущен в правой
            pos += new Vector3(0.08f, -0.22f, -0.12f);
            rot *= Quaternion.Euler(35f, 0f, -25f);
        }
        // ПКМ коротко — удар посохом: занёс навершием назад над плечом и обрушил вперёд-вниз, двумя руками
        if (combat != null) PlayerCombat.BashPose(combat.BashP, new Vector3(-0.05f, 0.3f, -0.2f), new Vector3(-45f, 0f, -10f),
                                                   new Vector3(-0.15f, -0.05f, 0.35f), new Vector3(85f, 0f, 10f), ref pos, ref rot);
        WeaponSway.Apply(ref pos, ref rot);

        // глаза идут за посохом: завёл за плечо — взгляд чуть назад-вверх и вбок, метнул — качнулся вперёд
        float kickS = Mathf.Sin(castKick * Mathf.PI);
        PlayerCamera.Sway(new Vector3(-6f * c + 4f * kickS, 1.5f * c, -2.5f * c) + (aiming ? new Vector3(-3f, 0f, 0f) : Vector3.zero));

        float k = 1f - Mathf.Exp(-14f * Time.deltaTime);
        staff.localPosition = Vector3.Lerp(staff.localPosition, pos, k);
        staff.localRotation = Quaternion.Slerp(staff.localRotation, rot, k);
        // держу тварь посохом за шею — древко поперёк, за её шеей
        if (combat != null && combat.PoleHold(staff, out Vector3 hp, out Quaternion hr)) { staff.position = hp; staff.rotation = hr; }

        // камень парит над когтями и медленно вращается, вокруг кружат искры
        float tm = Time.time;
        if (stone != null)
        {
            stone.localPosition = stoneBase + Vector3.up * (0.012f * Mathf.Sin(tm * 2.2f) + 0.02f * c);
            stone.localRotation = Quaternion.Euler(45f, tm * (40f + 260f * c), 45f);
            if (motes != null)
                for (int i = 0; i < motes.Length; i++)
                {
                    float a = tm * (1.6f + 3f * c) + i * Mathf.PI * 2f / motes.Length;
                    float r = 0.09f + 0.02f * Mathf.Sin(tm * 3f + i) - 0.03f * c;
                    motes[i].localPosition = stone.localPosition + new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a * 1.3f + i) * 0.05f, Mathf.Sin(a) * r);
                    motes[i].localScale = Vector3.one * (0.012f + 0.01f * c + 0.004f * Mathf.Sin(tm * 9f + i));
                }
        }
        if (stoneLight != null) stoneLight.intensity = 0.8f + 3f * c + (aiming || tkHolding ? 2f : 0f) + Mathf.PerlinNoise(tm * 5f, 0f) * 0.4f;

        // камень светится сильнее, когда копишь силу
        if (stoneRenderer != null)
        {
            float glow = 2f + 6f * c + (aiming || tkHolding ? 4f : 0f) + Mathf.Sin(Time.time * 6f) * 0.5f;
            stoneRenderer.GetPropertyBlock(block);
            block.SetColor("_EmissionColor", new Color(0.6f, 0.35f, 1f) * glow);
            stoneRenderer.SetPropertyBlock(block);
        }
        if (fireOrb != null)
        {
            fireOrb.gameObject.SetActive(charging);
            fireOrb.localScale = Vector3.one * Mathf.Lerp(0.04f, 0.3f, c) * (1f + Mathf.Sin(Time.time * 30f) * 0.08f);
        }

        // луч телекинеза: от камня к поднятому
        if (tkBeam != null)
        {
            tkBeam.enabled = tkHolding;
            if (tkHolding && stone != null)
            {
                Vector3 a = stone.position;
                Vector3 b = tkBody != null ? tkBody.worldCenterOfMass : (tkEnemy != null ? tkEnemy.transform.position + Vector3.up : tkPoint);
                for (int i = 0; i < tkBeam.positionCount; i++)
                {
                    float t = i / (tkBeam.positionCount - 1f);
                    Vector3 p = Vector3.Lerp(a, b, t);
                    p += Mathf.Sin(t * Mathf.PI) * (Vector3.up * Mathf.Sin(Time.time * 17f + t * 9f) + cam.transform.right * Mathf.Cos(Time.time * 13f + t * 7f)) * 0.12f;
                    tkBeam.SetPosition(i, p);
                }
            }
        }
    }

    void OnGUI()
    {
        // сила камня — всегда видна, если посох в руке или её не хватает
        if (Selected || Mana < maxMana)
        {
            Hud.Bar(new Rect(12, 136, 220, 8), Mana / maxMana, new Color(0.6f, 0.35f, 1f), null);
            GUI.Label(new Rect(16, 144, 300, 22), "Сила камня", Hud.Tiny);
        }
        if (!Selected) return;
        float cx = Screen.width / 2f, cy = Screen.height / 2f;

        if (charging)
        {
            float p = Mathf.Clamp01((charge - 0.1f) / 0.9f);
            Hud.Bar(new Rect(cx - 40, cy + 18, 80, 4), p, p >= 1f ? new Color(1f, 0.5f, 0.15f) : new Color(1f, 0.8f, 0.5f), null);
        }
        if (tkHolding && tkEnemy != null)
        {
            // сколько ещё удержу, пока не вырвется
            float hold = tkEnemy.kind == EnemyKind.Brute ? 1.3f : 2.8f;
            float left = Mathf.Clamp01(1f - (Time.time - tkStart) / hold);
            Hud.Bar(new Rect(cx - 60, cy + 30, 120, 5), left, new Color(0.65f, 0.4f, 1f), "вырывается");
        }

        GUI.Label(new Rect(0, Screen.height - 88, Screen.width, 22),
                  "ЛКМ держать — огненный шар (дольше — сильнее)   ПКМ держать — молния, отпустить — удар   F держать — телекинез (веди и отпусти — швырнёшь, колёсико — ближе/дальше)", Hud.CenterTiny);
        if (Hud.Popups && Time.time < messageUntil)
            GUI.Label(new Rect(cx - 300, cy - 70, 600, 30), message, Hud.CenterBig);
    }
}
