using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Бой мечом — просто и понятно. Бьёт тот клинок, который видно на экране (длинный, 1.4 м).
//
//   ЛКМ (щелчок)            — удар. Подряд: справа налево, слева направо, третий — сверху (сильнее).
//   ЛКМ зажать и отпустить  — тяжёлый удар сверху. Клинок входит в тело, видно, как он там сидит, и сам выходит.
//   Куда смотришь — туда и бьёшь: ниже — по ногам, выше — по голове.
//   Лёгкий удар по туловищу «вязнет» в теле и проходит дальше. Громилу лёгким не взять — клинок отскакивает.
//   Q — пинок. G — схватить тварь; пока держишь, видна дуга броска: ЛКМ или G — бросить, Q — через себя.
//   ПКМ — блок (держи врага в прицеле), Ctrl / C — перекат.
//   Топор и кирка рубят деревья и бьют камни по щелчку. Лук — см. PlayerBow.
public class PlayerCombat : MonoBehaviour
{
    [Header("Меч")]
    public float slashDamage = 20f;
    public float comboEndDamage = 28f;
    public float heavyDamage = 48f;
    public float ripDamage = 12f;
    public float swingTime = 0.36f;        // лёгкий удар целиком (замах, удар, возврат)
    public float comboEndTime = 0.46f;
    public float heavyTime = 0.55f;
    public float chargeTime = 0.6f;        // сколько держать ЛКМ для полного замаха (тяжёлый удар)
    public float embedTime = 0.45f;        // сколько клинок сидит в теле после тяжёлого удара
    public bool impale = false;            // true — сильный удар насаживает тварь на клинок (старое поведение)

    [Header("Остальное")]
    public float range = 2.8f;
    public float kickDamage = 8f;
    public float otherToolDamage = 12f;
    public float throwSpeed = 14f;

    [Header("Выносливость")]
    public float swingStamina = 8f;
    public float heavyStamina = 20f;
    public float kickStamina = 12f;
    public float grabStamina = 15f;
    public float toolStamina = 6f;
    public float bashStamina = 8f;         // удар луком / посохом / бластером в ближнем бою (ПКМ)

    enum Attack { None, Kick, Tool, Throw, Recoil, Rip, Bash }
    const int BladeSamples = 10;

    Camera cam;
    Transform pivot;
    PlayerCamera pcam;
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

    // Удар мечом
    bool swinging;
    float swP, swDur;                  // доля удара 0..1 и его длительность
    Vector2 swingS;                    // направление удара на экране
    bool swingHeavy;
    float swingPower;                  // 0 — щелчок, 1 — полный замах
    float swStart, swEnd, swOver;      // углы дуги: откуда, докуда, сколько проходит по инерции
    float swW, swS, swF;               // фазы: замах до swW, удар до swS, проход по инерции до swF, потом возврат
    Quaternion swFromRot, chargeFromRot;
    Vector3 swFromPos, chargeFromPos;
    bool chargeCaptured;
    float swingDamage;
    int comboStep;
    float comboUntil;
    bool queued;
    bool charging;
    float pressTime;
    float dragUntil;                   // клинок «вязнет» в теле
    float bladeTipY = 1.5f, bladeBaseY = 0.12f;
    readonly Vector3[] prevPts = new Vector3[BladeSamples];
    readonly Vector3[] curPts = new Vector3[BladeSamples];
    bool havePrev;
    readonly HashSet<Enemy> hitThisSwing = new HashSet<Enemy>();

    // Клинок в теле (после тяжёлого удара)
    Enemy embedEnemy;
    BodyPart embedPart;
    Vector3 embedLocal;
    float embedUntil;
    bool cutting;                      // клинок входит в тело: идёт всё медленнее и застревает
    float cutStart, stuckAt;
    Quaternion stuckFromRot; Vector3 stuckFromPos;
    // выбор удара мышью во время замаха: дёрнул вверх — удар сверху, вбок — с той стороны
    Vector2 chargeAcc, chosenDir;
    bool hasChosen, chargeLook;
    Quaternion chargeRot; Vector3 chargePos;
    public float chargeFlick = 22f;

    float toolSwing;
    int heldId = -1;
    float grabTime;
    bool waitingGrab;

    // Дуга броска
    LineRenderer arc;
    Transform arcMarker;
    string arcText;

    readonly List<Enemy> targets = new List<Enemy>();
    PlayerMovement movement;
    readonly HashSet<Enemy> tripped = new HashSet<Enemy>();
    readonly Collider[] slideHits = new Collider[24];

    public bool IsAttacking => attack != Attack.None || swinging;
    public bool IsHolding => heldId >= 0;
    public bool IsStuck => embedEnemy != null;

    public void PlaySwing() { toolSwing = 1f; }

    // У этих оружий своё управление (PlayerBow, PlayerStaff, PlayerBlaster)
    // Удар в ближнем бою луком, посохом или бластером: 0..1 — сколько прошло, меньше 0 — не бью
    public float BashP => attack == Attack.Bash ? Mathf.Clamp01((Time.time - attackStart) / attackDuration) : -1f;
    float rmbAt = -10f;

    // Поза оружия при ударе: замах (0–35%), удар (35–60%), возврат. Каждое оружие даёт свои позы
    public static void BashPose(float u, Vector3 wPos, Vector3 wRot, Vector3 sPos, Vector3 sRot, ref Vector3 pos, ref Quaternion rot)
    {
        if (u < 0f) return;
        Vector3 op, orr;
        if (u < 0.35f) { float w = Smooth(u / 0.35f); op = wPos * w; orr = wRot * w; }
        else if (u < 0.6f) { float q = (u - 0.35f) / 0.25f; q = q * q * (2.2f - 1.2f * q); op = Vector3.Lerp(wPos, sPos, q); orr = Vector3.Lerp(wRot, sRot, q); }
        else { float w = Smooth((u - 0.6f) / 0.4f); op = sPos * (1f - w); orr = sRot * (1f - w); }
        pos += op;
        rot *= Quaternion.Euler(orr);
    }

    // Лук или посох в руках, а тварь держу: перехватываю её древком за шею, двумя руками
    bool poleHold;
    Vector3 polePos; Quaternion poleRot;
    public bool PoleHold(Transform model, out Vector3 pos, out Quaternion rot)
    {
        pos = polePos; rot = poleRot;
        if (!poleHold || model == null) return false;
        // посох держу за середину (его начало — у нижней рукояти), лук — за рукоять
        if (hotbar != null && hotbar.Current == Tool.Staff) pos -= poleRot * Vector3.up * 0.42f;
        return true;
    }

    public static bool OwnWeapon(Tool t) => t == Tool.Bow || t == Tool.Staff || t == Tool.Blaster;

    void Start()
    {
        cam = Camera.main;
        hotbar = GetComponent<PlayerHotbar>();
        health = GetComponent<PlayerHealth>();
        controller = GetComponent<CharacterController>();
        movement = GetComponent<PlayerMovement>();
        EnemyManager.GrabResult += OnGrabResult;

        PlayerCamera pc = GetComponentInChildren<PlayerCamera>();
        pcam = pc;
        pivot = pc != null ? pc.transform : (cam != null ? cam.transform : transform);

        // Длина клинка — по модели меча (верх самой высокой детали)
        if (hotbar != null && hotbar.swordModel != null)
        {
            float top = 0f;
            foreach (Transform c in hotbar.swordModel) top = Mathf.Max(top, c.localPosition.y + c.localScale.y * 0.5f);
            if (top > 0.3f) bladeTipY = top;
        }

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
            Destroy(l);                 // пинаем своей настоящей ногой (PlayerBody), отдельная нога не нужна
            leg = null;
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
        swinging = false;
        charging = false;
        embedEnemy = null; embedPart = null;
        if (heldId >= 0) heldId = -1;
        if (leg != null) leg.gameObject.SetActive(false);
        if (arc != null) arc.enabled = false;
        if (arcMarker != null) arcMarker.gameObject.SetActive(false);
        PlayerCamera.LookScale = 1f;
    }

    void OnGrabResult(int id, bool ok)
    {
        if (!waitingGrab) return;   // это ответ не мне (например, телекинез посоха)
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
        bool locked = Cursor.lockState == CursorLockMode.Locked;
        bool hasSword = tool == Tool.Sword;
        bool blocking = health != null && health.IsBlocking;
        bool canFight = tool != Tool.Hammer && mouse != null && kb != null && locked;

        if (!canFight || !hasSword || blocking || IsHolding) charging = false;

        if (canFight)
        {
            if (IsHolding)
            {
                if (!throwing)
                {
                    if (mouse.leftButton.wasPressedThisFrame || kb.gKey.wasPressedThisFrame) ThrowHeld(false);
                    else if (kb.qKey.wasPressedThisFrame) ThrowHeld(true);
                }
            }
            else
            {
                bool busy = swinging || IsStuck || attack != Attack.None;
                if (kb.qKey.wasPressedThisFrame && !busy) StartAttack(Attack.Kick);
                if (kb.gKey.wasPressedThisFrame && !busy) TryGrab();

                if (hasSword && !blocking)
                {
                    if (mouse.leftButton.wasPressedThisFrame && !IsStuck) { charging = true; pressTime = Time.time; chargeCaptured = false; hasChosen = false; chargeAcc = Vector2.zero; }
                    if (charging)
                    {
                        // куда дёрнул мышь, пока держишь, — оттуда и будет удар
                        chargeAcc = chargeAcc * Mathf.Exp(-12f * Time.unscaledDeltaTime) + mouse.delta.ReadValue();
                        if (Mathf.Abs(chargeAcc.x) > chargeFlick && Mathf.Abs(chargeAcc.x) > Mathf.Abs(chargeAcc.y))
                        { chosenDir = chargeAcc.x > 0f ? new Vector2(-1f, -0.15f).normalized : new Vector2(1f, -0.1f).normalized; hasChosen = true; chargeAcc = Vector2.zero; }
                        else if (chargeAcc.y > chargeFlick && chargeAcc.y > Mathf.Abs(chargeAcc.x))
                        { chosenDir = new Vector2(0.12f, -1f).normalized; hasChosen = true; chargeAcc = Vector2.zero; }
                    }
                    // меч уже проходит по инерции или возвращается — можно сразу бить снова
                    bool free = (!swinging || swP >= swF) && !IsStuck && attack == Attack.None;
                    if (charging && swinging && swP >= swF && Time.time - pressTime > 0.12f) { swinging = false; } // держишь — переходим в замах
                    if (charging && !mouse.leftButton.isPressed)
                    {
                        charging = false;
                        float power = Mathf.Clamp01((Time.time - pressTime - 0.1f) / (chargeTime - 0.1f));
                        if (free) BeginSwing(power);
                        else if (swinging) queued = true; // нажал во время удара — следующий удар серии сразу за ним
                    }
                }
                else if (!OwnWeapon(tool) && tool != Tool.Sword && mouse.leftButton.wasPressedThisFrame && attack == Attack.None)
                    StartAttack(Attack.Tool);

                // ПКМ — ударить луком, посохом или бластером. У лука ПКМ свободна — бьёт сразу.
                // У посоха (молния) и бластера (прицел) — короткое нажатие: бьёт, держишь — как раньше
                if (OwnWeapon(tool))
                {
                    if (mouse.rightButton.wasPressedThisFrame) rmbAt = Time.time;
                    bool tap = tool == Tool.Bow ? mouse.rightButton.wasPressedThisFrame
                                                : mouse.rightButton.wasReleasedThisFrame && Time.time - rmbAt < 0.2f;
                    if (tap && !busy) StartAttack(Attack.Bash);
                }
            }
        }

        // пока заносишь меч, камера крутится меньше — мышью выбираешь, откуда бить
        bool slowLook = charging && Time.time - pressTime > 0.1f;
        if (slowLook) PlayerCamera.LookScale = 0.4f;
        else if (chargeLook && !(health != null && health.IsBlocking)) PlayerCamera.LookScale = 1f;
        chargeLook = slowLook;

        UpdateHold();
        UpdateSlide();
        UpdateSwing();
        UpdateEmbed();
        UpdateAttack(tool);
        UpdateArc();
        Animate();
        if (swinging) SweepBlade(); // проверяем по тому, где клинок СЕЙЧАС нарисован
        else havePrev = false;
    }

    // =====================================================================
    // Подкат (Ctrl на бегу вперёд) — сбивает тварей с ног
    // =====================================================================
    void UpdateSlide()
    {
        if (movement == null || !movement.IsSliding) { tripped.Clear(); return; }
        Vector3 dir = movement.SlideDir;
        Vector3 at = transform.position + dir * 0.7f + Vector3.up * 0.4f;
        int n = Physics.OverlapSphereNonAlloc(at, 0.9f, slideHits, ~0, QueryTriggerInteraction.Collide);
        for (int i = 0; i < n; i++)
        {
            Enemy e = slideHits[i].GetComponentInParent<Enemy>();
            if (e == null || e.HealthFraction <= 0f || tripped.Contains(e)) continue;
            tripped.Add(e);
            BodyPartType leg = Vector3.Dot(e.transform.right, transform.position - e.transform.position) > 0f ? BodyPartType.LegR : BodyPartType.LegL;
            EnemyManager.RequestHit(e, new HitInfo { damage = kickDamage * 0.6f, dir = dir, part = leg, type = HitType.Trip });
            e.LocalReact((int)leg, dir, 1f);
            CombatFx.Play(CombatFx.Snd.Fall, e.transform.position, 1f);
            CombatFx.Hitstop(0.05f);
            PlayerCamera.Shake(0.25f);
        }
    }

    // =====================================================================
    // Меч
    // =====================================================================
    // Направление следующего удара серии: справа налево, слева направо, сверху
    Vector2 NextDir()
    {
        if (hasChosen) return chosenDir;
        int step = Time.time > comboUntil ? 0 : comboStep;
        Vector2 d = step == 0 ? new Vector2(-1f, -0.15f) : step == 1 ? new Vector2(1f, -0.1f) : new Vector2(-0.2f, -1f);
        return d.normalized;
    }

    // power: 0 — быстрый щелчок (короткая дуга), 1 — полный замах (широкая дуга, тяжёлый удар, клинок входит в тело)
    void BeginSwing(float power)
    {
        power = Mathf.Clamp01(power);
        float cost = Mathf.Lerp(swingStamina, heavyStamina, power);
        if (health != null && !health.UseStamina(cost, false)) { Say("Нет сил — передохни"); return; }

        if (Time.time > comboUntil) comboStep = 0;
        swingS = NextDir();
        hasChosen = false;
        bool third = comboStep == 2;
        comboStep = (comboStep + 1) % 3;

        swingPower = power;
        swingHeavy = power >= 0.99f;
        swDur = Mathf.Lerp(third ? comboEndTime : swingTime, heavyTime, power);
        swingDamage = Mathf.Lerp(third ? comboEndDamage : slashDamage, heavyDamage, power);

        // чем сильнее замах, тем шире дуга и дальше меч уходит по инерции
        swStart = -(60f + 70f * power);
        swEnd = 70f + 55f * power;
        swOver = 18f + 27f * power;
        swW = Mathf.Lerp(0.2f, 0.1f, power);   // с полного замаха меч уже отведён — сразу бьёт
        swS = 0.56f;
        swF = 0.76f;

        // удар начинается оттуда, где меч сейчас (после прошлого удара или из замаха) — без рывка в стойку
        if (swingModel != null) { swFromRot = swingModel.localRotation; swFromPos = swingModel.localPosition; }
        else { swFromRot = restRotation; swFromPos = restPosition; }

        swinging = true;
        swP = 0f;
        queued = false;
        havePrev = false;
        hitThisSwing.Clear();
        Lunge(Mathf.Lerp(0.12f, 0.4f, power));
        CombatFx.Play(CombatFx.Snd.Whoosh, transform.position + Vector3.up * 1.5f, Mathf.Lerp(0.45f, 0.9f, power), Mathf.Lerp(1.2f, 0.8f, power));
    }

    void UpdateSwing()
    {
        if (!swinging) return;
        if (queued && swP >= swF && !IsStuck) { BeginSwing(0f); return; } // следующий удар — прямо из прохода по инерции
        float rate = Time.time < dragUntil ? 0.22f : 1f; // в теле клинок идёт медленнее — чувствуется вес
        if (cutting)
        {
            // режет вглубь: каждую долю секунды всё труднее, пока не встанет совсем
            rate = 0.5f * Mathf.Exp(-(Time.time - cutStart) * 7f);
            if (rate < 0.04f || swP >= swS)
            {
                cutting = false;
                swinging = false;
                stuckAt = Time.time;
                if (swingModel != null) { stuckFromRot = swingModel.localRotation; stuckFromPos = swingModel.localPosition; }
                PlayerCamera.Punch(new Vector3(2f, 0f, 0f));
                return;
            }
        }
        swP += Time.deltaTime / Mathf.Max(0.05f, swDur) * rate;
        if (swP < 1f) return;
        swinging = false;
        swP = 1f;
        comboUntil = Time.time + 0.6f;
        if (queued && !IsStuck) BeginSwing(0f);
        queued = false;
    }

    bool ActiveNow() => swinging && swP > swW && swP < swS + 0.06f;

    void ModelBlade(Vector3[] pts)
    {
        for (int i = 0; i < pts.Length; i++)
            pts[i] = swingModel.TransformPoint(new Vector3(0f, Mathf.Lerp(bladeBaseY, bladeTipY, i / (pts.Length - 1f)), 0f));
    }

    // Клинок на экране прошёл путь от прошлого кадра до этого — во что он врезался
    void SweepBlade()
    {
        if (swingModel == null || hotbar == null || hotbar.Current != Tool.Sword || !ActiveNow() || cutting) { havePrev = false; return; }
        ModelBlade(curPts);
        bool stop = false;
        if (havePrev)
            for (int i = 0; i < BladeSamples && !stop; i++)
                stop = TestSegment(prevPts[i], curPts[i], curPts[i] - prevPts[i]);
        System.Array.Copy(curPts, prevPts, BladeSamples);
        havePrev = !stop && swinging;
    }

    // true — клинок остановился
    bool TestSegment(Vector3 a, Vector3 b, Vector3 motion)
    {
        Vector3 d = b - a;
        float len = d.magnitude;
        if (len < 0.0005f) return false;
        RaycastHit[] hits = Physics.RaycastAll(a, d / len, len, ~(1 << 2), QueryTriggerInteraction.Collide);
        System.Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
        foreach (RaycastHit h in hits)
        {
            if (h.transform.root == transform.root) continue;
            BodyPart bp = h.collider.GetComponentInParent<BodyPart>();
            if (bp != null)
            {
                if (!bp.enabled || bp.owner == null || hitThisSwing.Contains(bp.owner)) continue;
                if (Strike(bp, h.point, motion.normalized)) return true;
                continue;
            }
            if (h.collider.isTrigger) continue;
            if (h.collider.GetComponentInParent<Enemy>() != null) continue;
            if (h.collider.GetComponentInParent<PlayerMovement>() != null || h.collider.GetComponentInParent<NetworkPlayer>() != null) continue;
            Rigidbody body = h.collider.attachedRigidbody;
            if (body != null && !body.isKinematic) { body.AddForce(motion.normalized * 4f, ForceMode.VelocityChange); continue; }
            if (Mathf.Abs(h.normal.y) > 0.55f) continue;  // пол — скользит
            HitWall(h.point);
            return true;
        }
        return false;
    }

    // Клинок попал в часть тела. true — дальше не идёт
    bool Strike(BodyPart bp, Vector3 point, Vector3 motion)
    {
        Enemy e = bp.owner;
        hitThisSwing.Add(e);
        HitType type = swingS.y < -0.6f ? HitType.Chop : HitType.Slash;
        bool brute = e.kind == EnemyKind.Brute;
        bool bounce = brute && swingPower < 0.6f && (bp.type == BodyPartType.Torso || bp.type == BodyPartType.Head);
        bool embed = impale && swingPower >= 0.5f && !bounce;   // насаживание выключено: клинок прорезает и выходит

        EnemyManager.RequestHit(e, new HitInfo { damage = swingDamage, dir = motion, part = bp.type, type = type, heavy = swingHeavy, embed = embed && !brute });
        e.LocalReact((int)bp.type, motion, Mathf.Lerp(0.7f, 1.3f, swingPower));
        CombatFx.Ichor(point, motion, Mathf.RoundToInt(Mathf.Lerp(6f, 14f, swingPower)));
        CombatFx.Hitstop(Mathf.Lerp(0.035f, 0.08f, swingPower));
        PlayerCamera.Shake(Mathf.Lerp(0.12f, 0.35f, swingPower));
        // отдача в руку: клинок упёрся в тело — камеру дёргает навстречу удару
        PlayerCamera.Punch(new Vector3(-swingS.y * 2.5f, swingS.x * 2.5f, -swingS.x * 1.5f) * Mathf.Lerp(0.8f, 1.6f, swingPower));
        CombatFx.Play(swingPower > 0.6f ? CombatFx.Snd.FleshHeavy : CombatFx.Snd.Flesh, point, Mathf.Lerp(0.75f, 1f, swingPower), Mathf.Lerp(1.05f, 0.85f, swingPower));
        ShowPart(bp.type);

        // похоже, этот удар добивает — замедление и сильный толчок, как в кино
        float est = swingDamage * (bp.type == BodyPartType.Head ? 1.8f : 1f);
        if (e.HealthFraction > 0f && est >= e.HealthFraction * e.maxHealth)
        {
            PlayerCamera.Punch(new Vector3(-swingS.y * 4f, swingS.x * 4f, -swingS.x * 3f));
        }

        if (bounce) { CombatFx.Play(CombatFx.Snd.Thunk, point, 1f, 0.6f); Bounce("Отскок! Громилу — тяжёлым ударом (зажми ЛКМ) или по ногам"); return true; }
        if (embed)
        {
            // клинок входит в тело: дальше идёт всё медленнее и застревает (видно, как режет)
            embedEnemy = e;
            embedPart = bp;
            embedLocal = bp.transform.InverseTransformPoint(point + motion * 0.15f);
            cutting = true;
            cutStart = Time.time;
            queued = false;
            e.SlowMo(0.5f, 0.35f);        // замедляется только эта тварь — остальные нет
            return true;
        }
        // клинок продавливает тело и выходит с другой стороны: в туловище вязнет дольше, в руке/ноге — чуть,
        // чем сильнее замах, тем дольше режет. Тварь тяжёлая — клинок не пролетает сквозь неё, как сквозь нитку
        bool thick = bp.type == BodyPartType.Torso || bp.type == BodyPartType.Head || brute;
        dragUntil = Time.time + (thick ? Mathf.Lerp(0.13f, 0.22f, swingPower) : Mathf.Lerp(0.06f, 0.1f, swingPower)) * (brute ? 1.4f : 1f);
        PlayerCamera.Shake(thick ? 0.15f : 0.08f);
        return false;
    }

    void UpdateEmbed()
    {
        if (!IsStuck) return;
        bool gone = embedPart == null || embedPart.lost || !embedPart.enabled || embedEnemy == null || embedEnemy.HealthFraction <= 0f;
        if (gone) { SlipOut(); return; }
        // тварь отлетела (её отбросило, она уже летела, умерла у другого игрока) — клинок не может остаться
        // «в ней» на расстоянии: выскальзывает и возвращается в руку, а не висит в воздухе
        float far = Vector3.Distance(pivot.position, embedPart.transform.TransformPoint(embedLocal));
        bool flewOff = far > bladeTipY + 1.0f;
        // клинок вошёл, но тварь так и не насадилась на него (летела, громила, была в руках) — тоже выходит
        bool notHeld = Time.time - cutStart > 0.35f && !embedEnemy.OnBlade && embedEnemy.kind != EnemyKind.Brute;
        if (flewOff || notHeld) { SlipOut(); return; }
        if (cutting) return;
        // застрял: держишь ЛКМ — клинок остаётся внутри (и тварь на нём), отпустил — тебя вытаскивает, клинок выходит
        Mouse m = Mouse.current;
        bool held = m != null && m.leftButton.isPressed;
        float t = Time.time - stuckAt;
        if ((!held && t > 0.3f) || t > 3f) RipOut();
    }

    void FreeBlade()
    {
        cutting = false;
        if (swinging && swP < swF) swinging = false;
        embedEnemy = null;
        embedPart = null;
        havePrev = false;
    }

    // Клинок выскользнул сам: меч плавно возвращается в руку из того места, где был, без рывка
    void SlipOut()
    {
        FreeBlade();
        swinging = false;
        queued = false;
        if (attack != Attack.None || swingModel == null) return;
        recoilFrom = swingModel.localRotation; recoilFromPos = swingModel.localPosition;
        attack = Attack.Recoil;
        attackStart = Time.time;
        attackDuration = 0.25f;
        hitAt = 99f;
        hitDone = true;
    }

    void RipOut()
    {
        Enemy e = embedEnemy;
        BodyPart bp = embedPart;
        Vector3 p = bp != null ? bp.transform.TransformPoint(embedLocal) : Vector3.zero;
        FreeBlade();
        if (e == null || bp == null) return;
        Vector3 back = -FlatForward();
        EnemyManager.RequestHit(e, new HitInfo { damage = ripDamage, dir = back, part = bp.type, type = HitType.RipOut });
        e.LocalReact((int)bp.type, back, 0.8f);
        CombatFx.Ichor(p, back, 14);
        CombatFx.Play(CombatFx.Snd.FleshHeavy, p, 0.9f, 1.2f);
        PlayerCamera.Shake(0.2f);
        if (swingModel != null) { recoilFrom = swingModel.localRotation; recoilFromPos = swingModel.localPosition; }
        StartAttack(Attack.Rip);
    }

    void ShowPart(BodyPartType t)
    {
        switch (t)
        {
            case BodyPartType.Head: partText = "ГОЛОВА ×1.8"; break;
            case BodyPartType.ArmL: case BodyPartType.ArmR: partText = "рука"; break;
            case BodyPartType.LegL: case BodyPartType.LegR: partText = "нога — хромает"; break;
            case BodyPartType.Sack: partText = "МЕШОК!"; break;
            default: partText = "тело"; break;
        }
        partTextUntil = Time.time + 0.8f;
    }

    void HitWall(Vector3 point)
    {
        CombatFx.Sparks(point, Color.white, 8);
        CombatFx.Hitstop(0.04f);
        CombatFx.Play(CombatFx.Snd.Clang, point, 0.6f, 0.7f);
        Bounce(null);
    }

    // Клинок отскочил
    void Bounce(string text)
    {
        if (swingModel != null) { recoilFrom = swingModel.localRotation; recoilFromPos = swingModel.localPosition; }
        swinging = false;
        queued = false;
        havePrev = false;
        attack = Attack.Recoil;
        attackStart = Time.time;
        attackDuration = 0.32f;
        hitAt = 99f;
        hitDone = true;
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
        float cost = a == Attack.Kick ? kickStamina : a == Attack.Tool ? toolStamina : a == Attack.Bash ? bashStamina : 0f;
        if (health != null && cost > 0f && !health.UseStamina(cost, false)) { Say("Нет сил — передохни"); return; }

        attack = a;
        attackStart = Time.time;
        hitDone = false;
        switch (a)
        {
            case Attack.Kick: attackDuration = 0.45f; hitAt = 0.14f; break;
            case Attack.Tool: attackDuration = 0.45f; hitAt = 0.15f; break;
            case Attack.Bash:
                attackDuration = hotbar != null && hotbar.Current == Tool.Staff ? 0.6f : 0.5f;
                hitAt = attackDuration * 0.45f;
                CombatFx.Play(CombatFx.Snd.Whoosh, transform.position + Vector3.up * 1.5f, 0.6f, 0.9f);
                Lunge(0.25f);
                break;
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
                CombatFx.Play(CombatFx.Snd.Hurt, e.transform.position + Vector3.up, 1f, 1.1f);
                PlayerCamera.Punch(new Vector3(3f, 0f, 0f));
                e.LocalReact((int)BodyPartType.Torso, fwd, 1.3f);
                CombatFx.Hitstop(0.05f);
                PlayerCamera.Shake(0.25f);
                break;
            }
            case Attack.Bash:
            {
                FindTargets(range - 0.5f, 60f, 1);
                if (targets.Count == 0) break;
                Enemy e = targets[0];
                // лук — хлёсткий удар плечом лука сбоку, бластер — рукоятью сверху, посох — тяжёлый удар по голове
                bool staffHit = tool == Tool.Staff;
                HitInfo hi = staffHit
                    ? new HitInfo { damage = 14f, dir = fwd, part = BodyPartType.Head, type = HitType.Chop }
                    : new HitInfo { damage = tool == Tool.Blaster ? 10f : 8f, dir = fwd, part = tool == Tool.Blaster ? BodyPartType.Head : BodyPartType.Torso, type = HitType.Tool };
                EnemyManager.RequestHit(e, hi);
                e.LocalReact((int)hi.part, fwd, staffHit ? 1.2f : 0.9f);
                CombatFx.Play(staffHit ? CombatFx.Snd.FleshHeavy : CombatFx.Snd.Thunk, e.transform.position + Vector3.up * 1.4f * e.bodyScale, 0.9f, staffHit ? 0.8f : 1.1f);
                PlayerCamera.Shake(staffHit ? 0.3f : 0.2f);
                PlayerCamera.Punch(staffHit ? new Vector3(4f, 0f, 0f) : new Vector3(1f, -3f, 2f));
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
        return f * throwSpeed * 0.85f + Vector3.up * 2.5f;   // бросок одной рукой — чуть слабее
    }

    // Бросок — не сразу: герой разворачивается, заносит тварь и швыряет её всем телом
    void ThrowHeld(bool overShoulder)
    {
        if (throwing || heldId < 0) return;
        throwing = true;
        throwOver = overShoulder;
        throwStart = Time.time;
        CombatFx.Play(CombatFx.Snd.Whoosh, transform.position + Vector3.up, 0.6f, 0.7f);
    }

    void ReleaseThrow()
    {
        throwing = false;
        Enemy e = EnemyManager.Instance != null ? EnemyManager.Instance.Find(heldId) : null;
        int id = heldId;
        heldId = -1;
        LocalHoldId = -1;
        if (e == null) return;
        if (throwOver)
        {
            // через себя: дугой над головой и со всей силы об землю за спиной
            EnemyManager.RequestHit(e, new HitInfo { damage = 20f, dir = Vector3.down, part = BodyPartType.Torso, type = HitType.Slash });
            EnemyManager.RequestThrow(id, -transform.forward * 3f + Vector3.down * 9f, 8f);
            PlayerCamera.Shake(0.4f);
            PlayerCamera.Punch(new Vector3(6f, 0f, 0f));
            CombatFx.Play(CombatFx.Snd.Fall, e.transform.position, 1f, 0.8f);
        }
        else
        {
            EnemyManager.RequestThrow(id, ThrowVelocity(), 2f);
            PlayerCamera.Shake(0.2f);
            PlayerCamera.Punch(new Vector3(-2f, -3f, 2f));
        }
        StartAttack(Attack.Throw);
    }

    // Где тварь в руках (позиция её ног). Держу у груди, ноги над землёй; при броске — по дуге
    public static int LocalHoldId { get; private set; } = -1;
    public static Vector3 LocalHoldPoint { get; private set; }
    bool throwing, throwOver;
    float throwStart, holdSend;
    Vector3 gripFix;          // поправка: тварь подвешена так, чтобы кисть была ровно на её загривке

    void UpdateHold()
    {
        if (heldId < 0) { poleHold = false; gripFix = Vector3.zero; LocalHoldId = -1; throwing = false; if (PlayerBody.Local != null) { PlayerBody.Local.HoldTarget = null; PlayerBody.Local.HoldTargetR = null; PlayerBody.Local.Twist = 0f; } return; }
        Enemy e = EnemyManager.Instance != null ? EnemyManager.Instance.Find(heldId) : null;
        if (e == null) return;
        float sc = e.bodyScale;
        poleHold = hotbar != null && (hotbar.Current == Tool.Bow || hotbar.Current == Tool.Staff);
        // Держу ОДНОЙ (левой) рукой за шиворот — в правой остаётся оружие.
        // Сначала решаю, где моя кисть, а тварь подвешиваю под ней: кисть всегда на твари.
        float neck = 1.25f * sc;                                   // от ног твари до её шеи
        float hy = Mathf.Max(0.3f, -0.97f + neck);                 // крупную поднимаю выше, чтобы ноги не волочились
        Vector3 hold = new Vector3(poleHold ? 0f : -0.2f, hy, poleHold ? 0.55f : 0.5f);   // кисть: впереди на уровне груди (с древком — по центру)
        Vector3 hand = hold;
        float twist = 0f;
        float t = Time.time;
        bool over = false;
        Vector3 overFeet = Vector3.zero;
        if (throwing)
        {
            float dur = throwOver ? 0.6f : 0.45f;
            float u = Mathf.Clamp01((t - throwStart) / dur);
            if (!throwOver)
            {
                // замах влево-назад одной рукой (корпус разворачивается), потом рывок вперёд-вверх
                Vector3 back = new Vector3(-0.75f, hy - 0.1f, -0.1f), release = new Vector3(-0.05f, hy + 0.2f, 0.8f);
                if (u < 0.45f) { float w = Smooth(u / 0.45f); hand = Vector3.Lerp(hold, back, w); twist = 45f * w; }
                else { float w = (u - 0.45f) / 0.55f; w = w * w; hand = Vector3.Lerp(back, release, w) + Vector3.up * Mathf.Sin(w * Mathf.PI) * 0.25f; twist = Mathf.Lerp(45f, -40f, w); }
                PlayerCamera.Sway(new Vector3(-2f * Mathf.Sin(u * Mathf.PI), -twist * 0.07f, twist * 0.04f));
            }
            else
            {
                // через голову: поднимаю над собой левой рукой и обрушиваю за спину
                float a = Smooth(u) * Mathf.PI;                       // 0 — передо мной, π — за спиной
                over = true;
                overFeet = new Vector3(-0.25f, -0.75f + Mathf.Sin(a) * 1.9f, Mathf.Cos(a) * (0.75f + 0.4f * sc));
                // древком за шею: тяну её на себя и вверх, через плечо — руки идут вместе с ней
                if (poleHold) hand = hold + new Vector3(0f, Mathf.Sin(a) * 0.45f, -0.35f * (1f - Mathf.Cos(a)) * 0.5f);
                PlayerCamera.Sway(new Vector3(-7f * Mathf.Sin(a), 0f, 0f) + new Vector3(5f, 0f, 0f) * Mathf.Clamp01(u * 3f - 2f));
            }
        }
        else
        {
            // тварь бьётся, рука ходит вместе с ней
            hand += new Vector3(Mathf.Sin(t * 13f) * 0.03f, Mathf.Sin(t * 9f) * 0.025f, 0f);
        }
        // ноги твари — под кистью; тело чуть дальше кисти (держу за ворот спереди)
        Vector3 feet = over ? overFeet : hand + Vector3.forward * 0.22f * sc - Vector3.up * neck;
        // где загривок твари на самом деле (она бьётся, откинута назад) — сдвигаю её, пока он не окажется в кисти
        Vector3 realGrip = transform.InverseTransformPoint(e.GripPoint);
        Vector3 aim = over ? realGrip : hand;
        if (!over)
        {
            gripFix += (aim - realGrip) * (1f - Mathf.Exp(-12f * Time.deltaTime));
            gripFix = Vector3.ClampMagnitude(gripFix, 0.8f);
        }
        feet += gripFix;
        if (throwing && Mathf.Clamp01((t - throwStart) / (throwOver ? 0.6f : 0.45f)) >= 1f) { SetHold(e, transform.TransformPoint(feet)); ReleaseThrow(); return; }
        SetHold(e, transform.TransformPoint(feet));
        if (PlayerBody.Local != null)
        {
            // кисть тянется к тому месту твари, где она реально сейчас (тварь догоняет точку с небольшим запозданием)
            if (poleHold)
            {
                // древко лежит поперёк, за шеей твари; руки — на его концах
                Vector3 c = transform.TransformPoint(hand);
                Vector3 across = transform.right;
                Vector3 fwd = transform.forward;
                if (over) { c = e.GripPoint; }
                polePos = c + fwd * 0.12f * sc;          // чуть дальше загривка — за шеей
                poleRot = Quaternion.LookRotation(fwd, across);   // длинная ось оружия — поперёк
                PlayerBody.Local.HoldTarget = polePos - across * 0.28f;
                PlayerBody.Local.HoldTargetR = polePos + across * 0.28f;
            }
            else
            {
                PlayerBody.Local.HoldTarget = e.GripPoint;   // кисть — на загривке твари, где он реально сейчас
                PlayerBody.Local.HoldTargetR = null;
            }
            PlayerBody.Local.Twist = twist;
        }
    }

    void SetHold(Enemy e, Vector3 p)
    {
        LocalHoldId = heldId;
        LocalHoldPoint = p;
        if (Net.IsHost || Time.time >= holdSend)
        {
            holdSend = Time.time + 0.05f;
            EnemyManager.TkMove(heldId, p);
        }
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
    // Анимация оружия в руке (позы — в осях головы)
    // =====================================================================
    static Quaternion BladeRotation(Vector3 dl)
    {
        Vector3 hint = Mathf.Abs(Vector3.Dot(dl, Vector3.up)) > 0.95f ? Vector3.back : Vector3.up;
        return Quaternion.LookRotation(dl, hint) * Quaternion.Euler(90f, 0f, 0f); // ось клинка (вверх у модели) — вдоль dl
    }

    // Поворот клинка на дуге удара: лезвие всегда идёт вперёд по ходу удара и не прокручивается вокруг себя
    static Quaternion ArcRotation(Vector2 s, Vector3 dl)
    {
        Vector3 n = Vector3.Cross(new Vector3(s.x, s.y, 0f), Vector3.forward).normalized;
        if (n.y < 0f) n = -n;
        return Quaternion.LookRotation(dl, n) * Quaternion.Euler(90f, 0f, 0f);
    }

    // Поза на дуге удара: th — угол на дуге (минус — замах, плюс — конец удара), amp — размах 0..1
    static void ArcPose(Vector2 s, float thDeg, float amp, out Vector3 dl, out Vector3 hand)
    {
        // за 90° дуга сжимается: рука не может завести клинок себе за спину и через своё тело
        if (Mathf.Abs(thDeg) > 90f) thDeg = Mathf.Sign(thDeg) * (90f + (Mathf.Abs(thDeg) - 90f) * 0.35f);
        float th = thDeg * Mathf.Deg2Rad;
        Vector3 side = new Vector3(s.x, s.y, 0f);
        // клинок никогда не смотрит назад, в тебя: на краях дуги он уходит вбок, а не за спину
        float fw = Mathf.Max(Mathf.Cos(th), -0.1f);
        dl = (Vector3.forward * fw + side * Mathf.Sin(th) + Vector3.forward * 0.45f).normalized;
        hand = new Vector3(0.15f, -0.3f, 0.3f) + side * (0.25f + 0.17f * amp) * Mathf.Sin(th) + Vector3.forward * (0.18f + 0.14f * amp) * Mathf.Max(Mathf.Cos(th), -0.25f);   // в середине удара рука выносится вперёд — выпад
    }

    static float Smooth(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }

    // Удар: замах — разгон через весь экран — меч уходит по инерции дальше — возврат
    void SwingPose(out Quaternion rot, out Vector3 pos)
    {
        float a = swingPower;
        float p = swP;
        if (p < swW)
        {
            float w = Smooth(p / swW);
            ArcPose(swingS, swStart, a, out Vector3 d0, out Vector3 h0);
            rot = Quaternion.Slerp(swFromRot, ArcRotation(swingS, d0), w); pos = Vector3.Lerp(swFromPos, h0, w);
        }
        else if (p < swS)
        {
            // разгоняется (сначала медленно, потом быстро) — чувствуется вес
            float q = (p - swW) / (swS - swW);
            q = q * q * (2.2f - 1.2f * q);
            ArcPose(swingS, Mathf.Lerp(swStart, swEnd, q), a, out Vector3 dl, out Vector3 hand);
            rot = ArcRotation(swingS, dl); pos = hand;
        }
        else if (p < swF)
        {
            // по инерции: меч уходит дальше и тормозит
            float u = (p - swS) / (swF - swS);
            u = 1f - (1f - u) * (1f - u);
            ArcPose(swingS, swEnd + swOver * u, a, out Vector3 dl, out Vector3 hand);
            rot = ArcRotation(swingS, dl); pos = hand;
        }
        else
        {
            ArcPose(swingS, swEnd + swOver, a, out Vector3 d1, out Vector3 h1);
            float w = Smooth((p - swF) / (1f - swF));
            rot = Quaternion.Slerp(ArcRotation(swingS, d1), restRotation, w); pos = Vector3.Lerp(h1, restPosition, w);
        }
    }

    void Animate()
    {
        if (swingModel == null) return;
        if (hotbar != null && OwnWeapon(hotbar.Current)) return; // лук, посох и бластер двигают свои скрипты
        float udt = Time.unscaledDeltaTime;
        Quaternion rot = restRotation;
        Vector3 pos = restPosition;
        float now = Time.time;
        bool hasSword = hotbar == null || hotbar.Current == Tool.Sword;

        if (attack == Attack.Recoil || attack == Attack.Rip)
        {
            float p = Mathf.Clamp01((now - attackStart) / attackDuration);
            rot = Quaternion.Slerp(recoilFrom, restRotation, Smooth(p)) * Quaternion.Euler(-25f * Mathf.Sin(p * Mathf.PI), 0f, 0f);
            pos = Vector3.Lerp(recoilFromPos, restPosition, Smooth(p)) + Vector3.back * 0.15f * Mathf.Sin(p * Mathf.PI);
        }
        else if (hasSword && IsStuck && embedPart != null && !cutting)
        {
            // клинок застрял: остриё в теле, держишь — уходит глубже и дрожит от напряжения
            float st = now - stuckAt;
            float depth = 0.25f + 0.2f * Smooth(st / 1.2f);
            Vector3 target = pivot.InverseTransformPoint(embedPart.transform.TransformPoint(embedLocal));
            Vector3 handRest = new Vector3(0.15f, -0.3f, 0.35f);
            Vector3 dl = (target - handRest).normalized;
            Vector3 hand = target + dl * (depth - bladeTipY);
            Vector3 off = hand - handRest;
            if (off.magnitude > 0.7f) hand = handRest + off.normalized * 0.7f;
            float strain = 1f + st * 1.5f;
            Quaternion aim = BladeRotation(dl) * Quaternion.Euler(Mathf.Sin(now * 37f) * 1.2f * strain, 0f, Mathf.Cos(now * 29f) * 0.8f * strain);
            float w = Smooth(st / 0.15f);                // плавно из позы удара, без рывка
            rot = Quaternion.Slerp(stuckFromRot, aim, w);
            pos = Vector3.Lerp(stuckFromPos, hand, w);
        }
        else if (hasSword && swinging)
        {
            SwingPose(out rot, out pos);
        }
        else if (hasSword && charging && Time.time - pressTime > 0.08f)
        {
            // замах над головой для тяжёлого удара; заряжен — дрожит
            // отводим меч в сторону будущего удара: чем дольше держишь, тем дальше замах
            if (!chargeCaptured) { chargeCaptured = true; chargeRot = swingModel.localRotation; chargePos = swingModel.localPosition; }
            float raw = Mathf.Clamp01((Time.time - pressTime - 0.1f) / (chargeTime - 0.1f));
            float c = Smooth(raw);
            ArcPose(NextDir(), -(60f + 70f * c), c, out Vector3 dl, out Vector3 hand);
            // меч плавно идёт к замаху (и перетекает, если дёрнул мышь в другую сторону)
            float k = 1f - Mathf.Exp(-16f * udt);
            chargeRot = Quaternion.Slerp(chargeRot, ArcRotation(NextDir(), dl), k);
            chargePos = Vector3.Lerp(chargePos, hand, k);
            Vector3 shake = raw >= 0.99f ? Random.insideUnitSphere * 0.004f : Vector3.zero;
            rot = chargeRot;
            pos = chargePos + shake;
        }
        else if (attack == Attack.Kick || attack == Attack.Tool || attack == Attack.Throw)
        {
            float p = Mathf.Clamp01((now - attackStart) / attackDuration);
            float hitP = Mathf.Max(0.01f, hitAt / attackDuration);
            float strike = Smooth(p / (hitP * 1.4f));
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
            rot = restRotation * Quaternion.Euler(30f, 0f, -20f);  // левая рука занята тварью — меч опущен в правой
            pos = restPosition + new Vector3(0.05f, -0.12f, -0.05f);
        }
        else if (toolSwing > 0f)
        {
            toolSwing = Mathf.MoveTowards(toolSwing, 0f, Time.deltaTime / 0.45f);
            float a = Mathf.Sin(toolSwing * Mathf.PI) * 80f;
            rot = restRotation * Quaternion.Euler(a, -a * 0.4f, 0f);
        }

        // Пинок — своей ногой (тело героя)
        if (PlayerBody.Local != null)
            PlayerBody.Local.KickP = attack == Attack.Kick ? Mathf.Clamp01((now - attackStart) / attackDuration) : -1f;

        // Блок: меч поперёк перед лицом. Удар в блок — меч дёргается
        bool blocking = hasSword && health != null && health.IsBlocking;
        blockBlend = Mathf.MoveTowards(blockBlend, blocking ? 1f : 0f, udt * 10f);
        if (blockBlend > 0f)
        {
            float jolt = health != null ? health.BlockJolt : 0f;
            // меч ставится туда, откуда ждёшь удар: сверху — поперёк над глазами, слева/справа — стоймя сбоку
            int g = health != null ? health.GuardSide : 0;
            Vector3 wantBl = g == 0 ? new Vector3(-1f, 0.15f, 0.3f) : g > 0 ? new Vector3(0.35f, 1f, 0.3f) : new Vector3(-0.35f, 1f, 0.3f);
            Vector3 wantPos = g == 0 ? new Vector3(0.5f, 0.12f, 0.55f) : g > 0 ? new Vector3(0.38f, -0.38f, 0.5f) : new Vector3(-0.16f, -0.38f, 0.5f);
            if (blockBlend < 0.05f) { guardBl = wantBl; guardPos = wantPos; }
            float gk = 1f - Mathf.Exp(-20f * udt);
            guardBl = Vector3.Lerp(guardBl, wantBl, gk);
            guardPos = Vector3.Lerp(guardPos, wantPos, gk);
            Vector3 bl = guardBl.normalized;
            Quaternion br = BladeRotation(bl) * Quaternion.Euler(-jolt * 12f, 0f, 0f);
            Vector3 joltDir = g == 0 ? Vector3.down : g > 0 ? Vector3.left : Vector3.right; // удар вдавливает меч к тебе
            Vector3 bpos = guardPos + (joltDir * 0.05f + Vector3.back * 0.08f) * jolt;
            float w = Smooth(blockBlend);
            rot = Quaternion.Slerp(rot, br, w);
            pos = Vector3.Lerp(pos, bpos, w);
        }

        // от третьего лица меч не дальше длины руки от плеча героя: рукоять всегда в кисти,
        // а не висит перед головой (бег, наклон, захват — меч идёт вместе с рукой)
        if (pcam != null && pcam.IsThirdPerson && PlayerBody.Local != null && swingModel.parent != null)
        {
            Vector3 sh = PlayerBody.Local.RightShoulder;
            // замах и удар идут всей рукой: движение кисти шире (вокруг плеча), а не только поворот клинка
            if (hasSword) { Vector3 arcC = new Vector3(0.15f, -0.3f, 0.3f); pos = arcC + (pos - arcC) * 1.6f; }
            Vector3 toGrip = swingModel.parent.TransformPoint(pos) - sh;
            float armReach = PlayerBody.ArmReach * 0.97f;
            if (toGrip.magnitude > armReach) pos = swingModel.parent.InverseTransformPoint(sh + toGrip.normalized * armReach);
        }
        swingModel.localRotation = rot;
        swingModel.localPosition = pos;

        // глаза идут за мечом: замахнулся вверх-вправо — и взгляд чуть туда, ударил — проводил
        if (hasSword && (swinging || (charging && Time.time - pressTime > 0.08f) || IsStuck) && blockBlend < 0.5f)
        {
            Vector3 d = pos - new Vector3(0.15f, -0.3f, 0.3f);   // от центра дуги удара
            PlayerCamera.Sway(new Vector3(-d.y * 24f, d.x * 22f, -d.x * 14f));   // заметно: глаза и голова идут за клинком
        }
    }

    float blockBlend;
    Vector3 guardBl = Vector3.left, guardPos;

    void Say(string text) { message = text; messageUntil = Time.time + 1.5f; }

    // =====================================================================
    // Экран
    // =====================================================================
    void OnGUI()
    {
        float cx = Screen.width / 2f, cy = Screen.height / 2f;
        if (hotbar == null || hotbar.Current != Tool.Bow && hotbar.Current != Tool.Blaster) GUI.Label(new Rect(cx - 6, cy - 12, 20, 24), "+", Hud.Big); // у лука свой прицел

        if (Hud.Popups && Time.time < partTextUntil)
        {
            Color old = GUI.color;
            GUI.color = new Color(1f, 0.9f, 0.6f, Mathf.Clamp01((partTextUntil - Time.time) / 0.3f));
            GUI.Label(new Rect(cx + 26, cy - 4, 300, 22), partText, Hud.Small);
            GUI.color = old;
        }

        if (IsHolding)
        {
            GUI.Label(new Rect(0, Screen.height - 88, Screen.width, 22), "Держишь тварь:  целься — ЛКМ / G бросить   Q — через себя", Hud.CenterTiny);
            if (Hud.Popups && arcText != null)
            {
                Color old = GUI.color; GUI.color = new Color(1f, 0.75f, 0.3f);
                GUI.Label(new Rect(0, cy + 84, Screen.width, 24), arcText, Hud.Center);
                GUI.color = old;
            }
        }
        else if (hotbar != null && hotbar.Current != Tool.Hammer && !OwnWeapon(hotbar.Current) && lookedNode == null)
            GUI.Label(new Rect(0, Screen.height - 88, Screen.width, 22),
                      hotbar.Current == Tool.Sword
                          ? "ЛКМ — удар   держи ЛКМ — замах (дёрни мышь вверх — удар сверху, вбок — сбоку)   застрял — держи ЛКМ, отпусти — вытащишь   ПКМ — блок   Q — пинок   Ctrl на бегу — подкат"
                          : "ЛКМ — удар   ПКМ — блок только мечом   Q — пинок   G — схватить   Ctrl — перекат", Hud.CenterTiny);

        // Зарядка тяжёлого удара
        if (charging && Time.time - pressTime > 0.08f)
        {
            float c = Mathf.Clamp01((Time.time - pressTime - 0.1f) / (chargeTime - 0.1f));
            Hud.Bar(new Rect(cx - 40, cy + 18, 80, 4), c, c >= 1f ? new Color(1f, 0.6f, 0.2f) : Color.white, null);
        }

        if (lookedNode != null)
        {
            Rect r = new Rect(cx - 100, cy + 40, 200, 10);
            Hud.Bar(r, (float)lookedNode.HitsLeft / lookedNode.maxHits, new Color(0.9f, 0.75f, 0.3f),
                    lookedNode.displayName + "  —  " + PlayerHotbar.NameOf(lookedNode.requiredTool).ToLower());
        }
        if (Hud.Popups && Time.time < messageUntil)
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
    // Всплывающие надписи по центру («В ГОЛОВУ», «ПОДСЕЧКА!», «нельзя…»). Пока тестим бой — выключены.
    public static bool Popups = false;
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
