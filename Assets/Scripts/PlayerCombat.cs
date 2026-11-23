using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Бой. Всё, что в руке (кроме молота), умеет бить, пинать и хватать.
//
//   ЛКМ (щелчок)          — удар. Три щелчка подряд — комбо: справа, слева, сверху (третий отбрасывает).
//   ЛКМ (зажать и отпустить) — тяжёлый удар сверху: сбивает с ног, тварь «открыта».
//   ЛКМ по «открытой» твари — ДОБИВАНИЕ: подбрасываешь, она переворачивается в воздухе и бьётся о землю.
//   Q                     — пинок: тварь отлетает (в стену, в колья, с моста, в пропасть...).
//   G                     — схватить тварь (громилу — только «открытого»). Пока держишь:
//                              ЛКМ или G — бросить вперёд, Q — бросок через себя об землю.
//   ПКМ (с мечом)         — блок; поставь в последний миг — парирование (см. PlayerHealth).
//   Ctrl / C              — перекат (см. PlayerMovement).
// Топор и кирка ещё рубят деревья и бьют камни.
public class PlayerCombat : MonoBehaviour
{
    public float range = 2.8f;
    public float pushForce = 6f;
    public float swordDamage = 18f;
    public float comboEndDamage = 30f;
    public float heavyDamage = 45f;
    public float finisherDamage = 80f;
    public float kickDamage = 8f;
    public float otherToolDamage = 12f;
    public float swordStamina = 10f;
    public float heavyStamina = 25f;
    public float kickStamina = 14f;
    public float grabStamina = 15f;
    public float toolStamina = 6f;
    public float chargeTime = 0.35f;      // сколько держать ЛКМ для тяжёлого удара

    enum Attack { None, Light1, Light2, Light3, Heavy, Kick, Finisher, Tool, Throw }

    Camera cam;
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

    Attack attack = Attack.None;
    float attackStart, attackDuration, hitAt;
    bool hitDone;
    int comboStep;           // 0..2
    float comboUntil;
    bool queuedLight;
    float pressTime = -1f;
    bool charging;
    float toolSwing;         // старая анимация (молот)
    int heldId = -1;
    float grabTime;
    bool waitingGrab;
    readonly List<Enemy> targets = new List<Enemy>();

    public bool IsAttacking => attack != Attack.None;
    public bool IsHolding => heldId >= 0;

    public void PlaySwing() { toolSwing = 1f; }

    void Start()
    {
        cam = Camera.main;
        hotbar = GetComponent<PlayerHotbar>();
        health = GetComponent<PlayerHealth>();
        controller = GetComponent<CharacterController>();
        EnemyManager.GrabResult += OnGrabResult;

        // Нога для пинка (видна только во время пинка)
        PlayerCamera pc = GetComponentInChildren<PlayerCamera>();
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

    void OnDestroy() { EnemyManager.GrabResult -= OnGrabResult; }

    void OnDisable()
    {
        attack = Attack.None;
        charging = false;
        if (heldId >= 0) heldId = -1; // хост сам отпустит, когда никто не держит
        if (leg != null) leg.gameObject.SetActive(false);
    }

    void OnGrabResult(int id, bool ok)
    {
        waitingGrab = false;
        if (ok) { heldId = id; grabTime = Time.time; Say("Схватил! ЛКМ — бросить, Q — через себя"); }
        else Say("Не вырваться... то есть не схватить: слишком тяжёлый");
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
        bool canFight = tool != Tool.Hammer && mouse != null && kb != null && locked && (health == null || !health.IsBlocking);

        if (canFight)
        {
            if (IsHolding)
            {
                // Держим тварь
                if (mouse.leftButton.wasPressedThisFrame || kb.gKey.wasPressedThisFrame) ThrowHeld(false);
                else if (kb.qKey.wasPressedThisFrame) ThrowHeld(true);
            }
            else
            {
                if (kb.qKey.wasPressedThisFrame && attack == Attack.None) StartAttack(Attack.Kick);
                if (kb.gKey.wasPressedThisFrame && attack == Attack.None) TryGrab();

                if (tool == Tool.Sword)
                {
                    if (mouse.leftButton.wasPressedThisFrame) { pressTime = Time.time; charging = true; }
                    if (charging && !mouse.leftButton.isPressed)
                    {
                        charging = false;
                        bool heavy = Time.time - pressTime >= chargeTime;
                        if (heavy) { if (attack == Attack.None) StartAttack(Attack.Heavy); }
                        else if (attack == Attack.None) StartLightOrFinisher();
                        else queuedLight = true; // нажал во время удара — следующий удар комбо
                    }
                }
                else if (mouse.leftButton.wasPressedThisFrame && attack == Attack.None) StartAttack(Attack.Tool);
            }
        }
        else charging = false;

        UpdateAttack(tool);
        Animate();
    }

    // ---------- Атаки ----------
    void StartLightOrFinisher()
    {
        // «Открытая» тварь перед нами — добиваем
        FindTargets(range + 0.4f, 60f, 3);
        foreach (Enemy e in targets)
            if (e.IsOpen) { StartAttack(Attack.Finisher); return; }

        if (Time.time > comboUntil) comboStep = 0;
        StartAttack(comboStep == 0 ? Attack.Light1 : comboStep == 1 ? Attack.Light2 : Attack.Light3);
    }

    void StartAttack(Attack a)
    {
        float cost = a == Attack.Heavy ? heavyStamina : a == Attack.Kick ? kickStamina : a == Attack.Tool ? toolStamina :
                     a == Attack.Finisher ? 0f : swordStamina;
        if (health != null && cost > 0f && !health.UseStamina(cost, false)) { Say("Нет сил — передохни"); return; }

        attack = a;
        attackStart = Time.time;
        hitDone = false;
        queuedLight = false;
        switch (a)
        {
            case Attack.Light1: case Attack.Light2: attackDuration = 0.36f; hitAt = 0.11f; break;
            case Attack.Light3: attackDuration = 0.5f; hitAt = 0.17f; break;
            case Attack.Heavy: attackDuration = 0.65f; hitAt = 0.2f; break;
            case Attack.Kick: attackDuration = 0.45f; hitAt = 0.14f; break;
            case Attack.Finisher: attackDuration = 0.75f; hitAt = 0.22f; break;
            case Attack.Tool: attackDuration = 0.45f; hitAt = 0.15f; break;
            case Attack.Throw: attackDuration = 0.4f; hitAt = 0.1f; hitDone = true; break;
        }
        if (leg != null) leg.gameObject.SetActive(a == Attack.Kick);
    }

    void UpdateAttack(Tool tool)
    {
        if (attack == Attack.None) return;
        float t = Time.time - attackStart;
        if (!hitDone && t >= hitAt) { hitDone = true; DoHit(tool); }
        if (t < attackDuration) return;

        // Удар закончился
        bool wasLight = attack == Attack.Light1 || attack == Attack.Light2 || attack == Attack.Light3;
        if (wasLight)
        {
            comboStep = (comboStep + 1) % 3;
            comboUntil = Time.time + 0.55f;
        }
        if (leg != null) leg.gameObject.SetActive(false);
        attack = Attack.None;
        if (queuedLight && wasLight && comboStep != 0) StartLightOrFinisher();
        queuedLight = false;
    }

    void DoHit(Tool tool)
    {
        Vector3 me = transform.position;
        switch (attack)
        {
            case Attack.Light1:
            case Attack.Light2:
                HitEnemies(range, 65f, 2, swordDamage, HitType.Light, 0.045f, 0.12f);
                Lunge(0.25f);
                break;
            case Attack.Light3:
                HitEnemies(range + 0.2f, 70f, 3, comboEndDamage, HitType.ComboEnd, 0.08f, 0.28f);
                Lunge(0.45f);
                break;
            case Attack.Heavy:
                HitEnemies(range + 0.3f, 45f, 2, heavyDamage, HitType.Heavy, 0.11f, 0.4f);
                Lunge(0.5f);
                break;
            case Attack.Kick:
                HitEnemies(range - 0.4f, 50f, 1, kickDamage, HitType.Kick, 0.07f, 0.25f);
                break;
            case Attack.Finisher:
                FindTargets(range + 0.6f, 70f, 3);
                foreach (Enemy e in targets)
                    if (e.IsOpen)
                    {
                        EnemyManager.RequestHit(e, finisherDamage, me, HitType.Finisher);
                        CombatFx.Sparks(e.transform.position + Vector3.up * e.bodyScale, Color.white, 14);
                        CombatFx.Hitstop(0.18f);
                        PlayerCamera.Shake(0.6f);
                        Say("ДОБИВАНИЕ!");
                        break;
                    }
                Lunge(0.4f);
                break;
            case Attack.Tool:
                if (lookedEnemy != null)
                {
                    EnemyManager.RequestHit(lookedEnemy, otherToolDamage, me, HitType.Tool);
                    CombatFx.Hitstop(0.04f);
                }
                else if (lookedNode != null)
                {
                    if (tool == lookedNode.requiredTool) BuildingManager.RequestHitNode(lookedNode);
                    else Say("Нужен " + PlayerHotbar.NameOf(lookedNode.requiredTool).ToLower() + " (клавиша " + ((int)lookedNode.requiredTool + 1) + ")");
                }
                break;
        }
    }

    // Ударить всех тварей в дуге перед собой
    void HitEnemies(float reach, float arc, int max, float damage, HitType type, float stop, float shake)
    {
        FindTargets(reach, arc, max);
        if (targets.Count == 0) { if (type != HitType.Kick) PushThings(); return; }
        foreach (Enemy e in targets)
        {
            EnemyManager.RequestHit(e, damage, transform.position, type);
            CombatFx.Sparks(e.transform.position + Vector3.up * 0.9f * e.bodyScale, Color.white, type == HitType.Light ? 5 : 9);
        }
        CombatFx.Hitstop(stop);
        PlayerCamera.Shake(shake);
    }

    void FindTargets(float reach, float arc, int max)
    {
        targets.Clear();
        if (cam == null) return;
        Vector3 fwd = cam.transform.forward; fwd.y = 0f; fwd.Normalize();
        Vector3 me = transform.position;
        foreach (Enemy e in Enemy.All)
        {
            if (e == null || e.HealthFraction <= 0f || e.CurrentState == Enemy.State.Grabbed) continue;
            Vector3 d = e.transform.position - me;
            if (Mathf.Abs(d.y) > 2.5f) continue;
            d.y = 0f;
            float dist = d.magnitude - 0.45f * e.bodyScale;
            if (dist > reach) continue;
            if (dist > 0.6f && Vector3.Angle(fwd, d) > arc) continue;
            targets.Add(e);
        }
        targets.Sort((a, b) => (a.transform.position - me).sqrMagnitude.CompareTo((b.transform.position - me).sqrMagnitude));
        if (targets.Count > max) targets.RemoveRange(max, targets.Count - max);
    }

    void Lunge(float meters)
    {
        if (controller == null || !controller.enabled) return;
        Vector3 f = transform.forward; f.y = 0f;
        controller.Move(f.normalized * meters);
    }

    // ---------- Захват ----------
    void TryGrab()
    {
        if (waitingGrab) return;
        FindTargets(2.6f, 55f, 1);
        if (targets.Count == 0) { Say("Некого хватать — подойди вплотную"); return; }
        Enemy e = targets[0];
        if (!e.CanBeGrabbed) { Say("Громилу не поднять — сначала сбей его (тяжёлый удар или парирование)"); return; }
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

    void ThrowHeld(bool overShoulder)
    {
        Enemy e = EnemyManager.Instance != null ? EnemyManager.Instance.Find(heldId) : null;
        int id = heldId;
        heldId = -1;
        if (e == null) return;
        Vector3 f = cam != null ? cam.transform.forward : transform.forward;
        if (overShoulder)
        {
            // через себя: дугой над головой и об землю за спиной
            EnemyManager.RequestHit(e, 20f, transform.position, HitType.Light);
            EnemyManager.RequestThrow(id, -transform.forward * 5f + Vector3.up * 8f, 12f);
            PlayerCamera.Shake(0.3f);
        }
        else
        {
            Vector3 v = f * 15f + Vector3.up * 3f;
            EnemyManager.RequestThrow(id, v, 6f);
            PlayerCamera.Shake(0.2f);
        }
        StartAttack(Attack.Throw);
    }

    // ---------- Анимация оружия ----------
    void Animate()
    {
        if (swingModel == null) return;
        Vector3 euler = Vector3.zero;
        Vector3 offset = Vector3.zero;

        if (attack != Attack.None)
        {
            float p = Mathf.Clamp01((Time.time - attackStart) / attackDuration);
            float hitP = hitAt / attackDuration;
            float strike = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(p / (hitP * 1.4f)));       // быстрый мах до удара
            float back = p < 0.6f ? 1f : 1f - (p - 0.6f) / 0.4f;                               // возврат
            Vector3 from = Vector3.zero, to = Vector3.zero;
            switch (attack)
            {
                case Attack.Light1: from = new Vector3(0f, 75f, -70f); to = new Vector3(20f, -75f, -70f); break;
                case Attack.Light2: from = new Vector3(0f, -75f, 70f); to = new Vector3(20f, 75f, 70f); break;
                case Attack.Light3: from = new Vector3(-90f, 0f, 0f); to = new Vector3(60f, 0f, 0f); break;
                case Attack.Heavy: from = new Vector3(-110f, 10f, 0f); to = new Vector3(75f, -10f, 0f); break;
                case Attack.Finisher: from = new Vector3(-30f, 0f, 0f); to = new Vector3(30f, 0f, 0f); offset = new Vector3(0f, 0.1f, 0.5f) * strike * back; break;
                case Attack.Kick: from = new Vector3(10f, 0f, 0f); to = new Vector3(25f, 20f, 0f); break;
                case Attack.Tool: from = new Vector3(-40f, 0f, 0f); to = new Vector3(70f, -20f, 0f); break;
                case Attack.Throw: from = new Vector3(-20f, 0f, 0f); to = new Vector3(40f, 0f, 0f); break;
            }
            euler = Vector3.Lerp(from, to, strike) * back;
            if (leg != null && attack == Attack.Kick)
            {
                float k = Mathf.Sin(Mathf.Clamp01(p / 0.7f) * Mathf.PI);
                leg.localPosition = new Vector3(0.12f, -1.2f + 0.35f * k, 0.15f + 0.95f * k);
                leg.localRotation = Quaternion.Euler(-25f * k, 0f, 0f);
            }
        }
        else if (charging && hotbar != null && hotbar.Current == Tool.Sword)
        {
            float c = Mathf.Clamp01((Time.time - pressTime) / chargeTime);
            euler = new Vector3(-110f, 10f, 0f) * Mathf.SmoothStep(0f, 1f, c); // замах назад — копим силу
        }
        else if (IsHolding)
        {
            euler = new Vector3(-40f, 0f, 30f); // меч в сторону, руки заняты
        }
        else if (toolSwing > 0f)
        {
            toolSwing = Mathf.MoveTowards(toolSwing, 0f, Time.deltaTime / 0.45f);
            float a = Mathf.Sin(toolSwing * Mathf.PI) * 80f;
            euler = new Vector3(a, -a * 0.4f, 0f);
        }
        swingModel.localRotation = restRotation * Quaternion.Euler(euler);
        swingModel.localPosition = restPosition + offset;
    }

    void Say(string text) { message = text; messageUntil = Time.time + 1.5f; }

    // Толкнуть обломки и ресурсы перед собой
    void PushThings()
    {
        if (cam == null) return;
        Vector3 center = transform.position + Vector3.up * 0.6f + cam.transform.forward * (range * 0.6f);
        foreach (Collider c in Physics.OverlapSphere(center, range * 0.6f, ~0, QueryTriggerInteraction.Ignore))
        {
            Rigidbody rb = c.attachedRigidbody;
            if (rb == null || rb.isKinematic || rb.GetComponent<Enemy>() != null) continue;
            rb.AddForce((cam.transform.forward + Vector3.up * 0.4f) * pushForce, ForceMode.VelocityChange);
        }
    }

    void OnGUI()
    {
        GUI.Label(new Rect(Screen.width / 2f - 6, Screen.height / 2f - 12, 20, 24), "+", Hud.Big);

        // Зарядка тяжёлого удара
        if (charging && hotbar != null && hotbar.Current == Tool.Sword && Time.time - pressTime > 0.08f)
        {
            float c = Mathf.Clamp01((Time.time - pressTime) / chargeTime);
            Hud.Bar(new Rect(Screen.width / 2f - 40, Screen.height / 2f + 18, 80, 5), c, c >= 1f ? new Color(1f, 0.6f, 0.2f) : Color.white, null);
        }
        // Счётчик комбо
        if (Time.time < comboUntil && comboStep > 0)
            GUI.Label(new Rect(Screen.width / 2f + 30, Screen.height / 2f - 40, 200, 24), "комбо " + comboStep + "/3", Hud.Small);
        if (IsHolding)
            GUI.Label(new Rect(0, Screen.height / 2f + 60, Screen.width, 24), "Держишь тварь:  ЛКМ / G — бросить   Q — через себя", Hud.Center);
        else if (hotbar != null && hotbar.Current != Tool.Hammer && lookedNode == null)
            GUI.Label(new Rect(0, Screen.height - 88, Screen.width, 22),
                      "ЛКМ — удар (3 подряд — комбо, зажать — тяжёлый)   ПКМ — блок   Q — пинок   G — схватить   Ctrl — перекат", Hud.CenterTiny);

        if (lookedNode != null)
        {
            Rect r = new Rect(Screen.width / 2f - 100, Screen.height / 2f + 40, 200, 10);
            Hud.Bar(r, (float)lookedNode.HitsLeft / lookedNode.maxHits, new Color(0.9f, 0.75f, 0.3f),
                    lookedNode.displayName + "  —  " + PlayerHotbar.NameOf(lookedNode.requiredTool).ToLower());
        }
        if (Time.time < messageUntil)
            GUI.Label(new Rect(Screen.width / 2f - 300, Screen.height / 2f - 70, 600, 30), message, Hud.CenterBig);
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
