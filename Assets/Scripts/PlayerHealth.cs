using UnityEngine;

// Здоровье героя. Полоска слева вверху.
// Урон наносят твари. Через 5 сек без урона здоровье само восстанавливается.
// Если здоровье кончилось — герой погибает и через 5 сек возрождается во дворе форта.
public class PlayerHealth : MonoBehaviour
{
    public float maxHealth = 100f;
    public float regenDelay = 5f;      // через сколько секунд после урона начинает лечиться
    public float regenPerSecond = 4f;
    public float respawnTime = 5f;
    public Vector3 respawnPoint = new Vector3(0f, 1.1f, -12f);

    public float Health { get; private set; }
    public bool IsDead { get; private set; }

    public static PlayerHealth Local { get; private set; } // здоровье своего игрока

    float lastHurtTime = -100f;
    float respawnAt;
    float flash;

    void OnEnable() { Local = this; }
    void OnDisable() { if (Local == this) Local = null; }

    void Start()
    {
        Health = maxHealth;
        NetworkPlayer np = GetComponent<NetworkPlayer>();
        if (np != null) respawnPoint = np.spawnPoint;
    }

    public void TakeDamage(float amount)
    {
        if (IsDead || amount <= 0f) return;
        Health = Mathf.Max(0f, Health - amount);
        lastHurtTime = Time.time;
        flash = 1f;
        if (Health <= 0f) Die();
    }

    void Die()
    {
        IsDead = true;
        respawnAt = Time.time + respawnTime;
        SetControls(false);
    }

    void Respawn()
    {
        CharacterController cc = GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;
        transform.position = respawnPoint + new Vector3(Random.Range(-2f, 2f), 0f, Random.Range(-2f, 2f));
        if (cc != null) cc.enabled = true;
        Health = maxHealth;
        IsDead = false;
        SetControls(true);
    }

    void SetControls(bool on)
    {
        foreach (Behaviour b in new Behaviour[] { GetComponent<PlayerMovement>(), GetComponent<PlayerCombat>(), GetComponent<PlayerBuilder>() })
            if (b != null) b.enabled = on;
    }

    void Update()
    {
        flash = Mathf.Max(0f, flash - Time.deltaTime * 2f);
        if (IsDead)
        {
            if (Time.time >= respawnAt) Respawn();
            return;
        }
        if (Health < maxHealth && Time.time - lastHurtTime > regenDelay)
            Health = Mathf.Min(maxHealth, Health + regenPerSecond * Time.deltaTime);
    }

    void OnGUI()
    {
        Hud.Bar(new Rect(12, 72, 220, 14), Health / maxHealth, new Color(0.85f, 0.2f, 0.2f), null);
        GUI.Label(new Rect(16, 86, 220, 22), "Здоровье " + Mathf.CeilToInt(Health), Hud.Tiny);

        if (flash > 0f) Hud.Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0.8f, 0f, 0f, flash * 0.35f));

        if (IsDead)
        {
            Hud.Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0f, 0f, 0f, 0.75f));
            GUI.Label(new Rect(0, Screen.height / 2f - 40, Screen.width, 40), "Ты погиб", Hud.CenterBig);
            GUI.Label(new Rect(0, Screen.height / 2f + 5, Screen.width, 30),
                      "Возрождение через " + Mathf.CeilToInt(respawnAt - Time.time), Hud.Center);
        }
    }
}
