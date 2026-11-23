using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Бой. Меч — продолжение камеры.
//
//   ДЕРЖИ ЛКМ — меч выставлен вперёд и ходит вместе с камерой. Води мышью — рубишь.
//     Чем быстрее движение, тем сильнее удар (полоска под прицелом). Медленно — клинок просто упирается.
//     Попадает та часть тела, через которую прошёл клинок: голова, рука, нога, туловище.
//     Клинок ВХОДИТ в тело и остаётся там, пока держишь ЛКМ. Мышь тогда тяжёлая — ты держишь тварь.
//       води мышью     — пилишь глубже (руку или ногу можно так отпилить)
//       отпусти ЛКМ    — выдернуть клинок (а если резко повести мышь и отпустить — сбросишь тварь с клинка)
//       Q              — пнуть тварь с клинка
//       иди на стену   — тварь на клинке будет пригвождена
//     Очень быстрый удар по туловищу прорубает и отбрасывает тварь, а не застревает.
//     Разбег с мечом вперёд (Shift) — колющий удар.
//   Q — пинок. G — схватить тварь; пока держишь, видна дуга броска: ЛКМ или G — бросить, Q — через себя.
//   ПКМ — блок (держи врага в прицеле), Ctrl / C — перекат.
//   Топор и кирка рубят деревья и бьют камни по щелчку.
public class PlayerCombat : MonoBehaviour
{
    [Header("Клинок")]
    public float bladeLength = 2.2f;
    public float minHitSpeed = 4.5f;       // м/с кончика: медленнее — не удар
    public float heavySpeed = 13f;         // быстрее — сильный удар
    public float damagePerSpeed = 1.6f;
    public float maxSpeedForDamage = 26f;
    public float ripDamage = 15f;
    public float sawDamage = 7f;
    public float embeddedLook = 0.3f;      // послушность мыши, пока клинок в теле

    [Header("Остальное")]
    public float range = 2.8f;
    public float pushForce = 6f;
    public float kickDamage = 8f;
    public float otherToolDamage = 12f;
    public float throwSpeed = 14f;

    [Header("Выносливость")]
    public float hitStaminaPerDamage = 0.35f;
    public float kickStamina = 12f;
    public float grabStamina = 15f;
    public float toolStamina = 6f;

    enum Attack { None, Kick, Tool, Throw, Recoil, Rip }
    const int BladeSamples = 10;

    Camera cam;
    Transform pivot;
    PlayerHotbar hotbar;
    PlayerHealth health;
    CharacterController controller;
    Transform swingModel;
    Quaternion restRotation;
    Vector3 restPosition;
    Transform leg;
    ResourceNode lookedNode;
    Enemy lookedEnemy;
    string message;
    float messageUntil;
    string partText;
    float partTextUntil;

    Attack attack = Attack.None;
    float attackStart, attackDuration, hitAt;
    bool hitDone;
    Quaternion recoilFrom;
    Vector3 recoilFromPos;

    // Меч в руке
    bool swordOut;
    float outBlend, blockBlend;
    Vector2 lag;                       // клинок чуть отстаёт от камеры — чувствуется вес
    readonly Vector3[] prevPts = new Vector3[BladeSamples];
    readonly Vector3[] curPts = new Vector3[BladeSamples];
    bool havePrev;
    Vector3 lastTip, tipVel;
    float tipSpeedShown;
    float blockedUntil;
    readonly Dictionary<Enemy, float> hitCooldown = new Dictionary<Enemy, float>();

    // Клинок в теле
    Enemy embedEnemy;
    BodyPart embedPart;
    Vector3 embedLocal;
    bool embedTorso;
    float sawAccum;

    // Для «креста»
    Enemy lastHitEnemy;
    HitType lastHitType;
    Vector3 lastHitMotion;
    float lastHitTime;

    float toolSwing;
    int heldId = -1;
    float grabTime;
    bool waitingGrab;

    // Дуга броска
    LineRenderer arc;
    Transform arcMarker;
    string arcText;

    readonly List<Enemy> targets = new List<Enemy>();

    public bool IsAttacking => attack != Attack.None || swordOut;
    public bool IsHolding => heldId >= 0;
    public bool IsStuck => embedEnemy != null;

    public void PlaySwing() { toolSwing = 1f; }

    void Start()
    {
        cam = Camera.main;
        hotbar = GetComponent<PlayerHotbar>();
        health = GetComponent<PlayerHealth>();
        controller = GetComponent<CharacterController>();
        EnemyManager.GrabResult += OnGrabResult;

        PlayerCamera pc = GetComponentInChildren<PlayerCamera>();
        pivot = pc != null ? pc.transform : (cam != null ? cam.transform : transform);

        // Нога для пинка (видна только во время пинка)
        if (pc != null)
        {
            GameObject l = GameObject.CreatePrimitive(PrimitiveType.Cube);
            l.name = "KickLeg";
            Destroy(l.GetComponent<Collider>());
            l.transform.SetParent(pc.transform, false);
            l.transform.localScale = new Vector3(0.2f, 0.2f, 0.9f);
            Renderer body = GetComponent<Renderer>();
            if (body != null) l.GetComponent<Renderer>().sharedMaterial = body.sharedMaterial;
            l.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            l.SetActive(false);
            leg = l.transform;
        }
    }

    void OnDestroy()
    {
        EnemyManager.GrabResult -= OnGrabResult;
        if (arc != null) Destroy(arc.gameObject);
        if (arcMarker != null) Destroy(arcMarker.gameObject);
    }

    void OnDisable()
    {
        attack = Attack.None;
        swordOut = false;
        embedEnemy = null; embedPart = null;
        if (heldId >= 0) heldId = -1;
        if (leg != null) leg.gameObject.SetActive(false);
        if (arc != null) arc.enabled = false;
        if (arcMarker != null) arcMarker.gameObject.SetActive(false);
        PlayerCamera.LookScale = 1f;
    }

    void OnGrabResult(int id, bool ok)
    {
        waitingGrab = false;
        if (ok) { heldId = id; grabTime = Time.time; Say("Схватил! Целься — ЛКМ бросить, Q — через себя"); }
        else Say("Не получилось схватить");
    }

    void Update()
    {
        CombatFx.Tick();

        Transform model = hotbar != null ? hotbar.CurrentModel : null;
        if (model != swingModel)
        {
            if (swingModel != null) { swingModel.localRotation = restRotation; swingModel.localPosition = restPosition; }
            swingModel = model;
            if (swingModel != null) { restRotation = swingModel.localRotation; restPosition = swingModel.localPosition; }
        }

        Tool tool = hotbar != null ? hotbar.Current : Tool.Sword;
        lookedNode = null;
        lookedEnemy = null;
        if (tool != Tool.Hammer && AimHelper.Aim(cam, transform, range + 0.5f, out RaycastHit hit))
        {
            lookedNode = hit.collider.GetComponentInParent<ResourceNode>();
            lookedEnemy = hit.collider.GetComponentInParent<Enemy>();
        }

        CheckHeld();

        Mouse mouse = Mouse.current;
        Keyboard kb = Keyboard.current;
        Vector2 md = mouse != null ? mouse.delta.ReadValue() : Vector2.zero;
        bool locked = Cursor.lockState == CursorLockMode.Locked;
        bool sword = tool == Tool.Sword;
        bool blocking = health != null && health.IsBlocking;
        bool canFight = tool != Tool.Hammer && mouse != null && kb != null && locked;

        // Меч опускается: сменил предмет, поставил блок, схватил тварь
        if (swordOut && (!canFight || !sword || blocking || IsHolding)) LowerSword();

        if (canFight)
        {
            if (IsHolding)
            {
                if (mouse.leftButton.wasPressedThisFrame || kb.gKey.wasPressedThisFrame) ThrowHeld(false);
                else if (kb.qKey.wasPressedThisFrame) ThrowHeld(true);
            }
            else
            {
                if (kb.qKey.wasPressedThisFrame && attack == Attack.None) StartAttack(Attack.Kick);
                if (kb.gKey.wasPressedThisFrame && attack == Attack.None && !IsStuck) TryGrab();

                if (sword && !blocking)
                {
                    bool lmb = mouse.leftButton.isPressed;
                    if (lmb && !swordOut && (attack == Attack.None || attack == Attack.Kick)) RaiseSword();
                    else if (!lmb && swordOut) LowerSword();
                }
                else if (!sword && mouse.leftButton.wasPressedThisFrame && attack == Attack.None) StartAttack(Attack.Tool);
            }
        }

        UpdateSword(md);
        UpdateAttack(tool);
        UpdateArc();
        PlayerCamera.LookScale = IsStuck ? embeddedLook : 1f;
        Animate();
    }

    // =====================================================================
    // Меч в руке
    // =====================================================================
    void RaiseSword()
    {
        swordOut = true;
        havePrev = false;
        lag = Vector2.zero;
    }

    void LowerSword()
    {
        swordOut = false;
        havePrev = false;
        if (IsStuck) RipOut();
    }

    static readonly Vector3 GuardDir = new Vector3(0.06f, -0.1f, 1f).normalized;

    Vector3 BladeDirLocal() => Quaternion.Euler(lag.y, lag.x, 0f) * GuardDir;

    Vector3 HandWorld => pivot.TransformPoint(new Vector3(0.18f, -0.18f, 0.05f));

    void BladePoints(Vector3[] pts)
    {
        Vector3 hand = HandWorld;
        Vector3 d = pivot.TransformDirection(BladeDirLocal());
        for (int i = 0; i < pts.Length; i++) pts[i] = hand + d * Mathf.Lerp(0.45f, bladeLength, i / (pts.Length - 1f));
    }

    void UpdateSword(Vector2 md)
    {
        float udt = Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
        outBlend = Mathf.MoveTowards(outBlend, swordOut ? 1f : 0f, udt * 7f);

        // клинок отстаёт от поворота камеры, потом догоняет
        Vector2 targetLag = Vector2.ClampMagnitude(new Vector2(-md.x, md.y) * 0.35f, 22f);
        lag = Vector2.Lerp(lag, targetLag, 1f - Mathf.Exp(-12f * udt));

        // скорость кончика клинка (по камере — она крутится в реальном времени)
        BladePoints(curPts);
        Vector3 tip = curPts[BladeSamples - 1];
        tipVel = Vector3.Lerp(tipVel, (tip - lastTip) / udt, 1f - Mathf.Exp(-20f * udt));
        lastTip = tip;
        tipSpeedShown = Mathf.Lerp(tipSpeedShown, swordOut && !IsStuck ? tipVel.magnitude : 0f, 1f - Mathf.Exp(-10f * udt));

        if (!swordOut) { havePrev = false; return; }
        if (IsStuck) { UpdateEmbed(md); havePrev = false; return; }
        if (outBlend < 0.85f || Time.time < blockedUntil || attack == Attack.Recoil || attack == Attack.Rip) { havePrev = false; return; }

        if (havePrev)
        {
            for (int i = 0; i < BladeSamples; i++)
            {
                float speed = (curPts[i] - prevPts[i]).magnitude / udt;
                if (TestSegment(prevPts[i], curPts[i], speed)) break;
            }
        }
        havePrev = swordOut && !IsStuck && Time.time >= blockedUntil;
        System.Array.Copy(curPts, prevPts, BladeSamples);
    }

    // Отрезок, который прошёл клинок за кадр. true — дальше не проверять
    bool TestSegment(Vector3 a, Vector3 b, float speed)
    {
        Vector3 d = b - a;
        float len = d.magnitude;
        if (len < 0.0005f) return false;
        Vector3 motion = d / len;
        RaycastHit[] hits = Physics.RaycastAll(a, motion, len, ~(1 << 2), QueryTriggerInteraction.Collide);
        System.Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
        foreach (RaycastHit h in hits)
        {
            if (h.transform.root == transform.root) continue;
            BodyPart bp = h.collider.GetComponentInParent<BodyPart>();
            if (bp != null)
            {
                if (!bp.enabled || bp.owner == null || speed < minHitSpeed) continue;
                if (hitCooldown.TryGetValue(bp.owner, out float until) && Time.time < until) continue;
                Strike(bp, h.point, motion, speed);
                return true;
            }
            if (h.collider.isTrigger) continue;
            if (h.collider.GetComponentInParent<Enemy>() != null) continue;
            if (h.collider.GetComponentInParent<PlayerMovement>() != null || h.collider.GetComponentInParent<NetworkPlayer>() != null) continue;
            Rigidbody body = h.collider.attachedRigidbody;
            if (body != null && !body.isKinematic) { if (speed > 2f) body.AddForce(motion * Mathf.Min(speed, 10f) * 0.6f, ForceMode.VelocityChange); continue; }
            if (Mathf.Abs(h.normal.y) > 0.55f) continue;  // пол — скользит
            if (speed >= minHitSpeed) HitWall(h.point);
            return true;
        }
        return false;
    }

    // Клинок вошёл в часть тела
    void Strike(BodyPart bp, Vector3 point, Vector3 motion, float speed)
    {
        Enemy e = bp.owner;
        Vector3 ml = pivot.InverseTransformDirection(motion);
        HitType type = ml.z > 0.75f ? HitType.Thrust : ml.y < -0.6f ? HitType.Chop : ml.y > 0.6f ? HitType.Uppercut : HitType.Slash;
        bool heavy = speed >= heavySpeed;
        float dmg = Mathf.Min(speed, maxSpeedForDamage) * damagePerSpeed;
        if (health != null && !health.UseStamina(dmg * hitStaminaPerDamage, true)) { dmg *= 0.4f; Say("Нет сил — удар слабый"); }
        bool brute = e.kind == EnemyKind.Brute;

        // КРЕСТ: два рубящих по одной твари подряд в разные стороны
        if (type == HitType.Slash && lastHitEnemy == e && lastHitType == HitType.Slash && Time.time - lastHitTime < 1.4f &&
            Vector3.Dot(lastHitMotion, motion) < -0.4f)
        {
            type = HitType.Cross;
            dmg *= 1.4f;
            if (brute) EnemyManager.ShowCallout(Callout.Cross);
        }
        lastHitEnemy = e; lastHitType = type; lastHitMotion = motion; lastHitTime = Time.time;

        bool bounce = brute && !heavy && type != HitType.Thrust && (bp.type == BodyPartType.Torso || bp.type == BodyPartType.Head);
        bool cutThrough = heavy && type != HitType.Thrust && !brute && bp.type == BodyPartType.Torso; // с размаху — отбрасывает
        bool embed = !bounce && !cutThrough;

        EnemyManager.RequestHit(e, new HitInfo
        {
            damage = dmg, dir = motion, part = bp.type, type = type, heavy = heavy,
            embed = embed && bp.type == BodyPartType.Torso && !brute
        });
        e.LocalReact((int)bp.type, motion, Mathf.Clamp(speed / 10f, 0.5f, 1.8f));
        CombatFx.Ichor(point, motion, Mathf.Clamp(Mathf.RoundToInt(speed * 0.8f), 4, 16));
        CombatFx.Hitstop(heavy ? 0.07f : 0.035f);
        PlayerCamera.Shake(Mathf.Clamp(speed / 40f, 0.08f, 0.45f));
        ShowPart(bp.type, heavy);
        hitCooldown[e] = Time.time + 0.3f;

        if (bounce) { Bounce("Отскок! Громилу — быстрым сильным ударом или по ногам"); return; }
        if (embed)
        {
            embedEnemy = e;
            embedPart = bp;
            embedLocal = bp.transform.InverseTransformPoint(point + motion * 0.12f);
            embedTorso = bp.type == BodyPartType.Torso && !brute;
            sawAccum = 0f;
        }
    }

    // Клинок в теле: тяжело, можно пилить, отпустил — выдернул
    void UpdateEmbed(Vector2 md)
    {
        bool gone = embedPart == null || embedPart.lost || !embedPart.enabled || embedEnemy == null || embedEnemy.HealthFraction <= 0f;
        if (gone) { FreeBlade(); return; } // отрубил или убил — клинок свободен
        Vector3 p = embedPart.transform.TransformPoint(embedLocal);
        if (Vector3.Distance(p, HandWorld) > bladeLength + 0.6f) { Say("Клинок вырвало"); RipOut(); return; }

        sawAccum += md.magnitude;
        if (sawAccum > 220f)
        {
            sawAccum = 0f;
            Vector3 dir = pivot.TransformDirection(new Vector3(md.x, md.y, 0f).normalized);
            EnemyManager.RequestHit(embedEnemy, new HitInfo { damage = sawDamage, dir = dir, part = embedPart.type, type = HitType.Slash });
            embedEnemy.LocalReact((int)embedPart.type, dir, 0.5f);
            CombatFx.Ichor(p, dir, 5);
            PlayerCamera.Shake(0.08f);
        }
    }

    void FreeBlade()
    {
        embedEnemy = null;
        embedPart = null;
        havePrev = false;
    }

    void RipOut()
    {
        Enemy e = embedEnemy;
        BodyPart bp = embedPart;
        bool torso = embedTorso;
        FreeBlade();
        if (e == null || bp == null) return;
        Vector3 back = -FlatForward();
        Vector3 p = bp.transform.TransformPoint(embedLocal);
        if (torso && tipVel.magnitude > 7f)
        {
            // резко повёл и отпустил — тварь слетает с клинка в ту сторону
            Vector3 fling = tipVel; fling.y = Mathf.Max(fling.y, 0f);
            EnemyManager.RequestHit(e, new HitInfo { damage = ripDamage, dir = fling.normalized, part = BodyPartType.Torso, type = HitType.Kick });
            e.LocalReact((int)BodyPartType.Torso, fling.normalized, 1.4f);
            EnemyManager.ShowCallout("СБРОСИЛ С КЛИНКА!");
        }
        else
        {
            EnemyManager.RequestHit(e, new HitInfo { damage = ripDamage, dir = back, part = bp.type, type = HitType.RipOut });
            e.LocalReact((int)bp.type, back, 1.2f);
        }
        CombatFx.Ichor(p, back, 16);
        CombatFx.Hitstop(0.05f);
        PlayerCamera.Shake(0.3f);
        hitCooldown[e] = Time.time + 0.35f;
        if (swingModel != null) { recoilFrom = swingModel.localRotation; recoilFromPos = swingModel.localPosition; }
        StartAttack(Attack.Rip);
    }

    void ShowPart(BodyPartType t, bool heavy)
    {
        switch (t)
        {
            case BodyPartType.Head: partText = "ГОЛОВА ×1.8"; break;
            case BodyPartType.ArmL: case BodyPartType.ArmR: partText = "рука"; break;
            case BodyPartType.LegL: case BodyPartType.LegR: partText = "нога — хромает"; break;
            case BodyPartType.Sack: partText = "МЕШОК!"; break;
            default: partText = "тело"; break;
        }
        if (heavy) partText += "   СИЛЬНО";
        partTextUntil = Time.time + 0.8f;
    }

    void HitWall(Vector3 point)
    {
        CombatFx.Sparks(point, Color.white, 8);
        CombatFx.Hitstop(0.04f);
        Bounce(null);
    }

    // Клинок отскочил
    void Bounce(string text)
    {
        if (swingModel != null) { recoilFrom = swingModel.localRotation; recoilFromPos = swingModel.localPosition; }
        attack = Attack.Recoil;
        attackStart = Time.time;
        attackDuration = 0.32f;
        hitAt = 99f;
        hitDone = true;
        blockedUntil = Time.time + 0.35f;
        havePrev = false;
        Lunge(-0.2f);
        PlayerCamera.Shake(0.25f);
        if (text != null) Say(text);
    }

    Vector3 FlatForward()
    {
        Vector3 f = transform.forward; f.y = 0f;
        return f.sqrMagnitude > 0.001f ? f.normalized : Vector3.forward;
    }

    // =====================================================================
    // Пинок, инструменты, бросок
    // =====================================================================
    void StartAttack(Attack a)
    {
        float cost = a == Attack.Kick ? kickStamina : a == Attack.Tool ? toolStamina : 0f;
        if (health != null && cost > 0f && !health.UseStamina(cost, false)) { Say("Нет сил — передохни"); return; }

        attack = a;
        attackStart = Time.time;
        hitDone = false;
        switch (a)
        {
            case Attack.Kick: attackDuration = 0.45f; hitAt = 0.14f; break;
            case Attack.Tool: attackDuration = 0.45f; hitAt = 0.15f; break;
            case Attack.Throw: attackDuration = 0.4f; hitAt = 0.1f; hitDone = true; break;
            case Attack.Rip: attackDuration = 0.3f; hitAt = 0.1f; hitDone = true; break;
        }
        if (leg != null) leg.gameObject.SetActive(a == Attack.Kick);
    }

    void UpdateAttack(Tool tool)
    {
        if (attack == Attack.None) return;
        float t = Time.time - attackStart;
        if (!hitDone && t >= hitAt) { hitDone = true; DoHit(tool); }
        if (t < attackDuration) return;
        if (leg != null) leg.gameObject.SetActive(false);
        attack = Attack.None;
    }

    void DoHit(Tool tool)
    {
        Vector3 fwd = FlatForward();
        switch (attack)
        {
            case Attack.Kick:
            {
                Enemy e = null;
                if (IsStuck) { e = embedEnemy; FreeBlade(); } // пнуть с клинка
                else
                {
                    FindTargets(range - 0.3f, 55f, 1, 3.5f);
                    if (targets.Count > 0) e = targets[0];
                }
                if (e == null) return;
                EnemyManager.RequestHit(e, new HitInfo { damage = kickDamage, dir = fwd, part = BodyPartType.Torso, type = HitType.Kick });
                e.LocalReact((int)BodyPartType.Torso, fwd, 1.3f);
                hitCooldown[e] = Time.time + 0.35f;
                CombatFx.Hitstop(0.05f);
                PlayerCamera.Shake(0.25f);
                break;
            }
            case Attack.Tool:
                if (lookedEnemy != null)
                {
                    EnemyManager.RequestHit(lookedEnemy, new HitInfo { damage = otherToolDamage, dir = fwd, part = BodyPartType.Torso, type = HitType.Tool });
                    lookedEnemy.LocalReact((int)BodyPartType.Torso, fwd, 0.8f);
                    CombatFx.Hitstop(0.03f);
                }
                else if (lookedNode != null)
                {
                    if (tool == lookedNode.requiredTool) BuildingManager.RequestHitNode(lookedNode);
                    else Say("Нужен " + PlayerHotbar.NameOf(lookedNode.requiredTool).ToLower() + " (клавиша " + ((int)lookedNode.requiredTool + 1) + ")");
                }
                break;
        }
    }

    void FindTargets(float reach, float arcDeg, int max, float height = 2.5f)
    {
        targets.Clear();
        Vector3 fwd = FlatForward();
        Vector3 me = transform.position;
        foreach (Enemy e in Enemy.All)
        {
            if (e == null || e.HealthFraction <= 0f || e.CurrentState == Enemy.State.Grabbed) continue;
            Vector3 d = e.transform.position - me;
            if (Mathf.Abs(d.y) > height) continue;
            d.y = 0f;
            float dist = d.magnitude - 0.45f * e.bodyScale;
            if (dist > reach) continue;
            if (dist > 0.6f && Vector3.Angle(fwd, d) > arcDeg) continue;
            targets.Add(e);
        }
        targets.Sort((a, b) => (a.transform.position - me).sqrMagnitude.CompareTo((b.transform.position - me).sqrMagnitude));
        if (targets.Count > max) targets.RemoveRange(max, targets.Count - max);
    }

    void Lunge(float meters)
    {
        if (controller == null || !controller.enabled) return;
        controller.Move(FlatForward() * meters);
    }

    // ---------- Захват ----------
    void TryGrab()
    {
        if (waitingGrab) return;
        FindTargets(2.6f, 55f, 1);
        if (targets.Count == 0) { Say("Некого хватать — подойди вплотную"); return; }
        Enemy e = targets[0];
        if (!e.CanBeGrabbed) { Say("Громилу не поднять — сначала сбей его (сильный удар, по ногам или парирование)"); return; }
        if (health != null && !health.UseStamina(grabStamina, false)) { Say("Нет сил — передохни"); return; }
        waitingGrab = true;
        EnemyManager.RequestGrab(e);
    }

    void CheckHeld()
    {
        if (heldId < 0) return;
        Enemy e = EnemyManager.Instance != null ? EnemyManager.Instance.Find(heldId) : null;
        bool stillMine = e != null && (Time.time - grabTime < 0.6f || (e.HeldBy >= 0 && (ulong)e.HeldBy == Net.LocalId));
        if (!stillMine) { heldId = -1; Say("Вырвалась!"); }
    }

    Vector3 ThrowVelocity()
    {
        Vector3 f = cam != null ? cam.transform.forward : transform.forward;
        return f * throwSpeed + Vector3.up * 2.5f;
    }

    void ThrowHeld(bool overShoulder)
    {
        Enemy e = EnemyManager.Instance != null ? EnemyManager.Instance.Find(heldId) : null;
        int id = heldId;
        heldId = -1;
        if (e == null) return;
        if (overShoulder)
        {
            // через себя: дугой над головой и об землю за спиной
            EnemyManager.RequestHit(e, new HitInfo { damage = 20f, dir = Vector3.down, part = BodyPartType.Torso, type = HitType.Slash });
            EnemyManager.RequestThrow(id, -transform.forward * 5f + Vector3.up * 8f, 8f);
            PlayerCamera.Shake(0.3f);
        }
        else
        {
            EnemyManager.RequestThrow(id, ThrowVelocity(), 2f);
            PlayerCamera.Shake(0.2f);
        }
        StartAttack(Attack.Throw);
    }

    // Дуга полёта и место падения, пока держишь тварь
    void UpdateArc()
    {
        arcText = null;
        Enemy held = IsHolding && EnemyManager.Instance != null ? EnemyManager.Instance.Find(heldId) : null;
        if (held == null)
        {
            if (arc != null) arc.enabled = false;
            if (arcMarker != null) arcMarker.gameObject.SetActive(false);
            return;
        }
        if (arc == null)
        {
            GameObject g = new GameObject("ThrowArc");
            arc = g.AddComponent<LineRenderer>();
            Shader sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
            arc.material = new Material(sh);
            arc.startColor = new Color(1f, 0.75f, 0.3f, 0.9f);
            arc.endColor = new Color(1f, 0.4f, 0.2f, 0.3f);
            arc.startWidth = 0.07f; arc.endWidth = 0.04f;
            arc.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            GameObject m = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(m.GetComponent<Collider>());
            m.name = "ThrowMarker";
            m.transform.localScale = new Vector3(0.9f, 0.02f, 0.9f);
            m.GetComponent<Renderer>().sharedMaterial = arc.material;
            arcMarker = m.transform;
        }
        arc.enabled = true;
        Vector3 p = held.transform.position + Vector3.up * 0.9f * held.bodyScale;
        Vector3 v = ThrowVelocity() * Mathf.Lerp(1f, held.knockScale, 0.5f);
        Vector3 g2 = Physics.gravity;
        var pts = new List<Vector3> { p };
        bool landed = false;
        RaycastHit land = default;
        for (int i = 0; i < 70 && !landed; i++)
        {
            Vector3 next = p + v * 0.04f + 0.5f * g2 * 0.0016f;
            v += g2 * 0.04f;
            Vector3 d = next - p;
            foreach (RaycastHit h in Physics.RaycastAll(p, d.normalized, d.magnitude, ~(1 << 2), QueryTriggerInteraction.Ignore))
            {
                if (h.transform.root == transform.root || h.transform.root == held.transform.root) continue;
                if (!landed || h.distance < land.distance) { land = h; landed = true; }
            }
            p = landed ? land.point : next;
            pts.Add(p);
        }
        arc.positionCount = pts.Count;
        arc.SetPositions(pts.ToArray());
        arcMarker.gameObject.SetActive(landed);
        if (landed)
        {
            arcMarker.position = land.point + land.normal * 0.03f;
            arcMarker.up = land.normal;
            BuildPiece bp = land.collider.GetComponentInParent<BuildPiece>();
            Enemy other = land.collider.GetComponentInParent<Enemy>();
            if (bp != null && bp.pieceId == "Barricade") arcText = "НА КОЛЬЯ";
            else if (other != null) arcText = "В ТВАРЬ — собьёт с ног";
            else if (Mathf.Abs(land.normal.y) < 0.5f) arcText = "В СТЕНУ";
        }
        else if (p.y < -10f) arcText = "В ПРОПАСТЬ";
    }

    // =====================================================================
    // Анимация оружия в руке
    // =====================================================================
    static Quaternion BladeRotation(Vector3 dl)
    {
        Vector3 hint = Mathf.Abs(Vector3.Dot(dl, Vector3.up)) > 0.95f ? Vector3.back : Vector3.up;
        return Quaternion.LookRotation(dl, hint) * Quaternion.Euler(90f, 0f, 0f); // ось клинка (вверх у модели) — вдоль dl
    }

    void Animate()
    {
        if (swingModel == null) return;
        float udt = Time.unscaledDeltaTime;
        Quaternion rot = restRotation;
        Vector3 pos = restPosition;
        Vector3 hand = new Vector3(0.22f, -0.22f, 0.3f);
        float now = Time.time;
        bool sword = hotbar == null || hotbar.Current == Tool.Sword;

        if (attack == Attack.Recoil || attack == Attack.Rip)
        {
            float p = Mathf.Clamp01((now - attackStart) / attackDuration);
            Quaternion target = swordOut ? BladeRotation(BladeDirLocal()) : restRotation;
            Vector3 targetPos = swordOut ? hand + BladeDirLocal() * 0.15f : restPosition;
            rot = Quaternion.Slerp(recoilFrom, target, Mathf.SmoothStep(0f, 1f, p)) * Quaternion.Euler(-25f * Mathf.Sin(p * Mathf.PI), 0f, 0f);
            pos = Vector3.Lerp(recoilFromPos, targetPos, p) + Vector3.back * 0.15f * Mathf.Sin(p * Mathf.PI);
        }
        else if (IsStuck && embedPart != null)
        {
            // клинок указывает в точку, где он сидит в теле, и подрагивает
            Vector3 w = embedPart.transform.TransformPoint(embedLocal);
            Vector3 dl = pivot.InverseTransformDirection(w - HandWorld).normalized;
            rot = BladeRotation(dl) * Quaternion.Euler(Mathf.Sin(now * 37f) * 2f, 0f, Mathf.Sin(now * 29f) * 2f);
            pos = hand + dl * 0.28f;
        }
        else if (outBlend > 0f && sword)
        {
            Vector3 dl = BladeDirLocal();
            float w = Mathf.SmoothStep(0f, 1f, outBlend);
            rot = Quaternion.Slerp(restRotation, BladeRotation(dl), w);
            pos = Vector3.Lerp(restPosition, hand + dl * 0.15f, w);
        }
        else if (attack == Attack.Kick || attack == Attack.Tool || attack == Attack.Throw)
        {
            float p = Mathf.Clamp01((now - attackStart) / attackDuration);
            float hitP = Mathf.Max(0.01f, hitAt / attackDuration);
            float strike = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(p / (hitP * 1.4f)));
            float back = p < 0.6f ? 1f : 1f - (p - 0.6f) / 0.4f;
            Vector3 from = Vector3.zero, to = Vector3.zero;
            switch (attack)
            {
                case Attack.Kick: from = new Vector3(10f, 0f, 0f); to = new Vector3(25f, 20f, 0f); break;
                case Attack.Tool: from = new Vector3(-40f, 0f, 0f); to = new Vector3(70f, -20f, 0f); break;
                case Attack.Throw: from = new Vector3(-20f, 0f, 0f); to = new Vector3(40f, 0f, 0f); break;
            }
            rot = restRotation * Quaternion.Euler(Vector3.Lerp(from, to, strike) * back);
        }
        else if (IsHolding)
        {
            rot = restRotation * Quaternion.Euler(-40f, 0f, 30f); // меч в сторону, руки заняты
        }
        else if (toolSwing > 0f)
        {
            toolSwing = Mathf.MoveTowards(toolSwing, 0f, Time.deltaTime / 0.45f);
            float a = Mathf.Sin(toolSwing * Mathf.PI) * 80f;
            rot = restRotation * Quaternion.Euler(a, -a * 0.4f, 0f);
        }

        // Нога при пинке
        if (leg != null && attack == Attack.Kick)
        {
            float p = Mathf.Clamp01((now - attackStart) / attackDuration);
            float k = Mathf.Sin(Mathf.Clamp01(p / 0.7f) * Mathf.PI);
            leg.localPosition = new Vector3(0.12f, -1.2f + 0.35f * k, 0.15f + 0.95f * k);
            leg.localRotation = Quaternion.Euler(-25f * k, 0f, 0f);
        }

        // Блок: меч поперёк перед лицом. Удар в блок — меч дёргается
        bool blocking = sword && health != null && health.IsBlocking;
        blockBlend = Mathf.MoveTowards(blockBlend, blocking ? 1f : 0f, udt * 10f);
        if (blockBlend > 0f)
        {
            float jolt = health != null ? health.BlockJolt : 0f;
            Vector3 bl = new Vector3(-1f, 0.2f, 0.25f).normalized;
            Quaternion br = BladeRotation(bl) * Quaternion.Euler(-jolt * 12f, 0f, 0f);
            Vector3 bpos = new Vector3(0.12f, -0.06f + jolt * 0.04f, 0.5f - jolt * 0.08f);
            float w = Mathf.SmoothStep(0f, 1f, blockBlend);
            rot = Quaternion.Slerp(rot, br, w);
            pos = Vector3.Lerp(pos, bpos, w);
        }

        swingModel.localRotation = rot;
        swingModel.localPosition = pos;
    }

    void Say(string text) { message = text; messageUntil = Time.time + 1.5f; }

    // =====================================================================
    // Экран
    // =====================================================================
    void OnGUI()
    {
        float cx = Screen.width / 2f, cy = Screen.height / 2f;
        GUI.Label(new Rect(cx - 6, cy - 12, 20, 24), "+", Hud.Big);

        // Скорость клинка: белая — обычный удар, оранжевая — сильный
        if (swordOut && !IsStuck)
        {
            float k = Mathf.Clamp01(tipSpeedShown / heavySpeed);
            Color c = tipSpeedShown >= heavySpeed ? new Color(1f, 0.55f, 0.15f) : tipSpeedShown >= minHitSpeed ? Color.white : new Color(1f, 1f, 1f, 0.35f);
            Hud.Bar(new Rect(cx - 40, cy + 18, 80, 4), k, c, null);
        }

        if (Time.time < partTextUntil)
        {
            Color old = GUI.color;
            GUI.color = new Color(1f, 0.9f, 0.6f, Mathf.Clamp01((partTextUntil - Time.time) / 0.3f));
            GUI.Label(new Rect(cx + 26, cy - 4, 300, 22), partText, Hud.Small);
            GUI.color = old;
        }

        if (IsHolding)
        {
            GUI.Label(new Rect(0, cy + 60, Screen.width, 24), "Держишь тварь:  целься — ЛКМ / G бросить   Q — через себя", Hud.Center);
            if (arcText != null)
            {
                Color old = GUI.color; GUI.color = new Color(1f, 0.75f, 0.3f);
                GUI.Label(new Rect(0, cy + 84, Screen.width, 24), arcText, Hud.Center);
                GUI.color = old;
            }
        }
        else if (IsStuck)
            GUI.Label(new Rect(0, cy + 60, Screen.width, 24),
                      "Клинок в теле:  отпусти ЛКМ — выдернуть (резко поведи — сбросишь)   води мышью — пилить   Q — пнуть   к стене — пригвоздить", Hud.Center);
        else if (hotbar != null && hotbar.Current != Tool.Hammer && lookedNode == null)
            GUI.Label(new Rect(0, Screen.height - 88, Screen.width, 22),
                      hotbar.Current == Tool.Sword
                          ? "Держи ЛКМ и веди мышью — рубишь (быстрее — сильнее)   ПКМ — блок (смотри на врага)   Q — пинок   G — схватить   Ctrl — перекат"
                          : "ЛКМ — удар   ПКМ — блок только мечом   Q — пинок   G — схватить   Ctrl — перекат", Hud.CenterTiny);

        if (lookedNode != null)
        {
            Rect r = new Rect(cx - 100, cy + 40, 200, 10);
            Hud.Bar(r, (float)lookedNode.HitsLeft / lookedNode.maxHits, new Color(0.9f, 0.75f, 0.3f),
                    lookedNode.displayName + "  —  " + PlayerHotbar.NameOf(lookedNode.requiredTool).ToLower());
        }
        if (Time.time < messageUntil)
            GUI.Label(new Rect(cx - 300, cy - 70, 600, 30), message, Hud.CenterBig);
    }
}

// Общие помощники: куда смотрит игрок
public static class AimHelper
{
    public static bool Aim(Camera cam, Transform self, float reach, out RaycastHit result)
    {
        result = default;
        if (cam == null) return false;
        // В виде от 3-го лица камера дальше — добавляем это расстояние
        Vector3 head = cam.transform.parent != null ? cam.transform.parent.position : cam.transform.position;
        float extra = Vector3.Distance(cam.transform.position, head);
        Ray ray = new Ray(cam.transform.position, cam.transform.forward);
        RaycastHit[] hits = Physics.RaycastAll(ray, reach + extra, ~(1 << 2), QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        bool found = false;
        foreach (RaycastHit h in hits)
        {
            if (h.transform.root == self.root) continue;
            if (h.distance < best) { best = h.distance; result = h; found = true; }
        }
        return found;
    }

    public static BuildPiece LookedPiece(Camera cam, Transform self, float reach)
    {
        return Aim(cam, self, reach, out RaycastHit h) ? h.collider.GetComponentInParent<BuildPiece>() : null;
    }
}

// Общие помощники для надписей и панелей на экране
public static class Hud
{
    static GUIStyle big, small, tiny, center, centerBig, centerTiny, title;
    public static GUIStyle Big => big ??= new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
    public static GUIStyle Small => small ??= new GUIStyle(GUI.skin.label) { fontSize = 16, normal = { textColor = Color.white } };
    public static GUIStyle Tiny => tiny ??= new GUIStyle(GUI.skin.label) { fontSize = 12, normal = { textColor = new Color(1f, 1f, 1f, 0.7f) } };
    public static GUIStyle Center => center ??= new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
    public static GUIStyle CenterBig => centerBig ??= new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
    public static GUIStyle CenterTiny => centerTiny ??= new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(1f, 1f, 1f, 0.75f) } };
    public static GUIStyle Title => title ??= new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold, wordWrap = true, normal = { textColor = Color.white } };

    public static void Fill(Rect r, Color c, float radius = 0f)
    {
        GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, false, 0, c, 0, radius);
    }

    public static void Bar(Rect r, float fill, Color color, string text)
    {
        Fill(r, new Color(0, 0, 0, 0.6f));
        Fill(new Rect(r.x, r.y, r.width * Mathf.Clamp01(fill), r.height), color);
        if (!string.IsNullOrEmpty(text)) GUI.Label(new Rect(r.x - 150, r.y - 26, r.width + 300, 24), text, Center);
    }
}
