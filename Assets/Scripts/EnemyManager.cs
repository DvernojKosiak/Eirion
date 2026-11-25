using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

// Набеги тварей.
// Хост: с наступлением ночи твари лезут из пропасти Раскола на севере (каждая волна больше),
//       идут по дороге через форт к деревне. Хост управляет ими
//       и 10 раз в секунду рассылает всем, где они.
// Остальные: показывают копии тварей и могут их бить (удар уходит хосту).
// N — набег прямо сейчас (для проверки).
public class EnemyManager : MonoBehaviour
{
    public static EnemyManager Instance { get; private set; }

    [Header("Где появляются и куда идут")]
    public Vector3 spawnCenter = new Vector3(0f, -9f, 181f); // в пропасти, под краем обрыва
    public float spawnWidth = 160f;
    public float climbUntil = 0.6f;                           // докуда лезть (высота края)
    public Vector3 villageCenter = new Vector3(0f, 0f, -105f);
    [Tooltip("Дорога тварей: от края пропасти через ворота форта к деревне")]
    public Vector3[] path = new Vector3[]
    {
        new Vector3(0f, 0f, 75f), new Vector3(0f, 0f, 62f), new Vector3(0f, 4f, 44f),
        new Vector3(0f, 4f, 0f), new Vector3(0f, 4f, -44f), new Vector3(0f, 0f, -64f),
    };

    [Header("Волны")]
    public int baseCount = 4;            // тварей в первой волне
    public int extraPerWave = 2;         // + сколько в каждой следующей
    public int maxAlive = 40;

    [Header("Внешний вид")]
    public Material bodyMaterial;
    public Material eyeMaterial;

    public class PlayerInfo { public Transform transform; public ulong ownerId; public bool dead; }

    const string MsgState = "E.enemies", MsgHitReq = "E.enemyHit", MsgHurt = "E.hurt", MsgWaveReq = "E.waveReq", MsgSpit = "E.spit",
                 MsgParryReq = "E.parry", MsgGrabReq = "E.grab", MsgGrabRes = "E.grabRes", MsgThrowReq = "E.throw", MsgFx = "E.fx", MsgReact = "E.react",
                 MsgArrowReq = "E.arrowReq", MsgArrow = "E.arrow",
                 MsgTkReq = "E.tkReq", MsgTkRes = "E.tkRes", MsgTkMove = "E.tkMove", MsgSpellReq = "E.spellReq", MsgSpell = "E.spell";

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

            AssignTokens();
            var dead = new List<int>();
            foreach (var kv in enemies)
            {
                if (kv.Value == null) { dead.Add(kv.Key); continue; }
                kv.Value.Simulate(players, villageCenter);
                if (kv.Value.transform.position.y < -30f) kv.Value.Health = 0f; // сорвался в пропасть
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

    // ---------- Очередь на атаку: на одного игрока нападают максимум двое, остальные кружат ----------
    public int maxAttackersPerPlayer = 2;

    void AssignTokens()
    {
        foreach (PlayerInfo p in players)
        {
            int have = 0;
            var waiting = new List<Enemy>();
            foreach (var kv in enemies)
            {
                Enemy e = kv.Value;
                if (e == null || e.TargetOwner != p.ownerId || e.shootRange > 0f) continue;
                if (e.HasToken) have += e.kind == EnemyKind.Brute ? 2 : 1;
                else if (Time.time >= e.TokenCooldownUntil && e.CurrentState == Enemy.State.Move) waiting.Add(e);
            }
            Vector3 pp = p.transform != null ? p.transform.position : Vector3.zero;
            waiting.Sort((a, b) => (a.transform.position - pp).sqrMagnitude.CompareTo((b.transform.position - pp).sqrMagnitude));
            foreach (Enemy e in waiting)
            {
                int cost = e.kind == EnemyKind.Brute ? 2 : 1; // громила занимает оба места
                if (have + cost > maxAttackersPerPlayer && have > 0) break;
                e.HasToken = true;
                have += cost;
            }
        }
        // Кто потерял цель — отдаёт очередь
        foreach (var kv in enemies)
            if (kv.Value != null && kv.Value.TargetOwner == Net.NoOne) kv.Value.HasToken = false;
    }

    // ---------- Волна ----------
    void SpawnWave()
    {
        wave++;
        int count = Mathf.Min(baseCount + extraPerWave * (wave - 1), maxAlive - enemies.Count);
        for (int i = 0; i < count; i++)
        {
            Vector3 p = spawnCenter + new Vector3(Random.Range(-spawnWidth / 2f, spawnWidth / 2f), Random.Range(-6f, 0f), Random.Range(0f, 1f));
            Enemy e = CreateEnemy(nextId++, p, 180f, true, PickKind(i));
            e.SetPath(path, p.y < climbUntil, climbUntil);
        }
    }

    // Кто придёт в этой волне: с 2-й ночи бегуны, с 3-й — плевальщики и громилы (их всё больше)
    EnemyKind PickKind(int index)
    {
        if (wave >= 3 && index < 1 + (wave - 3) / 2) return EnemyKind.Brute;
        float r = Random.value;
        if (wave >= 3 && r < 0.2f) return EnemyKind.Spitter;
        if (wave >= 2 && r < 0.5f) return EnemyKind.Runner;
        return EnemyKind.Crawler;
    }

    readonly Dictionary<EnemyKind, Material> kindMats = new Dictionary<EnemyKind, Material>();

    Material BodyFor(EnemyKind k)
    {
        if (bodyMaterial == null || k == EnemyKind.Crawler) return bodyMaterial;
        if (kindMats.TryGetValue(k, out Material m) && m != null) return m;
        m = new Material(bodyMaterial);
        Color c = k == EnemyKind.Runner ? new Color(0.25f, 0.08f, 0.1f) : k == EnemyKind.Brute ? new Color(0.12f, 0.09f, 0.08f) : new Color(0.12f, 0.2f, 0.1f);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        kindMats[k] = m;
        return m;
    }

    Material glowMat;
    Material Glow()
    {
        if (glowMat != null) return glowMat;
        glowMat = new Material(bodyMaterial != null ? bodyMaterial : new Material(Shader.Find("Universal Render Pipeline/Lit")));
        Color c = new Color(0.45f, 0.9f, 0.2f);
        if (glowMat.HasProperty("_BaseColor")) glowMat.SetColor("_BaseColor", c);
        if (glowMat.HasProperty("_EmissionColor")) { glowMat.EnableKeyword("_EMISSION"); glowMat.SetColor("_EmissionColor", c * 2.5f); }
        return glowMat;
    }

    Enemy CreateEnemy(int id, Vector3 pos, float yaw, bool simulated, EnemyKind kind)
    {
        Material body = BodyFor(kind);
        GameObject go = new GameObject("Enemy_" + id);
        go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));

        // Тело из частей: голова, туловище, руки, ноги (у плевальщика — ещё мешок с ядом)
        Transform vis = new GameObject("Visual").transform;
        vis.SetParent(go.transform, false);
        BodyPart[] parts = EnemyBody.Build(vis, kind, body, eyeMaterial, Glow());

        Enemy e = go.AddComponent<Enemy>();
        e.ApplyKind(kind, Mathf.Max(1, wave));
        vis.localScale = Vector3.one * e.bodyScale;
        e.Setup(id, simulated, vis, parts);
        enemies[id] = e;
        return e;
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

    // ---------- Удары игроков по тварям ----------
    // Удар считает хост. Тот, кто ударил, сразу сам показывает, как дёрнулось тело (LocalReact), — без задержки сети.
    public static void RequestHit(Enemy e, HitInfo h)
    {
        if (Instance == null || e == null) return;
        e.Flash();
        if (Net.IsHost) { e.ApplyHit(h, Net.LocalId); return; }
        FastBufferWriter w = Net.Writer(64);
        try
        {
            w.WriteValueSafe(e.id); w.WriteValueSafe(h.damage); w.WriteValueSafe(h.dir); w.WriteValueSafe((int)h.part);
            w.WriteValueSafe((int)h.type); w.WriteValueSafe(h.heavy); w.WriteValueSafe(h.embed);
            Net.ToHost(MsgHitReq, w);
        }
        finally { w.Dispose(); }
    }

    // Хост: тело дёрнулось / случилось что-то яркое (отрубил, казнь...). Рассылаем всем.
    // callout — надпись на экране у того, кто ударил.
    public static void React(Enemy e, int part, Vector3 dir, float strength, ulong attacker, int callout)
    {
        if (e == null) return;
        if (strength > 0f && attacker != Net.LocalId) e.LocalReact(part, dir, strength);
        if (attacker == Net.LocalId && callout != Callout.None) ShowCallout(callout);
        if (!Net.Online) return;
        FastBufferWriter w = Net.Writer(64);
        try
        {
            w.WriteValueSafe(e.id); w.WriteValueSafe(part); w.WriteValueSafe(dir); w.WriteValueSafe(strength);
            w.WriteValueSafe(attacker); w.WriteValueSafe(callout);
            Net.ToAllClients(MsgReact, w);
        }
        finally { w.Dispose(); }
    }

    static string calloutText;
    static float calloutUntil;
    public static void ShowCallout(int code)
    {
        if (code <= 0 || code >= Callout.Text.Length) return;
        ShowCallout(Callout.Text[code]);
    }
    public static void ShowCallout(string text)
    {
        calloutText = text;
        calloutUntil = Time.unscaledTime + 1.3f;
    }

    // Игрок парировал удар твари
    public static void RequestParry(int enemyId) => SendSimple(MsgParryReq, enemyId, id => Instance.Find(id)?.Parried());

    // Схватить тварь. Ответ придёт в GrabResult (у того, кто хватал)
    public static event System.Action<int, bool> GrabResult;

    public static void RequestGrab(Enemy e)
    {
        if (Instance == null || e == null) return;
        if (Net.IsHost) { Instance.ServerGrab(e.id, Net.LocalId); return; }
        FastBufferWriter w = Net.Writer(16);
        try { w.WriteValueSafe(e.id); Net.ToHost(MsgGrabReq, w); } finally { w.Dispose(); }
    }

    void ServerGrab(int id, ulong by)
    {
        Enemy e = Find(id);
        Transform t = PlayerTransform(by);
        bool ok = e != null && e.Grab(t, by);
        if (by == Net.LocalId) { GrabResult?.Invoke(id, ok); return; }
        FastBufferWriter w = Net.Writer(16);
        try { w.WriteValueSafe(id); w.WriteValueSafe(ok); Net.ToClient(MsgGrabRes, by, w); } finally { w.Dispose(); }
    }

    // Бросить схваченную тварь
    public static void RequestThrow(int enemyId, Vector3 velocity, float spin)
    {
        if (Instance == null) return;
        if (Net.IsHost) { Instance.Find(enemyId)?.Throw(velocity, spin); return; }
        FastBufferWriter w = Net.Writer(32);
        try { w.WriteValueSafe(enemyId); w.WriteValueSafe(velocity); w.WriteValueSafe(spin); Net.ToHost(MsgThrowReq, w); } finally { w.Dispose(); }
    }

    static void SendSimple(string msg, int id, System.Action<int> asHost)
    {
        if (Instance == null) return;
        if (Net.IsHost) { asHost(id); return; }
        FastBufferWriter w = Net.Writer(16);
        try { w.WriteValueSafe(id); Net.ToHost(msg, w); } finally { w.Dispose(); }
    }

    public Enemy Find(int id) => enemies.TryGetValue(id, out Enemy e) && e != null ? e : null;

    public Transform PlayerTransform(ulong owner)
    {
        foreach (PlayerInfo p in players) if (p.ownerId == owner && p.transform != null) return p.transform;
        RefreshPlayers();
        foreach (PlayerInfo p in players) if (p.ownerId == owner && p.transform != null) return p.transform;
        return null;
    }

    // Тварь ударила игрока (вызывает хост). attackerId — какая тварь (чтобы можно было её парировать)
    public static void HurtPlayer(ulong ownerId, float damage, int attackerId = -1)
    {
        if (ownerId == Net.LocalId)
        {
            if (PlayerHealth.Local != null) PlayerHealth.Local.TakeHit(damage, attackerId);
            return;
        }
        FastBufferWriter w = Net.Writer(16);
        try { w.WriteValueSafe(damage); w.WriteValueSafe(attackerId); Net.ToClient(MsgHurt, ownerId, w); } finally { w.Dispose(); }
    }

    // Глухой удар (тварь врезалась в стену, в землю, в колья): пыль и тряска камеры у тех, кто рядом
    // enemyId и fx: 0 — пыль, 1 — ядовитый взрыв, 2 — тварь расплющило (enemyId — какую)
    public static void Impact(Vector3 at, float strength, int enemyId = -1, int fx = 0)
    {
        LocalImpact(at, strength, enemyId, fx);
        if (!Net.Online) return;
        FastBufferWriter w = Net.Writer(40);
        try { w.WriteValueSafe(at); w.WriteValueSafe(strength); w.WriteValueSafe(enemyId); w.WriteValueSafe(fx); Net.ToAllClients(MsgFx, w); } finally { w.Dispose(); }
    }

    static Material dustMat;
    static void LocalImpact(Vector3 at, float strength, int enemyId, int fx)
    {
        if (fx == 2 && Instance != null) Instance.Find(enemyId)?.Squash();
        if (fx == 1) CombatFx.Poison(at, 26);
        if (dustMat == null)
        {
            Shader sh = Shader.Find("Universal Render Pipeline/Lit");
            dustMat = new Material(sh != null ? sh : Shader.Find("Standard"));
            Color c = new Color(0.35f, 0.3f, 0.25f);
            if (dustMat.HasProperty("_BaseColor")) dustMat.SetColor("_BaseColor", c);
        }
        int n = 3 + Mathf.RoundToInt(strength * 6f);
        for (int i = 0; i < n; i++)
        {
            GameObject d = GameObject.CreatePrimitive(PrimitiveType.Cube);
            d.layer = 2;
            Destroy(d.GetComponent<Collider>());
            d.transform.position = at + Random.insideUnitSphere * 0.4f;
            d.transform.localScale = Vector3.one * Random.Range(0.08f, 0.2f);
            d.transform.rotation = Random.rotation;
            d.GetComponent<Renderer>().sharedMaterial = dustMat;
            d.AddComponent<FlyAway>().velocity = Random.insideUnitSphere * 3f + Vector3.up * 2.5f;
            Destroy(d, 0.8f);
        }
        if (PlayerHealth.Local != null)
        {
            float dist = Vector3.Distance(PlayerHealth.Local.transform.position, at);
            if (dist < 14f) PlayerCamera.Shake(strength * 0.8f * (1f - dist / 14f));
        }
    }

    // ---------- Телекинез посоха ----------
    public static event System.Action<int, bool> TkResult;

    public static void RequestTk(Enemy e)
    {
        if (Instance == null || e == null) return;
        if (Net.IsHost) { Instance.ServerTk(e.id, Net.LocalId); return; }
        FastBufferWriter w = Net.Writer(16);
        try { w.WriteValueSafe(e.id); Net.ToHost(MsgTkReq, w); } finally { w.Dispose(); }
    }

    void ServerTk(int id, ulong by)
    {
        Enemy e = Find(id);
        Transform t = PlayerTransform(by);
        bool ok = e != null && e.GrabTk(t, by);
        if (by == Net.LocalId) { TkResult?.Invoke(id, ok); return; }
        FastBufferWriter w = Net.Writer(16);
        try { w.WriteValueSafe(id); w.WriteValueSafe(ok); Net.ToClient(MsgTkRes, by, w); } finally { w.Dispose(); }
    }

    // Куда тянуть поднятую тварь (часто, без гарантии доставки — следующая точка всё равно придёт)
    public static void TkMove(int enemyId, Vector3 point)
    {
        if (Instance == null) return;
        if (Net.IsHost) { Instance.Find(enemyId)?.SetTkPoint(point, Net.LocalId); return; }
        FastBufferWriter w = Net.Writer(24);
        try { w.WriteValueSafe(enemyId); w.WriteValueSafe(point); Net.ToHost(MsgTkMove, w, NetworkDelivery.Unreliable); } finally { w.Dispose(); }
    }

    // ---------- Заклинания и выстрелы (огненный шар, молния, луч бластера) ----------
    // Тот, кто стреляет, сам решает попадания; остальным — только картинка
    public static void CastSpell(int kind, Vector3 a, Vector3 b, float power, Transform shooterRoot)
    {
        Magic.Spawn(kind, a, b, power, true, shooterRoot);
        if (!Net.Online) return;
        if (Net.IsHost) SendSpell(kind, a, b, power, Net.LocalId);
        else
        {
            FastBufferWriter w = Net.Writer(48);
            try { w.WriteValueSafe(kind); w.WriteValueSafe(a); w.WriteValueSafe(b); w.WriteValueSafe(power); Net.ToHost(MsgSpellReq, w); } finally { w.Dispose(); }
        }
    }

    static void SendSpell(int kind, Vector3 a, Vector3 b, float power, ulong shooter)
    {
        FastBufferWriter w = Net.Writer(56);
        try { w.WriteValueSafe(kind); w.WriteValueSafe(a); w.WriteValueSafe(b); w.WriteValueSafe(power); w.WriteValueSafe(shooter); Net.ToAllClients(MsgSpell, w); } finally { w.Dispose(); }
    }

    // ---------- Стрелы ----------
    // Стрелок сам решает, во что попала его стрела (урон всё равно идёт через хоста). Остальным — летящая картинка.
    public static void ShootArrow(Vector3 origin, Vector3 vel, bool heavy, Transform shooterRoot)
    {
        Arrow.Spawn(origin, vel, true, heavy, shooterRoot, true);
        if (!Net.Online) return;
        if (Net.IsHost) SendArrow(origin, vel, heavy, Net.LocalId);
        else
        {
            FastBufferWriter w = Net.Writer(48);
            try { w.WriteValueSafe(origin); w.WriteValueSafe(vel); w.WriteValueSafe(heavy); Net.ToHost(MsgArrowReq, w); } finally { w.Dispose(); }
        }
    }

    static void SendArrow(Vector3 origin, Vector3 vel, bool heavy, ulong shooter)
    {
        FastBufferWriter w = Net.Writer(56);
        try { w.WriteValueSafe(origin); w.WriteValueSafe(vel); w.WriteValueSafe(heavy); w.WriteValueSafe(shooter); Net.ToAllClients(MsgArrow, w); } finally { w.Dispose(); }
    }

    // ---------- Плевки ----------
    public static void Spit(Vector3 from, Vector3 to, float damage)
    {
        Projectile.Launch(from, to, true, damage);
        if (!Net.Online) return;
        FastBufferWriter w = Net.Writer(32);
        try { w.WriteValueSafe(from); w.WriteValueSafe(to); Net.ToAllClients(MsgSpit, w); } finally { w.Dispose(); }
    }

    // Плевок упал (на хосте): ранит игроков рядом
    public void SplashAt(Vector3 point, float radius, float damage)
    {
        foreach (PlayerInfo p in players)
        {
            if (p.transform == null || p.dead) continue;
            if (Vector3.Distance(p.transform.position + Vector3.up * 0.6f, point) < radius) HurtPlayer(p.ownerId, damage);
        }
    }

    // ---------- Сеть ----------
    void SendState()
    {
        if (!Net.Online) return;
        int n = enemies.Count;
        var ids = new int[n]; var pos = new Vector3[n]; var yaw = new float[n]; var hp = new float[n]; var climb = new bool[n]; var kinds = new int[n]; var states = new int[n]; var held = new int[n]; var lost = new int[n]; var sides = new int[n];
        int i = 0;
        foreach (var kv in enemies)
        {
            if (kv.Value == null) { n--; continue; }
            ids[i] = kv.Key; pos[i] = kv.Value.transform.position; yaw[i] = kv.Value.transform.eulerAngles.y; hp[i] = kv.Value.HealthFraction; climb[i] = kv.Value.Climbing; kinds[i] = (int)kv.Value.kind;
            states[i] = (int)kv.Value.CurrentState; held[i] = kv.Value.HeldBy; lost[i] = kv.Value.LostMask; sides[i] = kv.Value.AttackSide;
            i++;
        }
        System.Array.Resize(ref ids, i); System.Array.Resize(ref pos, i); System.Array.Resize(ref yaw, i); System.Array.Resize(ref hp, i); System.Array.Resize(ref climb, i); System.Array.Resize(ref kinds, i); System.Array.Resize(ref states, i); System.Array.Resize(ref held, i); System.Array.Resize(ref lost, i); System.Array.Resize(ref sides, i);
        FastBufferWriter w = Net.Writer(64 + i * 40);
        try
        {
            w.WriteValueSafe(wave);
            w.WriteValueSafe(ids); w.WriteValueSafe(pos); w.WriteValueSafe(yaw); w.WriteValueSafe(hp); w.WriteValueSafe(climb); w.WriteValueSafe(kinds); w.WriteValueSafe(states); w.WriteValueSafe(held); w.WriteValueSafe(lost); w.WriteValueSafe(sides);
            Net.ToAllClients(MsgState, w, NetworkDelivery.ReliableFragmentedSequenced);
        }
        finally { w.Dispose(); }
    }

    void Register(CustomMessagingManager cmm)
    {
        cmm.RegisterNamedMessageHandler(MsgState, (sender, r) =>
        {
            r.ReadValueSafe(out int w);
            r.ReadValueSafe(out int[] ids); r.ReadValueSafe(out Vector3[] pos); r.ReadValueSafe(out float[] yaw); r.ReadValueSafe(out float[] hp); r.ReadValueSafe(out bool[] climb); r.ReadValueSafe(out int[] kinds);
            r.ReadValueSafe(out int[] states); r.ReadValueSafe(out int[] held); r.ReadValueSafe(out int[] lost); r.ReadValueSafe(out int[] sides);
            if (Net.IsHost) return;
            wave = w;
            var alive = new HashSet<int>(ids);
            for (int i = 0; i < ids.Length; i++)
            {
                if (!enemies.TryGetValue(ids[i], out Enemy e) || e == null) e = CreateEnemy(ids[i], pos[i], yaw[i], false, i < kinds.Length ? (EnemyKind)kinds[i] : EnemyKind.Crawler);
                e.SetNetState(pos[i], yaw[i], hp[i], i < climb.Length && climb[i], i < states.Length ? states[i] : 0, i < held.Length ? held[i] : -1, i < lost.Length ? lost[i] : 0, i < sides.Length ? sides[i] : 0);
            }
            var gone = new List<int>();
            foreach (var kv in enemies) if (!alive.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (int id in gone) { if (enemies[id] != null) enemies[id].Die(); enemies.Remove(id); }
        });
        cmm.RegisterNamedMessageHandler(MsgHitReq, (sender, r) =>
        {
            r.ReadValueSafe(out int id); r.ReadValueSafe(out float dmg); r.ReadValueSafe(out Vector3 dir); r.ReadValueSafe(out int part);
            r.ReadValueSafe(out int type); r.ReadValueSafe(out bool heavy); r.ReadValueSafe(out bool embed);
            if (Net.IsHost) Find(id)?.ApplyHit(new HitInfo { damage = dmg, dir = dir, part = (BodyPartType)part, type = (HitType)type, heavy = heavy, embed = embed }, sender);
        });
        cmm.RegisterNamedMessageHandler(MsgHurt, (sender, r) =>
        {
            r.ReadValueSafe(out float dmg); r.ReadValueSafe(out int attacker);
            if (PlayerHealth.Local != null) PlayerHealth.Local.TakeHit(dmg, attacker);
        });
        cmm.RegisterNamedMessageHandler(MsgParryReq, (sender, r) => { r.ReadValueSafe(out int id); if (Net.IsHost) Find(id)?.Parried(); });
        cmm.RegisterNamedMessageHandler(MsgGrabReq, (sender, r) => { r.ReadValueSafe(out int id); if (Net.IsHost) ServerGrab(id, sender); });
        cmm.RegisterNamedMessageHandler(MsgGrabRes, (sender, r) => { r.ReadValueSafe(out int id); r.ReadValueSafe(out bool ok); GrabResult?.Invoke(id, ok); });
        cmm.RegisterNamedMessageHandler(MsgThrowReq, (sender, r) =>
        {
            r.ReadValueSafe(out int id); r.ReadValueSafe(out Vector3 vel); r.ReadValueSafe(out float spin);
            if (Net.IsHost) Find(id)?.Throw(vel, spin);
        });
        cmm.RegisterNamedMessageHandler(MsgFx, (sender, r) =>
        {
            r.ReadValueSafe(out Vector3 at); r.ReadValueSafe(out float str); r.ReadValueSafe(out int eid); r.ReadValueSafe(out int fx);
            if (!Net.IsHost) LocalImpact(at, str, eid, fx);
        });
        cmm.RegisterNamedMessageHandler(MsgReact, (sender, r) =>
        {
            r.ReadValueSafe(out int id); r.ReadValueSafe(out int part); r.ReadValueSafe(out Vector3 dir); r.ReadValueSafe(out float str);
            r.ReadValueSafe(out ulong attacker); r.ReadValueSafe(out int callout);
            if (Net.IsHost) return;
            Enemy e = Find(id);
            if (e != null && str > 0f && attacker != Net.LocalId) e.LocalReact(part, dir, str);
            if (attacker == Net.LocalId) ShowCallout(callout);
        });
        cmm.RegisterNamedMessageHandler(MsgSpit, (sender, r) =>
        {
            r.ReadValueSafe(out Vector3 from); r.ReadValueSafe(out Vector3 to);
            if (!Net.IsHost) Projectile.Launch(from, to, false, 0f);
        });
        cmm.RegisterNamedMessageHandler(MsgTkReq, (sender, r) => { r.ReadValueSafe(out int id); if (Net.IsHost) ServerTk(id, sender); });
        cmm.RegisterNamedMessageHandler(MsgTkRes, (sender, r) => { r.ReadValueSafe(out int id); r.ReadValueSafe(out bool ok); TkResult?.Invoke(id, ok); });
        cmm.RegisterNamedMessageHandler(MsgTkMove, (sender, r) =>
        {
            r.ReadValueSafe(out int id); r.ReadValueSafe(out Vector3 p);
            if (Net.IsHost) Find(id)?.SetTkPoint(p, sender);
        });
        cmm.RegisterNamedMessageHandler(MsgSpellReq, (sender, r) =>
        {
            r.ReadValueSafe(out int k); r.ReadValueSafe(out Vector3 a); r.ReadValueSafe(out Vector3 b); r.ReadValueSafe(out float pw);
            if (!Net.IsHost) return;
            Transform t = PlayerTransform(sender);
            Magic.Spawn(k, a, b, pw, false, t != null ? t.root : null);
            SendSpell(k, a, b, pw, sender);
        });
        cmm.RegisterNamedMessageHandler(MsgSpell, (sender, r) =>
        {
            r.ReadValueSafe(out int k); r.ReadValueSafe(out Vector3 a); r.ReadValueSafe(out Vector3 b); r.ReadValueSafe(out float pw); r.ReadValueSafe(out ulong shooter);
            if (shooter == Net.LocalId) return;
            Transform t = PlayerTransform(shooter);
            Magic.Spawn(k, a, b, pw, false, t != null ? t.root : null);
        });
        cmm.RegisterNamedMessageHandler(MsgArrowReq, (sender, r) =>
        {
            r.ReadValueSafe(out Vector3 o); r.ReadValueSafe(out Vector3 v); r.ReadValueSafe(out bool hv);
            if (!Net.IsHost) return;
            Transform t = PlayerTransform(sender);
            Arrow.Spawn(o, v, false, hv, t != null ? t.root : null, false);
            SendArrow(o, v, hv, sender);
        });
        cmm.RegisterNamedMessageHandler(MsgArrow, (sender, r) =>
        {
            r.ReadValueSafe(out Vector3 o); r.ReadValueSafe(out Vector3 v); r.ReadValueSafe(out bool hv); r.ReadValueSafe(out ulong shooter);
            if (shooter == Net.LocalId) return;
            Transform t = PlayerTransform(shooter);
            Arrow.Spawn(o, v, false, hv, t != null ? t.root : null, false);
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

        // Надпись приёма: «КАЗНЬ!», «ОТРУБИЛ НОГУ!»...
        if (Hud.Popups && Time.unscaledTime < calloutUntil && !string.IsNullOrEmpty(calloutText))
        {
            float k = Mathf.Clamp01((calloutUntil - Time.unscaledTime) / 0.3f);
            Color old = GUI.color;
            GUI.color = new Color(1f, 0.82f, 0.3f, k);
            GUI.Label(new Rect(0, Screen.height * 0.26f, Screen.width, 34), calloutText, Hud.CenterBig);
            GUI.color = old;
        }

        // Полоски здоровья над тварями
        Camera cam = Camera.main;
        if (cam == null) return;
        foreach (var kv in enemies)
        {
            Enemy e = kv.Value;
            if (e == null) continue;
            Vector3 world = e.transform.position + Vector3.up * (2.1f * e.bodyScale);
            Enemy.State st = e.CurrentState;
            string label = st == Enemy.State.Stagger ? "ОТКРЫТ — тяжёлый удар!" : st == Enemy.State.Kneel ? "НА КОЛЕНЕ — руби по голове сверху!" :
                           st == Enemy.State.Pinned ? "ПРИГВОЖДЁН" : null;
            if (!Hud.Popups) label = null;   // надписи над тварями убраны (вернуть — Hud.Popups = true)
            bool open = label != null;
            if (e.HealthFraction >= 0.999f && !open) continue;
            if (Vector3.Distance(world, cam.transform.position) > 30f) continue;
            Vector3 s = cam.WorldToScreenPoint(world);
            if (s.z < 0f) continue;
            Hud.Bar(new Rect(s.x - 30, Screen.height - s.y, 60, 6), e.HealthFraction, new Color(0.8f, 0.2f, 0.9f), null);
            if (open)
            {
                Color old = GUI.color; GUI.color = new Color(1f, 0.85f, 0.2f);
                GUI.Label(new Rect(s.x - 160, Screen.height - s.y - 30, 320, 24), label, Hud.Center);
                GUI.color = old;
            }
        }
    }
}
