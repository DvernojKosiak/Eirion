using UnityEngine;
using UnityEngine.InputSystem;

// Здоровье и выносливость героя. Полоски слева вверху.
//   Здоровье: урон от тварей и падений. Лечится само, но медленно и только если 8 сек без урона.
//   Выносливость (жёлтая): тратится на бег, прыжки, удары и блок. Восстанавливается, когда отдыхаешь.
//   Блок: меч в руке (1) + держать ПКМ. Удар почти не проходит, но съедает выносливость.
//         Выносливость кончилась — блок пробит, пара секунд без защиты.
//   ПАРИРОВАНИЕ: поставь блок в самый последний момент (за 0.25 сек до удара) — урона нет вовсе,
//         а тварь «открыта» и шатается — её можно добить.
//   Перекат (Ctrl) — на миг неуязвим.
// Смерть: половина ресурсов из сумки выпадает на месте гибели. Через 5 сек — возрождение в замке.
public class PlayerHealth : MonoBehaviour
{
    public float maxHealth = 100f;
    public float regenDelay = 8f;       // через сколько секунд после урона начинает лечиться
    public float regenPerSecond = 2f;
    public float respawnTime = 5f;
    public Vector3 respawnPoint = new Vector3(8f, 5.2f, -20f);

    [Header("Выносливость")]
    public float maxStamina = 160f;
    public float staminaRegen = 38f;    // в секунду
    public float staminaDelay = 0.6f;   // пауза после траты
    public float blockReduction = 0.8f; // сколько урона гасит блок
    public float blockStaminaPerDamage = 1.1f;

    public float Health { get; private set; }
    public float Stamina { get; private set; }
    public bool IsDead { get; private set; }
    public bool IsBlocking { get; private set; }
    public float BlockJolt => blockFlash;            // для анимации меча: удар пришёлся в блок
    public bool InParryWindow => IsBlocking && Time.time - guardSetAt < parryWindow;
    public float parryWindow = 0.3f;                 // поставил блок (или перевёл меч) за столько секунд до удара — парирование
    public float blockAngle = 70f;                   // блок держит только удары спереди (смотри на врага!)
    public float guardFlick = 22f;                   // насколько резко двинуть мышь в блоке, чтобы перевести меч

    // Куда поставлен меч в блоке (как видишь на экране): -1 — слева, 0 — сверху, 1 — справа.
    // Блок держит только удар с той стороны, куда поставлен меч.
    public int GuardSide { get; private set; }
    float guardSetAt = -100f;
    Vector2 guardAcc;
    bool guardLookSlow;

    // С какой стороны (на экране) прилетит удар этой твари: -1 слева, 0 сверху, 1 справа
    public static int StrikeSide(Enemy e) => e.AttackSide == 0 ? 0 : -e.AttackSide;
    static string SideName(int s) => s == 0 ? "сверху" : s < 0 ? "слева" : "справа";

    public static PlayerHealth Local { get; private set; } // здоровье своего игрока

    float lastHurtTime = -100f;
    float lastStaminaUse = -100f;
    float blockBrokenUntil;
    float respawnAt;
    float flash, blockFlash;
    string deathText = "Ты погиб";
    bool droppedLoot;
    PlayerHotbar hotbar;
    PlayerMovement movement;
    float blockStart = -100f;
    string notice;
    float noticeUntil;
    Color noticeColor;

    void OnEnable() { Local = this; }
    void OnDisable() { if (Local == this) Local = null; }

    void Start()
    {
        Health = maxHealth;
        Stamina = maxStamina;
        hotbar = GetComponent<PlayerHotbar>();
        movement = GetComponent<PlayerMovement>();
        NetworkPlayer np = GetComponent<NetworkPlayer>();
        if (np != null) respawnPoint = np.spawnPoint;
    }

    // Потратить выносливость. continuous = трата каждый кадр (бег) — хватает и остатка
    public bool UseStamina(float amount, bool continuous)
    {
        if (IsDead) return false;
        if (continuous ? Stamina <= 0.5f : Stamina < amount) return false;
        Stamina = Mathf.Max(0f, Stamina - amount);
        lastStaminaUse = Time.time;
        return true;
    }

    public void TakeDamage(float amount) => TakeDamage(amount, null, true);

    // Удар твари (attackerId — какая тварь, -1 если плевок и т.п.)
    public void TakeHit(float amount, int attackerId)
    {
        if (IsDead) return;
        if (movement != null && movement.IsDodging) { Notice("Уклон!", new Color(0.6f, 0.9f, 1f)); return; }
        // Блок держит только удар спереди и только с той стороны, куда поставлен меч
        bool facing = true;
        int side = 0;
        Vector3 away = -transform.forward;
        Enemy attacker = attackerId >= 0 && EnemyManager.Instance != null ? EnemyManager.Instance.Find(attackerId) : null;
        if (attacker != null)
        {
            Camera c0 = Camera.main;
            Vector3 f = c0 != null ? c0.transform.forward : transform.forward; f.y = 0f;
            Vector3 to = attacker.transform.position - transform.position; to.y = 0f;
            facing = to.sqrMagnitude < 0.01f || Vector3.Angle(f, to) <= blockAngle;
            side = StrikeSide(attacker);
            if (to.sqrMagnitude > 0.01f) away = -to.normalized;
        }
        Camera cam = Camera.main;
        Vector3 bladePoint = cam != null ? cam.transform.position + cam.transform.forward * 0.7f + cam.transform.right * side * 0.25f : transform.position + Vector3.up * 1.5f;

        if (IsBlocking && (!facing || (attacker != null && GuardSide != side)))
        {
            Notice(!facing ? "Удар сбоку — блок не держит!" : "Удар " + SideName(side) + " — меч не там!", new Color(1f, 0.45f, 0.35f));
            TakeDamage(amount, null, false);
            Hurt(amount, away, side);
            return;
        }
        if (IsBlocking && attackerId >= 0 && InParryWindow)
        {
            // Парирование: поставил меч точно под удар в последний момент
            Notice("ПАРИРОВАНО!", new Color(1f, 0.85f, 0.3f));
            EnemyManager.RequestParry(attackerId);
            CombatFx.Play(CombatFx.Snd.Parry, bladePoint, 1f);
            CombatFx.Sparks(bladePoint, new Color(1f, 0.85f, 0.4f), 18);
            PlayerCamera.Shake(0.3f);
            PlayerCamera.Punch(new Vector3(-2f, side * -2f, 0f));
            CombatFx.Hitstop(0.12f);
            Stamina = Mathf.Min(maxStamina, Stamina + 10f);
            blockFlash = 1f;
            return;
        }
        bool blocked = IsBlocking;
        TakeDamage(amount, null, true);
        if (blocked && IsBlocking)
        {
            // звон, искры о клинок, меч дёргается, тебя чуть отталкивает
            CombatFx.Play(CombatFx.Snd.Clang, bladePoint, 0.9f);
            CombatFx.Sparks(bladePoint, Color.white, 10);
            CombatFx.Hitstop(0.05f);
            if (movement != null) movement.Push(away * 3f, 0.1f);
            PlayerCamera.Punch(new Vector3(-3f, side * -3f, side * 2f));
            PlayerCamera.Shake(0.15f);
            Notice("Блок", new Color(0.75f, 0.85f, 1f));
        }
        else Hurt(amount, away, side);
    }

    // Пропустил удар: отбрасывает, голову дёргает в сторону удара, ноги заплетаются
    void Hurt(float amount, Vector3 away, int side)
    {
        float k = Mathf.Clamp(amount / 20f, 0.5f, 1.6f);
        if (movement != null) movement.Push(away * (4f + 3f * k), 0.3f * k);
        PlayerCamera.Punch(new Vector3(-6f * k, side * -6f * k, side == 0 ? 0f : side * 9f * k));
        PlayerCamera.Shake(Mathf.Clamp(amount / 30f, 0.2f, 0.6f));
        CombatFx.Play(CombatFx.Snd.Hurt, transform.position + Vector3.up * 1.4f, 1f);
        CombatFx.Hitstop(0.06f);
    }

    void Notice(string text, Color c) { notice = text; noticeColor = c; noticeUntil = Time.time + 1f; }

    public void TakeDamage(float amount, string reason, bool blockable)
    {
        if (IsDead || amount <= 0f) return;
        if (blockable && IsBlocking)
        {
            float cost = amount * blockStaminaPerDamage;
            blockFlash = 1f;
            if (Stamina >= cost)
            {
                Stamina -= cost;
                lastStaminaUse = Time.time;
                amount *= 1f - blockReduction;
            }
            else
            {
                // блок пробит
                Stamina = 0f;
                lastStaminaUse = Time.time;
                blockBrokenUntil = Time.time + 2f;
                IsBlocking = false;
            }
        }
        Health = Mathf.Max(0f, Health - amount);
        lastHurtTime = Time.time;
        flash = Mathf.Clamp01(amount / 15f);
        if (Health <= 0f) Die(reason ?? "Ты погиб");
    }

    void Die(string text)
    {
        deathText = text;
        IsDead = true;
        IsBlocking = false;
        respawnAt = Time.time + respawnTime;
        Inventory inv = GetComponent<Inventory>();
        droppedLoot = inv != null && inv.DropHalf(transform.position + Vector3.up);
        SetControls(false);
    }

    void Respawn()
    {
        CharacterController cc = GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;
        transform.position = respawnPoint + new Vector3(Random.Range(-2f, 2f), 0f, Random.Range(-2f, 2f));
        if (cc != null) cc.enabled = true;
        PlayerMovement pm = GetComponent<PlayerMovement>();
        if (pm != null) pm.ResetFall();
        Health = maxHealth;
        Stamina = maxStamina;
        IsDead = false;
        SetControls(true);
    }

    void SetControls(bool on)
    {
        foreach (Behaviour b in new Behaviour[] { GetComponent<PlayerMovement>(), GetComponent<PlayerCombat>(), GetComponent<PlayerBuilder>(), GetComponent<PlayerInteract>() })
            if (b != null) b.enabled = on;
    }

    void Update()
    {
        flash = Mathf.Max(0f, flash - Time.deltaTime * 2f);
        blockFlash = Mathf.Max(0f, blockFlash - Time.deltaTime * 4f);
        if (IsDead)
        {
            if (Time.time >= respawnAt) Respawn();
            return;
        }
        if (transform.position.y < -40f) // сорвался в пропасть Раскола
        {
            Health = 0f;
            Die("Ты сорвался в Раскол");
            return;
        }

        // Блок: меч + ПКМ
        Mouse mouse = Mouse.current;
        bool wantBlock = mouse != null && mouse.rightButton.isPressed && Cursor.lockState == CursorLockMode.Locked &&
                         (hotbar == null || hotbar.Current == Tool.Sword) && Time.time >= blockBrokenUntil && Stamina > 1f;
        if (wantBlock && !IsBlocking) { blockStart = Time.time; guardSetAt = Time.time; GuardSide = 0; guardAcc = Vector2.zero; }
        if (wantBlock)
        {
            // резкое движение мышью переводит меч: влево, вправо или вверх
            Vector2 d = mouse.delta.ReadValue();
            guardAcc = guardAcc * Mathf.Exp(-12f * Time.unscaledDeltaTime) + d;
            int want = GuardSide;
            if (Mathf.Abs(guardAcc.x) > guardFlick && Mathf.Abs(guardAcc.x) > Mathf.Abs(guardAcc.y)) want = guardAcc.x > 0f ? 1 : -1;
            else if (guardAcc.y > guardFlick && guardAcc.y > Mathf.Abs(guardAcc.x)) want = 0;
            if (want != GuardSide) { GuardSide = want; guardSetAt = Time.time; guardAcc = Vector2.zero; }
        }
        if (wantBlock != guardLookSlow) { guardLookSlow = wantBlock; PlayerCamera.LookScale = wantBlock ? 0.3f : 1f; } // в блоке мышь переводит меч, камера почти стоит
        IsBlocking = wantBlock;
        if (IsBlocking) lastStaminaUse = Time.time - staminaDelay * 0.5f; // в блоке восстанавливается медленнее

        if (Time.time - lastStaminaUse > staminaDelay)
            Stamina = Mathf.Min(maxStamina, Stamina + staminaRegen * Time.deltaTime * (IsBlocking ? 0.3f : 1f));

        if (Health < maxHealth && Time.time - lastHurtTime > regenDelay)
            Health = Mathf.Min(maxHealth, Health + regenPerSecond * Time.deltaTime);
    }

    // Красные метки вокруг прицела: какая тварь сейчас замахивается на тебя и откуда прилетит удар.
    // Метка там, где тварь (сверху — впереди, сбоку — сбоку, снизу — за спиной). Полная и яркая — удар вот-вот.
    void DrawThreats(float cx, float cy)
    {
        if (IsDead) return;
        Camera cam = Camera.main;
        if (cam == null) return;
        Vector3 f = cam.transform.forward; f.y = 0f;
        if (f.sqrMagnitude < 0.001f) return;
        f.Normalize();
        foreach (Enemy e in Enemy.All)
        {
            if (e == null || e.CurrentState != Enemy.State.WindUp) continue;
            Vector3 to = transform.position - e.transform.position; to.y = 0f;
            float dist = to.magnitude;
            if (dist > e.attackRange + 2.5f) continue;
            if (dist > 0.1f && Vector3.Angle(e.transform.forward, to) > 60f) continue; // замахивается не на меня
            float ang = Vector3.SignedAngle(f, -to, Vector3.up) * Mathf.Deg2Rad;
            Vector2 dir = new Vector2(Mathf.Sin(ang), -Mathf.Cos(ang));
            float p = e.WindProgress;
            Color c = new Color(1f, 0.15f + 0.3f * (1f - p), 0.1f, 0.35f + 0.6f * p);
            for (int i = 0; i < 4; i++)
            {
                float r = 70f + i * 9f, size = 7f + i * 2f + p * 4f;
                Hud.Fill(new Rect(cx + dir.x * r - size / 2f, cy + dir.y * r - size / 2f, size, size), c, size / 2f);
            }
            int ss = StrikeSide(e);
            string from = SideName(ss);
            Color old = GUI.color; GUI.color = c;
            if (IsBlocking && GuardSide == ss) { GUI.color = new Color(0.6f, 0.9f, 1f); from += " — меч там"; }
            GUI.Label(new Rect(cx + dir.x * 120f - 60f, cy + dir.y * 120f - 10f, 120f, 20f), from, Hud.CenterTiny);
            GUI.color = old;
        }
    }

    void OnGUI()
    {
        Hud.Bar(new Rect(12, 72, 220, 14), Health / maxHealth, new Color(0.85f, 0.2f, 0.2f), null);
        GUI.Label(new Rect(16, 86, 220, 22), "Здоровье " + Mathf.CeilToInt(Health), Hud.Tiny);
        Hud.Bar(new Rect(12, 108, 220, 8), Stamina / maxStamina,
                Time.time < blockBrokenUntil ? new Color(0.6f, 0.3f, 0.1f) : new Color(0.95f, 0.8f, 0.25f), null);
        GUI.Label(new Rect(16, 116, 300, 22), Time.time < blockBrokenUntil ? "Блок пробит!" : "Выносливость", Hud.Tiny);

        float cx = Screen.width / 2f, cy = Screen.height / 2f;
        if (IsBlocking)
        {
            // Скобки вокруг прицела: золотые — окно парирования, голубые — обычный блок
            Color c = InParryWindow ? new Color(1f, 0.8f, 0.3f, 0.95f) : new Color(0.65f, 0.8f, 1f, 0.85f);
            c = Color.Lerp(c, Color.white, blockFlash);
            float gap = 34f + blockFlash * 8f;
            Color dim = new Color(c.r, c.g, c.b, 0.18f);
            // три стороны блока: яркая — куда поставлен меч
            Hud.Fill(new Rect(cx - gap - 6, cy - 26, 6, 52), GuardSide < 0 ? c : dim, 3);
            Hud.Fill(new Rect(cx + gap, cy - 26, 6, 52), GuardSide > 0 ? c : dim, 3);
            Hud.Fill(new Rect(cx - 26, cy - gap - 6, 52, 6), GuardSide == 0 ? c : dim, 3);
            Color old = GUI.color; GUI.color = c;
            GUI.Label(new Rect(0, cy + 44, Screen.width, 22), InParryWindow ? "парирование!" : "блок " + SideName(GuardSide) + " — дёрни мышь влево / вправо / вверх под удар", Hud.CenterTiny);
            GUI.color = old;
        }

        DrawThreats(cx, cy);

        if (Time.time < noticeUntil)
        {
            Color old = GUI.color; GUI.color = noticeColor;
            GUI.Label(new Rect(0, Screen.height / 2f - 110, Screen.width, 34), notice, Hud.CenterBig);
            GUI.color = old;
        }

        if (flash > 0f) Hud.Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0.8f, 0f, 0f, flash * 0.35f));

        if (IsDead)
        {
            Hud.Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0f, 0f, 0f, 0.75f));
            GUI.Label(new Rect(0, Screen.height / 2f - 40, Screen.width, 40), deathText, Hud.CenterBig);
            if (droppedLoot)
                GUI.Label(new Rect(0, Screen.height / 2f - 2, Screen.width, 24), "Половина ресурсов осталась там, где ты погиб. Вернись за ними!", Hud.CenterTiny);
            GUI.Label(new Rect(0, Screen.height / 2f + 25, Screen.width, 30),
                      "Возрождение через " + Mathf.CeilToInt(respawnAt - Time.time), Hud.Center);
        }
    }
}
