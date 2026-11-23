using UnityEngine;
using UnityEngine.InputSystem;

// Здоровье и выносливость героя. Полоски слева вверху.
//   Здоровье: урон от тварей и падений. Лечится само, но медленно и только если 8 сек без урона.
//   Выносливость (жёлтая): тратится на бег, прыжки, удары и блок. Восстанавливается, когда отдыхаешь.
//   Блок: меч в руке (1) + держать ПКМ. Удар почти не проходит, но съедает выносливость.
//         Выносливость кончилась — блок пробит, пара секунд без защиты.
// Смерть: половина ресурсов из сумки выпадает на месте гибели. Через 5 сек — возрождение в замке.
public class PlayerHealth : MonoBehaviour
{
    public float maxHealth = 100f;
    public float regenDelay = 8f;       // через сколько секунд после урона начинает лечиться
    public float regenPerSecond = 2f;
    public float respawnTime = 5f;
    public Vector3 respawnPoint = new Vector3(8f, 5.2f, -20f);

    [Header("Выносливость")]
    public float maxStamina = 100f;
    public float staminaRegen = 24f;    // в секунду
    public float staminaDelay = 0.9f;   // пауза после траты
    public float blockReduction = 0.8f; // сколько урона гасит блок
    public float blockStaminaPerDamage = 1.4f;

    public float Health { get; private set; }
    public float Stamina { get; private set; }
    public bool IsDead { get; private set; }
    public bool IsBlocking { get; private set; }

    public static PlayerHealth Local { get; private set; } // здоровье своего игрока

    float lastHurtTime = -100f;
    float lastStaminaUse = -100f;
    float blockBrokenUntil;
    float respawnAt;
    float flash, blockFlash;
    string deathText = "Ты погиб";
    bool droppedLoot;
    PlayerHotbar hotbar;

    void OnEnable() { Local = this; }
    void OnDisable() { if (Local == this) Local = null; }

    void Start()
    {
        Health = maxHealth;
        Stamina = maxStamina;
        hotbar = GetComponent<PlayerHotbar>();
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
        IsBlocking = wantBlock;
        if (IsBlocking) lastStaminaUse = Time.time - staminaDelay * 0.5f; // в блоке восстанавливается медленнее

        if (Time.time - lastStaminaUse > staminaDelay)
            Stamina = Mathf.Min(maxStamina, Stamina + staminaRegen * Time.deltaTime * (IsBlocking ? 0.3f : 1f));

        if (Health < maxHealth && Time.time - lastHurtTime > regenDelay)
            Health = Mathf.Min(maxHealth, Health + regenPerSecond * Time.deltaTime);
    }

    void OnGUI()
    {
        Hud.Bar(new Rect(12, 72, 220, 14), Health / maxHealth, new Color(0.85f, 0.2f, 0.2f), null);
        GUI.Label(new Rect(16, 86, 220, 22), "Здоровье " + Mathf.CeilToInt(Health), Hud.Tiny);
        Hud.Bar(new Rect(12, 108, 220, 8), Stamina / maxStamina,
                Time.time < blockBrokenUntil ? new Color(0.6f, 0.3f, 0.1f) : new Color(0.95f, 0.8f, 0.25f), null);
        GUI.Label(new Rect(16, 116, 300, 22), Time.time < blockBrokenUntil ? "Блок пробит!" : "Выносливость", Hud.Tiny);

        if (IsBlocking)
        {
            Color c = Color.Lerp(new Color(0.7f, 0.8f, 1f, 0.8f), Color.white, blockFlash);
            Color old = GUI.color; GUI.color = c;
            GUI.Label(new Rect(0, Screen.height / 2f + 22, Screen.width, 26), "[ БЛОК ]", Hud.Center);
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
