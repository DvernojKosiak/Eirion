using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

// Набеги тварей.
// Хост: с наступлением ночи выпускает волну с севера (каждая следующая больше), управляет тварями
//       и 10 раз в секунду рассылает всем, где они.
// Остальные: показывают копии тварей и могут их бить (удар уходит хосту).
// N — набег прямо сейчас (для проверки).
public class EnemyManager : MonoBehaviour
{
    public static EnemyManager Instance { get; private set; }

    [Header("Где появляются и куда идут")]
    public Vector3 spawnCenter = new Vector3(0f, 0.2f, 150f);
    public float spawnWidth = 120f;
    public Vector3 villageCenter = new Vector3(0f, 0f, -66f);

    [Header("Волны")]
    public int baseCount = 4;            // тварей в первой волне
    public int extraPerWave = 2;         // + сколько в каждой следующей
    public int maxAlive = 40;

    [Header("Внешний вид")]
    public Material bodyMaterial;
    public Material eyeMaterial;

    public class PlayerInfo { public Transform transform; public ulong ownerId; public bool dead; }

    const string MsgState = "E.enemies", MsgHitReq = "E.enemyHit", MsgHurt = "E.hurt", MsgWaveReq = "E.waveReq";

    readonly Dictionary<int, Enemy> enemies = new Dictionary<int, Enemy>();
    readonly List<PlayerInfo> players = new List<PlayerInfo>();
    int wave;
    int nextId = 1;
    float sendTimer, playersTimer;
    CustomMessagingManager registered;
    bool subscribed;

    public int AliveCount => enemies.Count;

    void Awake() { Instance = this; }

    void Start()
    {
        if (GameDirector.Instance != null && !subscribed)
        {
            subscribed = true;
            GameDirector.Instance.NightStarted += () => { if (Net.IsHost) SpawnWave(); };
            GameDirector.Instance.DayStarted += () => { if (Net.IsHost) BurnAtDawn(); };
        }
    }

    // На рассвете твари сгорают
    void BurnAtDawn()
    {
        foreach (var kv in enemies) if (kv.Value != null) kv.Value.Health = 0f;
    }

    bool Over => GameDirector.Instance != null && GameDirector.Instance.IsOver;

    bool GameRunning => Net.Online || NetworkManager.Singleton == null;

    void Update()
    {
        Net.EnsureRegistered(ref registered, Register);

        if (!GameRunning || Over) return;

        if (Net.IsHost)
        {
            Keyboard kb = Keyboard.current;
            if (kb != null && kb.nKey.wasPressedThisFrame) SpawnWave();

            playersTimer -= Time.deltaTime;
            if (playersTimer <= 0f) { playersTimer = 1f; RefreshPlayers(); }

            var dead = new List<int>();
            foreach (var kv in enemies)
            {
                if (kv.Value == null) { dead.Add(kv.Key); continue; }
                kv.Value.Simulate(players, villageCenter);
                if (kv.Value.transform.position.y < -20f) kv.Value.Health = 0f; // провалился под землю
                if (kv.Value.Health <= 0f) dead.Add(kv.Key);
            }
            foreach (int id in dead) { if (enemies[id] != null) enemies[id].Die(); enemies.Remove(id); }

            sendTimer -= Time.deltaTime;
            if (sendTimer <= 0f) { sendTimer = 0.1f; SendState(); }
        }
        else
        {
            Keyboard kb = Keyboard.current;
            if (kb != null && kb.nKey.wasPressedThisFrame)
            {
                FastBufferWriter w = Net.Writer(8);
                try { w.WriteValueSafe(1); Net.ToHost(MsgWaveReq, w); } finally { w.Dispose(); }
            }
        }
    }

    // ---------- Волна ----------
    void SpawnWave()
    {
        wave++;
        int count = Mathf.Min(baseCount + extraPerWave * (wave - 1), maxAlive - enemies.Count);
        for (int i = 0; i < count; i++)
        {
            Vector3 p = spawnCenter + new Vector3(Random.Range(-spawnWidth / 2f, spawnWidth / 2f), 0f, Random.Range(-8f, 8f));
            Enemy e = CreateEnemy(nextId++, p, 180f, true);
            e.maxHealth = 60f + 10f * (wave - 1); // с каждой волной покрепче
            e.Health = e.maxHealth;
        }
    }

    Enemy CreateEnemy(int id, Vector3 pos, float yaw, bool simulated)
    {
        GameObject go = new GameObject("Enemy_" + id);
        go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));

        // Внешний вид: сгорбленная тварь из простых фигур
        Transform vis = new GameObject("Visual").transform;
        vis.SetParent(go.transform, false);
        Part(vis, PrimitiveType.Capsule, new Vector3(0f, 0.85f, 0f), new Vector3(0.9f, 0.75f, 0.9f), Vector3.zero, bodyMaterial);
        Part(vis, PrimitiveType.Sphere, new Vector3(0f, 1.35f, 0.35f), new Vector3(0.6f, 0.5f, 0.65f), Vector3.zero, bodyMaterial);
        Part(vis, PrimitiveType.Sphere, new Vector3(-0.13f, 1.42f, 0.64f), Vector3.one * 0.1f, Vector3.zero, eyeMaterial);
        Part(vis, PrimitiveType.Sphere, new Vector3(0.13f, 1.42f, 0.64f), Vector3.one * 0.1f, Vector3.zero, eyeMaterial);
        Part(vis, PrimitiveType.Cube, new Vector3(-0.5f, 0.85f, 0.35f), new Vector3(0.15f, 0.15f, 0.9f), new Vector3(25f, -10f, 0f), bodyMaterial);
        Part(vis, PrimitiveType.Cube, new Vector3(0.5f, 0.85f, 0.35f), new Vector3(0.15f, 0.15f, 0.9f), new Vector3(25f, 10f, 0f), bodyMaterial);
        for (int i = 0; i < 3; i++)
            Part(vis, PrimitiveType.Cube, new Vector3(0f, 1.45f - i * 0.25f, -0.35f - i * 0.05f), new Vector3(0.08f, 0.35f, 0.08f), new Vector3(-35f, 0f, 0f), bodyMaterial);

        Enemy e = go.AddComponent<Enemy>();
        e.Setup(id, simulated, vis);
        enemies[id] = e;
        return e;
    }

    static void Part(Transform parent, PrimitiveType type, Vector3 pos, Vector3 scale, Vector3 euler, Material mat)
    {
        GameObject p = GameObject.CreatePrimitive(type);
        Destroy(p.GetComponent<Collider>());
        p.transform.SetParent(parent, false);
        p.transform.localPosition = pos;
        p.transform.localScale = scale;
        p.transform.localRotation = Quaternion.Euler(euler);
        if (mat != null) p.GetComponent<Renderer>().sharedMaterial = mat;
    }

    void RefreshPlayers()
    {
        players.Clear();
        if (Net.Online)
        {
            foreach (NetworkPlayer np in FindObjectsByType<NetworkPlayer>(FindObjectsSortMode.None))
            {
                if (!np.IsSpawned) continue;
                bool dead = np.OwnerClientId == Net.LocalId && PlayerHealth.Local != null && PlayerHealth.Local.IsDead;
                players.Add(new PlayerInfo { transform = np.transform, ownerId = np.OwnerClientId, dead = dead });
            }
        }
        else
        {
            foreach (PlayerMovement pm in FindObjectsByType<PlayerMovement>(FindObjectsSortMode.None))
                players.Add(new PlayerInfo { transform = pm.transform, ownerId = 0, dead = PlayerHealth.Local != null && PlayerHealth.Local.IsDead });
        }
    }

    // ---------- Удары ----------
    public static void RequestHit(Enemy e, float damage)
    {
        if (Instance == null || e == null) return;
        e.Flash();
        if (Net.IsHost) { Instance.ApplyHit(e.id, damage); return; }
        FastBufferWriter w = Net.Writer(16);
        try { w.WriteValueSafe(e.id); w.WriteValueSafe(damage); Net.ToHost(MsgHitReq, w); } finally { w.Dispose(); }
    }

    void ApplyHit(int id, float damage)
    {
        if (enemies.TryGetValue(id, out Enemy e) && e != null)
        {
            e.Health -= damage;
            e.Flash();
        }
    }

    // Тварь ударила игрока (вызывает хост)
    public static void HurtPlayer(ulong ownerId, float damage)
    {
        if (ownerId == Net.LocalId)
        {
            if (PlayerHealth.Local != null) PlayerHealth.Local.TakeDamage(damage);
            return;
        }
        FastBufferWriter w = Net.Writer(8);
        try { w.WriteValueSafe(damage); Net.ToClient(MsgHurt, ownerId, w); } finally { w.Dispose(); }
    }

    // ---------- Сеть ----------
    void SendState()
    {
        if (!Net.Online) return;
        int n = enemies.Count;
        var ids = new int[n]; var pos = new Vector3[n]; var yaw = new float[n]; var hp = new float[n];
        int i = 0;
        foreach (var kv in enemies)
        {
            if (kv.Value == null) { n--; continue; }
            ids[i] = kv.Key; pos[i] = kv.Value.transform.position; yaw[i] = kv.Value.transform.eulerAngles.y; hp[i] = kv.Value.HealthFraction;
            i++;
        }
        System.Array.Resize(ref ids, i); System.Array.Resize(ref pos, i); System.Array.Resize(ref yaw, i); System.Array.Resize(ref hp, i);
        FastBufferWriter w = Net.Writer(64 + i * 32);
        try
        {
            w.WriteValueSafe(wave);
            w.WriteValueSafe(ids); w.WriteValueSafe(pos); w.WriteValueSafe(yaw); w.WriteValueSafe(hp);
            Net.ToAllClients(MsgState, w, NetworkDelivery.ReliableFragmentedSequenced);
        }
        finally { w.Dispose(); }
    }

    void Register(CustomMessagingManager cmm)
    {
        cmm.RegisterNamedMessageHandler(MsgState, (sender, r) =>
        {
            r.ReadValueSafe(out int w);
            r.ReadValueSafe(out int[] ids); r.ReadValueSafe(out Vector3[] pos); r.ReadValueSafe(out float[] yaw); r.ReadValueSafe(out float[] hp);
            if (Net.IsHost) return;
            wave = w;
            var alive = new HashSet<int>(ids);
            for (int i = 0; i < ids.Length; i++)
            {
                if (!enemies.TryGetValue(ids[i], out Enemy e) || e == null) e = CreateEnemy(ids[i], pos[i], yaw[i], false);
                e.SetNetState(pos[i], yaw[i], hp[i]);
            }
            var gone = new List<int>();
            foreach (var kv in enemies) if (!alive.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (int id in gone) { if (enemies[id] != null) enemies[id].Die(); enemies.Remove(id); }
        });
        cmm.RegisterNamedMessageHandler(MsgHitReq, (sender, r) =>
        {
            r.ReadValueSafe(out int id); r.ReadValueSafe(out float dmg);
            if (Net.IsHost) ApplyHit(id, dmg);
        });
        cmm.RegisterNamedMessageHandler(MsgHurt, (sender, r) =>
        {
            r.ReadValueSafe(out float dmg);
            if (PlayerHealth.Local != null) PlayerHealth.Local.TakeDamage(dmg);
        });
        cmm.RegisterNamedMessageHandler(MsgWaveReq, (sender, r) =>
        {
            r.ReadValueSafe(out int _);
            if (Net.IsHost) SpawnWave();
        });
    }

    // ---------- Экран ----------
    void OnGUI()
    {
        if (!GameRunning) return;

        // Полоски здоровья над тварями
        Camera cam = Camera.main;
        if (cam == null) return;
        foreach (var kv in enemies)
        {
            Enemy e = kv.Value;
            if (e == null || e.HealthFraction >= 0.999f) continue;
            Vector3 world = e.transform.position + Vector3.up * 2.1f;
            if (Vector3.Distance(world, cam.transform.position) > 30f) continue;
            Vector3 s = cam.WorldToScreenPoint(world);
            if (s.z < 0f) continue;
            Hud.Bar(new Rect(s.x - 30, Screen.height - s.y, 60, 6), e.HealthFraction, new Color(0.8f, 0.2f, 0.9f), null);
        }
    }
}
