using System.Collections.Generic;
using UnityEngine;

public enum EnemyKind { Crawler, Runner, Brute, Spitter }

// Каким движением ударили. Направление удара выбирается мышью (см. PlayerCombat).
//   Slash — рубящий вбок, Chop — сверху вниз, Uppercut — снизу вверх, Thrust — выпад (колющий),
//   Cross — два рубящих крест-накрест, RipOut — вырвать застрявший клинок.
public enum HitType { Slash, Chop, Uppercut, Thrust, Kick, Tool, RipOut, Cross, Arrow, Trip, Fire, Lightning, Blaster }

// Всё про один удар
public struct HitInfo
{
    public float damage;
    public Vector3 dir;          // куда двигался клинок (или нога) в момент удара
    public BodyPartType part;    // куда попали
    public HitType type;
    public bool heavy;           // сильный удар (клинок шёл очень быстро)
    public bool embed;           // клинок вошёл в туловище и остался там (тварь насажена)
}

// Надписи на экране у того, кто ударил
public static class Callout
{
    public const int None = 0, Execution = 1, Head = 2, Arm = 3, Leg = 4, Sack = 5, Kneel = 6, Disarm = 7, Launch = 8, Air = 9,
                     Pinned = 10, Wall = 11, Spikes = 12, Crushed = 13, RipOut = 14, Finisher = 15, Cross = 16, Impaled = 17, Trip = 18;
    public static readonly string[] Text =
    {
        "", "КАЗНЬ!", "ГОЛОВА С ПЛЕЧ!", "ОТРУБИЛ РУКУ!", "ОТРУБИЛ НОГУ!", "МЕШОК ЛОПНУЛ!", "НА КОЛЕНИ!", "ВЫБИЛ УДАР!", "ПОДБРОС!", "В ПОЛЁТЕ!",
        "ПРИГВОЖДЁН!", "ОБ СТЕНУ!", "НА КОЛЬЯ!", "РАЗДАВЛЕН!", "ВЫРВАЛ КЛИНОК!", "ДОБИВАНИЕ!", "КРЕСТ!", "НАСАДИЛ!", "ПОДСЕЧКА!"
    };
}

// Тварь с Тёмной половины. Виды:
//   Ползун — обычная. Бегун — быстрый и слабый. Громила — огромный, медленный, ломает стены, его почти не отбросить.
//   Плевальщик — держится на расстоянии и плюётся ядом. На спине мешок с ядом — проткни, и он взорвётся.
//
// Тело собрано из частей (BodyPart): голова, туловище, две руки, две ноги. Каждая часть:
//   - принимает свой урон (голова ×1.8, руки и ноги меньше) и может быть отрублена;
//   - по-своему отвечает: по голове — оглушение, по ноге — хромает и падает на колено, по руке — сбивает замах;
//   - физически дёргается от удара в ту сторону, куда шёл клинок.
// Без ноги тварь хромает, без двух — ползёт. Без рук бьёт слабо. Без головы — всё.
//
// Как она дерётся (думает только хост):
//   - Нападают на одного игрока максимум двое (очередь раздаёт EnemyManager). Остальные кружат вокруг.
//   - Перед ударом тварь ЗАМАХИВАЕТСЯ (поднимает когти и краснеет) — сбей замах ударом по руке, парируй или уйди перекатом.
//   - Выпад в туловище — клинок застревает, тварь «насажена». Дотолкай её до стены — пригвоздишь.
//   - Сильный отброс в стену — тварь расплющивается. Пинок и бросок — летит физическим телом.
public class Enemy : MonoBehaviour
{
    public enum State { Move, WindUp, Recover, Flinch, Stagger, Grabbed, Thrown, Down, Kneel, Impaled, Pinned }

    public int id;
    public float maxHealth = 60f;
    public float speed = 3.2f;
    public float attackRange = 1.9f;
    public float attackCooldown = 1.3f;
    public float playerDamage = 12f;
    public float buildingDamage = 30f;
    public float aggroRange = 18f;   // с какого расстояния замечает игрока
    public float climbSpeed = 2.5f;  // как быстро карабкается по обрыву
    public float windUpTime = 0.55f; // замах перед ударом
    public EnemyKind kind = EnemyKind.Crawler;
    public float bodyScale = 1f;
    public float knockScale = 1f;    // насколько отлетает от ударов
    public float shootRange;         // >0 — умеет плеваться
    public float preferRange = 10f;
    public float shootCooldown = 2.6f;

    public static readonly List<Enemy> All = new List<Enemy>();

    public float Health { get; set; }
    public float HealthFraction { get; set; } = 1f;
    public bool Simulated { get; private set; }
    public State CurrentState { get; private set; } = State.Move;
    public bool Climbing => climbing;
    public bool IsOpen => CurrentState == State.Stagger;   // шатается — тяжёлым ударом можно добить
    public bool CanBeGrabbed => (kind != EnemyKind.Brute || CurrentState == State.Stagger || CurrentState == State.Kneel) &&
                                CurrentState != State.Impaled && CurrentState != State.Pinned;
    public bool OnBlade => CurrentState == State.Impaled || CurrentState == State.Pinned;
    public int HeldBy { get; private set; } = -1;           // кто держит (руками или на клинке), -1 — никто
    public ulong TargetOwner { get; private set; } = Net.NoOne;
    public bool HasToken;          // можно нападать (раздаёт EnemyManager)
    public float TokenCooldownUntil;
    public int LostMask { get; private set; }               // какие части отрублены (биты BodyPartType)
    public int AttackSide { get; private set; }             // откуда бьёт: -1 левой лапой, 1 правой, 0 сверху обеими
    public float WindProgress => CurrentState == State.WindUp ? Mathf.Clamp01((Time.time - windStart) / Mathf.Max(0.1f, windUpTime)) : 0f;
    public BodyPart[] Parts => parts;

    const float BodyHeight = 1.9f;

    CharacterController cc;
    Transform visual;
    BodyPart[] parts;
    readonly float[] partHp = new float[7];
    readonly float[] partMax = new float[7];
    float verticalVelocity;
    float nextAttack, nextShot;
    Vector3 detourDir;
    float detourUntil;
    float lunge;
    float flash;
    float windTint;
    Renderer[] renderers;
    Color[] baseColors;
    MaterialPropertyBlock block;
    BuildPiece targetPiece;
    BuildPiece strikePiece;
    float stateUntil;
    float circleSign;
    Transform holder;
    bool telekinetic;                 // держат посохом на расстоянии, а не руками
    Vector3 tkPoint;
    float burnUntil;                  // горит после огненного шара
    public bool Burning => Time.time < burnUntil;
    public void ShowBurn(float duration) { burnUntil = Mathf.Max(burnUntil, Time.time + duration); }
    float nextFlame;
    Rigidbody rb;
    CapsuleCollider throwCol;
    float thrownAt, lastImpact;
    float visualSpin;
    float limpUntil, kneelCooldown;
    float twist;                 // вращение от бокового удара (град/сек)
    float squash;                // расплющило о стену (0..1)
    float impaledAt;
    Vector3 lastHitDir = Vector3.forward;
    ulong lastAttacker = Net.NoOne;
    float walkPhase, visSpeed;
    Vector3 lastVisPos;
    bool dead;
    bool spiked;                 // висит на кольях
    bool freePin;                // пригвождён стрелой к стене
    float windStart;
    Vector3 visVel;

    Vector3 netPos;
    float netYaw;

    // Путь: точки, по которым идёт к деревне
    Vector3[] path;
    int pathIndex;
    float sideOffset;
    bool climbing;
    float climbTop;
    Vector3 knock;
    Vector3 lastPos;
    float stuckTime;

    void OnEnable() { All.Add(this); }
    void OnDisable() { All.Remove(this); }

    public bool Lost(BodyPartType p) => (LostMask & (1 << (int)p)) != 0;
    int LegsLost => (Lost(BodyPartType.LegL) ? 1 : 0) + (Lost(BodyPartType.LegR) ? 1 : 0);
    int ArmsLost => (Lost(BodyPartType.ArmL) ? 1 : 0) + (Lost(BodyPartType.ArmR) ? 1 : 0);
    float SpeedMul => LegsLost == 2 ? 0.25f : LegsLost == 1 ? 0.3f : Time.time < limpUntil ? 0.6f : 1f;
    float DamageMul => ArmsLost == 2 ? 0.35f : ArmsLost == 1 ? 0.65f : 1f;

    // Характеристики вида. wave — номер набега (с каждым тварь крепче)
    public void ApplyKind(EnemyKind k, int wave)
    {
        kind = k;
        int w = Mathf.Max(0, wave - 1);
        switch (k)
        {
            case EnemyKind.Runner:
                maxHealth = 30f + 5f * w; speed = 6.5f; playerDamage = 7f; buildingDamage = 10f; windUpTime = 0.35f;
                attackRange = 1.6f; attackCooldown = 0.7f; bodyScale = 0.75f; aggroRange = 28f; knockScale = 1.4f; break;
            case EnemyKind.Brute:
                maxHealth = 280f + 45f * w; speed = 2.1f; playerDamage = 30f; buildingDamage = 140f; windUpTime = 0.95f;
                attackRange = 2.6f; attackCooldown = 2f; bodyScale = 1.55f; aggroRange = 14f; knockScale = 0.15f; break;
            case EnemyKind.Spitter:
                maxHealth = 45f + 6f * w; speed = 3f; playerDamage = 14f; buildingDamage = 20f; windUpTime = 0.5f;
                attackRange = 1.9f; attackCooldown = 1.4f; bodyScale = 0.95f; aggroRange = 22f; shootRange = 18f; break;
            default:
                maxHealth = 60f + 10f * w; break;
        }
        Health = maxHealth;
    }

    // Задать дорогу. climbUntil — до какой высоты лезть вверх по обрыву (если тварь в пропасти)
    public void SetPath(Vector3[] points, bool climb, float climbUntil)
    {
        path = points;
        pathIndex = 0;
        sideOffset = Random.Range(-0.4f, 0.4f); // чуть в сторону, чтобы не шли строем, но попадали в ворота
        climbing = climb;
        climbTop = climbUntil;
        lastPos = transform.position;
    }

    public void Knock(Vector3 force) { knock += force * knockScale; }

    public void Setup(int enemyId, bool simulated, Transform visualRoot, BodyPart[] bodyParts)
    {
        id = enemyId;
        Simulated = simulated;
        visual = visualRoot;
        parts = bodyParts ?? new BodyPart[7];
        Health = maxHealth;
        netPos = transform.position;
        netYaw = transform.eulerAngles.y;
        lastVisPos = transform.position;
        circleSign = (enemyId % 2 == 0) ? 1f : -1f;

        // «Запас прочности» частей тела
        float limb = kind == EnemyKind.Brute ? 0.22f : 0.32f;
        partMax[(int)BodyPartType.Head] = maxHealth * 0.55f;
        partMax[(int)BodyPartType.ArmL] = partMax[(int)BodyPartType.ArmR] = maxHealth * limb;
        partMax[(int)BodyPartType.LegL] = partMax[(int)BodyPartType.LegR] = maxHealth * (limb + 0.05f);
        partMax[(int)BodyPartType.Sack] = 1f;
        partMax[(int)BodyPartType.Torso] = 99999f;
        for (int i = 0; i < 7; i++) partHp[i] = partMax[i];
        foreach (BodyPart bp in parts) if (bp != null) bp.owner = this;

        float h = BodyHeight * bodyScale;
        if (simulated)
        {
            cc = gameObject.AddComponent<CharacterController>();
            cc.radius = 0.45f * bodyScale; cc.height = h; cc.center = new Vector3(0f, h / 2f, 0f);
            cc.stepOffset = 0.4f;
        }
        else
        {
            CapsuleCollider col = gameObject.AddComponent<CapsuleCollider>();
            col.radius = 0.45f * bodyScale; col.height = h; col.center = new Vector3(0f, h / 2f, 0f);
        }

        renderers = GetComponentsInChildren<Renderer>();
        baseColors = new Color[renderers.Length];
        for (int i = 0; i < renderers.Length; i++)
        {
            Material m = renderers[i].sharedMaterial;
            baseColors[i] = m != null && m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : Color.gray;
        }
        block = new MaterialPropertyBlock();
    }

    void SetState(State s, float duration)
    {
        if (s == State.WindUp && CurrentState != State.WindUp)
        {
            windStart = Time.time;
            AttackSide = kind == EnemyKind.Brute ? (Random.value < 0.5f ? 0 : (Random.value < 0.5f ? -1 : 1)) : Random.Range(-1, 2);
        }
        CurrentState = s;
        stateUntil = Time.time + duration;
    }

    // =====================================================================
    // Мозги (только у хоста)
    // =====================================================================
    public void Simulate(List<EnemyManager.PlayerInfo> players, Vector3 villageCenter)
    {
        HealthFraction = Mathf.Max(0f, Health / maxHealth);

        // горит: тикает урон
        if (Burning && Health > 0f) { Health -= 5f * Time.deltaTime; HealthFraction = Mathf.Max(0f, Health / maxHealth); }

        // Вращение от бокового удара
        if (Mathf.Abs(twist) > 1f && CurrentState != State.Thrown)
        {
            transform.Rotate(0f, twist * Time.deltaTime, 0f);
            twist = Mathf.MoveTowards(twist, 0f, 1100f * Time.deltaTime);
        }

        // ---- Лезет из пропасти ----
        if (climbing) { Climb(); return; }

        // ---- Состояния, когда тварь не управляет собой ----
        switch (CurrentState)
        {
            case State.Grabbed: HoldInHands(); return;
            case State.Impaled: HoldOnBlade(); return;
            case State.Pinned: StayPinned(); return;
            case State.Thrown: FlyCheck(); return;
            case State.Recover:
                // удар с шагом вперёд — вкладывается всем телом
                MoveBody(transform.forward, lunge > 0.6f && LegsLost == 0 ? 4.5f : 0f);
                if (Time.time >= stateUntil) SetState(State.Move, 0f);
                return;
            case State.Down:
            case State.Flinch:
            case State.Stagger:
            case State.Kneel:
                MoveBody(Vector3.zero, 0f);
                if (Time.time >= stateUntil) SetState(State.Move, 0f);
                return;
        }

        // ---- Цель: ближайший живой игрок рядом (держимся за неё, пока не убежит далеко) ----
        EnemyManager.PlayerInfo target = null;
        float best = float.MaxValue;
        foreach (var p in players)
        {
            if (p.transform == null || p.dead) continue;
            float d = Vector3.Distance(p.transform.position, transform.position);
            bool current = p.ownerId == TargetOwner;
            float limit = current ? aggroRange * 1.5f : aggroRange;
            float score = current ? d - 4f : d;
            if (d < limit && score < best) { best = score; target = p; }
        }
        TargetOwner = target != null ? target.ownerId : Net.NoOne;

        // ---- Замах: ждём и бьём ----
        if (CurrentState == State.WindUp)
        {
            if (target != null && strikePiece == null) Face(target.transform.position - transform.position);
            MoveBody(Vector3.zero, 0f);
            if (Time.time >= stateUntil) Strike(target);
            return;
        }

        Vector3 goal;
        float moveSpeed = speed;
        bool faceGoal = true;
        Vector3 faceDir = Vector3.zero;

        if (target != null)
        {
            Vector3 tp = target.transform.position;
            Vector3 toT = tp - transform.position; toT.y = 0f;
            float dT = toT.magnitude;
            faceDir = toT;
            faceGoal = false;

            if (shootRange > 0f && dT > attackRange + 1.5f && dT < shootRange)
            {
                // Плевальщик: держит расстояние и плюётся
                goal = dT < preferRange ? transform.position - toT.normalized * 2f : tp;
                if (dT > preferRange - 1f && dT < preferRange + 1f) moveSpeed = 0f;
                if (Time.time >= nextShot)
                {
                    nextShot = Time.time + shootCooldown;
                    lunge = 1f;
                    EnemyManager.Spit(transform.position + Vector3.up * 1.4f * bodyScale + toT.normalized * 0.6f,
                                      tp + Vector3.up * 0.6f, playerDamage);
                }
            }
            else if (HasToken)
            {
                // Наша очередь: подходим и замахиваемся
                goal = tp;
                if (dT < attackRange && Time.time >= nextAttack)
                {
                    strikePiece = null;
                    SetState(State.WindUp, windUpTime);
                    return;
                }
                if (dT < attackRange * 0.8f) moveSpeed = 0f;
            }
            else
            {
                // Ждём очереди: кружим вокруг на расстоянии
                float radius = 4.5f + (id % 3) * 1.1f;
                float ang = (id * 137.5f + Time.time * 14f * circleSign) * Mathf.Deg2Rad;
                goal = tp + new Vector3(Mathf.Sin(ang), 0f, Mathf.Cos(ang)) * radius;
                moveSpeed = speed * 0.6f;
                if ((goal - transform.position).sqrMagnitude < 0.4f) moveSpeed = 0f;
            }
        }
        else
        {
            // Идём по точкам дороги, на последней — ломаем деревню
            Vector3 wp = villageCenter;
            if (path != null && pathIndex < path.Length)
            {
                wp = path[pathIndex] + Vector3.right * sideOffset;
                Vector3 d = wp - transform.position; d.y = 0f;
                if (d.magnitude < 4f || transform.position.z < wp.z - 1f) pathIndex++; // дошли или уже прошли
            }
            if (path == null || pathIndex >= path.Length)
            {
                if (Vector3.Distance(transform.position, villageCenter) < 22f)
                {
                    if (targetPiece == null || !targetPiece.isActiveAndEnabled) targetPiece = NearestPiece(24f);
                    wp = targetPiece != null ? targetPiece.Center : villageCenter;
                }
                else wp = villageCenter;
            }
            goal = wp;
        }

        moveSpeed *= SpeedMul; // хромает или ползёт

        Vector3 to = goal - transform.position; to.y = 0f;
        Vector3 dir = to.sqrMagnitude > 0.0001f ? to.normalized : transform.forward;
        if (Time.time < detourUntil) dir = detourDir;

        // Препятствие впереди: постройку ломаем (с замахом), своих и деревья обходим
        if (moveSpeed > 0f && Physics.SphereCast(transform.position + Vector3.up * 0.9f * bodyScale, 0.4f, dir, out RaycastHit hit,
                                                 1.0f + 0.45f * bodyScale, ~(1 << 2), QueryTriggerInteraction.Ignore))
        {
            BuildPiece bp = hit.collider.GetComponentInParent<BuildPiece>();
            Enemy other = hit.collider.GetComponentInParent<Enemy>();
            if (bp != null && (target == null || HasToken))
            {
                if (Time.time >= nextAttack)
                {
                    strikePiece = bp;
                    SetState(State.WindUp, windUpTime * 0.7f);
                    return;
                }
                moveSpeed = 0f;
            }
            else if (other != null && other != this)
                dir = Quaternion.Euler(0f, 50f * circleSign, 0f) * dir; // обходим своих
            else if (bp == null && hit.collider.GetComponentInParent<NetworkPlayer>() == null &&
                     hit.collider.GetComponentInParent<PlayerMovement>() == null && Time.time >= detourUntil)
            {
                detourDir = Quaternion.Euler(0f, Random.value < 0.5f ? -70f : 70f, 0f) * dir;
                detourUntil = Time.time + 1.2f;
                dir = detourDir;
            }
        }

        // Застрял — пробуем в сторону
        if (moveSpeed > 0f && Time.time >= detourUntil)
        {
            Vector3 moved = transform.position - lastPos; moved.y = 0f;
            stuckTime = moved.magnitude < moveSpeed * Time.deltaTime * 0.2f ? stuckTime + Time.deltaTime : 0f;
            if (stuckTime > 1f)
            {
                stuckTime = 0f;
                detourDir = Quaternion.Euler(0f, Random.value < 0.5f ? -90f : 90f, 0f) * dir;
                detourUntil = Time.time + 1.5f;
            }
        }
        lastPos = transform.position;

        MoveBody(dir, moveSpeed);
        Face(faceGoal ? dir : faceDir);
    }

    // Удар после замаха: попадёт, только если игрок всё ещё рядом и перед тварью
    void Strike(EnemyManager.PlayerInfo target)
    {
        lunge = 1f;
        nextAttack = Time.time + attackCooldown;
        if (strikePiece != null)
        {
            if (strikePiece.isActiveAndEnabled) BuildingManager.Damage(strikePiece, buildingDamage * DamageMul);
            strikePiece = null;
        }
        else if (target != null && target.transform != null)
        {
            Vector3 to = target.transform.position - transform.position; to.y = 0f;
            if (to.magnitude < attackRange + 0.8f && Vector3.Angle(transform.forward, to) < 70f)
                EnemyManager.HurtPlayer(target.ownerId, playerDamage * DamageMul, id);
        }
        // Ударила — уступает очередь другим
        HasToken = false;
        TokenCooldownUntil = Time.time + 1.2f + Random.value * 0.8f;
        SetState(State.Recover, kind == EnemyKind.Brute ? 0.9f : 0.45f);
    }

    void Climb()
    {
        bool atTop = transform.position.y >= climbTop;
        Vector3 over = Vector3.zero;
        if (atTop)
        {
            over = path != null && path.Length > 0 ? path[0] - transform.position : Vector3.back;
            over.y = 0f;
            over = over.sqrMagnitude > 0.01f ? over.normalized : Vector3.back;
            transform.rotation = Quaternion.LookRotation(over);
        }
        cc.Move(((atTop ? Vector3.zero : Vector3.up * climbSpeed) + over * speed + knock) * Time.deltaTime);
        knock = Vector3.MoveTowards(knock, Vector3.zero, 20f * Time.deltaTime);
        if (atTop && Physics.Raycast(transform.position + Vector3.up * 0.5f, Vector3.down, out RaycastHit ground, 1.5f, ~(1 << 2), QueryTriggerInteraction.Ignore)
            && ground.collider.GetComponentInParent<Enemy>() == null)
        {
            climbing = false; // под ногами земля — дальше идёт как обычно
            verticalVelocity = 0f;
        }
    }

    // Движение с гравитацией, отбрасыванием и «расталкиванием» соседей (чтобы не слипались в кучу)
    void MoveBody(Vector3 dir, float moveSpeed)
    {
        if (cc == null || !cc.enabled) return;
        Vector3 sep = Vector3.zero;
        foreach (Enemy o in All)
        {
            if (o == this || o.CurrentState == State.Thrown) continue;
            Vector3 d = transform.position - o.transform.position; d.y = 0f;
            float m = d.magnitude, min = 1.1f * (bodyScale + o.bodyScale) * 0.5f + 0.3f;
            if (m > 0.01f && m < min) sep += d / m * (min - m);
        }
        if (cc.isGrounded && verticalVelocity < 0f) verticalVelocity = -2f;
        verticalVelocity += -20f * Time.deltaTime;
        // разгон и торможение, а не мгновенный старт — движется как тело с весом
        float mdt = Time.deltaTime * TS;
        moveVel = Vector3.Lerp(moveVel, dir * moveSpeed, 1f - Mathf.Exp(-7f * mdt));
        Vector3 v = moveVel + sep * 3f + Vector3.up * verticalVelocity + knock;
        knock = Vector3.Lerp(knock, Vector3.zero, 1f - Mathf.Exp(-5f * mdt)); // плавно гаснет — тело «тяжёлое»
        cc.Move(v * mdt);
    }

    // Отброшенная тварь врезалась в стену — расплющило
    void OnControllerColliderHit(ControllerColliderHit hit)
    {
        if (!Simulated || climbing || knock.magnitude < 5f || Time.time - lastImpact < 0.3f) return;
        if (Mathf.Abs(hit.normal.y) > 0.6f) return;                       // это пол
        if (Vector3.Dot(hit.normal, knock.normalized) > -0.3f) return;    // скользнула вдоль
        if (hit.collider.GetComponentInParent<Enemy>() != null || hit.collider.GetComponentInParent<PlayerMovement>() != null ||
            hit.collider.GetComponentInParent<NetworkPlayer>() != null) return;
        WallSlam(hit.collider, hit.point, knock.magnitude);
    }

    void WallSlam(Collider c, Vector3 at, float speedHit)
    {
        lastImpact = Time.time;
        BuildPiece bp = c.GetComponentInParent<BuildPiece>();
        bool spikes = bp != null && bp.pieceId == "Barricade";
        if (spikes) { StickOnSpikes(at); EnemyManager.React(this, -1, Vector3.zero, 0f, lastAttacker, Callout.Spikes); return; }
        Health -= speedHit * 4f;
        if (bp != null) BuildingManager.Damage(bp, spikes ? 6f : speedHit * 2f);
        knock = Vector3.zero;
        Interrupt(State.Stagger, 1.0f);
        EnemyManager.Impact(at, 0.9f, id, 2);
        EnemyManager.React(this, -1, Vector3.zero, 0f, lastAttacker, spikes ? Callout.Spikes : Callout.Wall);
    }

    void Face(Vector3 dir)
    {
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.01f && knock.sqrMagnitude < 4f && Mathf.Abs(twist) < 60f)
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(dir), 8f * Time.deltaTime);
    }

    // =====================================================================
    // Удары по твари (на хосте)
    // =====================================================================
    static float PartMultiplier(BodyPartType p)
    {
        switch (p)
        {
            case BodyPartType.Head: return 1.8f;
            case BodyPartType.ArmL: case BodyPartType.ArmR: return 0.7f;
            case BodyPartType.LegL: case BodyPartType.LegR: return 0.75f;
            default: return 1f;
        }
    }

    public void ApplyHit(HitInfo h, ulong attacker)
    {
        if (Health <= 0f || dead) return;
        bool brute = kind == EnemyKind.Brute;
        lastAttacker = attacker;
        Vector3 dir = h.dir; dir.y = 0f;
        dir = dir.sqrMagnitude > 0.01f ? dir.normalized : -transform.forward;
        lastHitDir = h.dir.sqrMagnitude > 0.01f ? h.dir.normalized : dir;
        BodyPartType part = h.part;
        if (Lost(part)) part = BodyPartType.Torso;
        int callout = Callout.None;

        float dmg = h.damage * PartMultiplier(part);
        if (CurrentState == State.Pinned) dmg *= 1.5f;
        if (CurrentState == State.Stagger || CurrentState == State.Kneel || CurrentState == State.Down) dmg *= 1.25f; // лежачего бить больнее

        // КАЗНЬ: тварь на колене, удар сверху (или тяжёлый) по голове
        if (CurrentState == State.Kneel && part == BodyPartType.Head && (h.type == HitType.Chop || h.heavy))
        {
            dmg = Health + 1f;
            partHp[(int)BodyPartType.Head] = 0f;
            callout = Callout.Execution;
        }

        bool wasOpen = CurrentState == State.Stagger;
        Health -= dmg;
        flash = 1f;
        if (part != BodyPartType.Torso) partHp[(int)part] -= dmg;

        // Мешок с ядом — взрыв
        if (part == BodyPartType.Sack && !Lost(BodyPartType.Sack))
        {
            Sever(BodyPartType.Sack, lastHitDir);
            Explode();
            Health = 0f;
            callout = Callout.Sack;
        }
        // Отрубили часть тела
        else if (part != BodyPartType.Torso && partHp[(int)part] <= 0f && !Lost(part) && (h.type != HitType.Arrow || part == BodyPartType.Head)) // стрелой руку не отрубить
        {
            Sever(part, lastHitDir);
            if (part == BodyPartType.Head) { Health = 0f; if (callout == Callout.None) callout = Callout.Head; }
            else if (callout == Callout.None) callout = BodyPart.IsArm(part) ? Callout.Arm : Callout.Leg;
            if (BodyPart.IsLeg(part) && Health > 0f && !climbing && !OnBlade && CurrentState != State.Thrown) Interrupt(State.Kneel, 1.2f);
        }

        float strength = h.heavy ? 1.6f : h.type == HitType.Kick ? 1.3f : 1f;

        if (Health <= 0f || climbing || CurrentState == State.Grabbed)
        {
            if (Health <= 0f && OnBlade) { holder = null; HeldBy = -1; }
            Finish(part, strength, callout);
            return;
        }

        // Летящую — ещё дальше
        if (CurrentState == State.Thrown)
        {
            if ((h.type == HitType.Kick || h.heavy) && rb != null)
            {
                rb.linearVelocity = dir * 14f * Mathf.Lerp(1f, knockScale, 0.5f) + Vector3.up * 3f;
                thrownAt = Time.time;
                callout = Callout.Air;
            }
            Finish(part, strength, callout);
            return;
        }

        // Насажена на клинок / пригвождена
        if (OnBlade)
        {
            bool pinned = CurrentState == State.Pinned;
            if (h.type == HitType.RipOut)
            {
                ReleaseBlade(State.Flinch, 0.7f);
                Knock(dir * 2f);
                callout = Callout.RipOut;
            }
            else if (h.type == HitType.Kick)
            {
                ReleaseBlade(State.Recover, 0.3f);
                if (pinned) { Health -= 25f; squash = 1f; Interrupt(State.Stagger, 1.2f); EnemyManager.Impact(transform.position + Vector3.up, 0.8f, id, 2); callout = Callout.Crushed; }
                else BeginThrow(dir * 12f + Vector3.up * 3f, 3f);
            }
            Finish(part, strength, callout);
            return;
        }

        // Клинок вошёл в туловище и застрял — тварь насажена на клинок
        if (h.embed && !brute && CurrentState != State.Thrown)
        {
            Transform t = EnemyManager.Instance != null ? EnemyManager.Instance.PlayerTransform(attacker) : null;
            if (t != null)
            {
                Impale(t, attacker);
                callout = Callout.Impaled;
                if (WallBehind(dir, 0.45f * bodyScale + 0.6f, out RaycastHit wh)) { Pin(wh, dir); callout = Callout.Pinned; }
                Finish(part, strength, callout);
                return;
            }
        }

        // Тяжёлый удар по «открытой» (шатающейся) — добивание: подбросить и об землю
        if (wasOpen && h.heavy && h.type != HitType.Thrust)
        {
            Health -= 25f;
            BeginThrow(Vector3.up * 9f + dir * 3f, 9f);
            Finish(part, strength, Callout.Finisher);
            return;
        }

        switch (h.type)
        {
            case HitType.Slash:
            case HitType.Cross:
            case HitType.Tool:
            {
                // Боковой удар разворачивает тело в сторону клинка
                float side = Vector3.Dot(dir, transform.right);
                twist += -side * (brute ? 50f : 140f) * (h.heavy ? 1.4f : 1f);
                Knock(dir * (h.heavy ? 8f : 5f));
                if (h.type == HitType.Cross && !brute) { BeginThrow(dir * 9f + Vector3.up * 3f, 6f); callout = Callout.Cross; break; }
                if (h.heavy && !brute) { BeginThrow(dir * 8f + Vector3.up * 3f, 4f); break; }
                if (h.heavy && brute) { Interrupt(State.Stagger, 0.8f); break; }
                if (!brute) Interrupt(State.Flinch, 0.3f);       // больно: сбивает замах, на миг замирает
                LimbEffect(part, h, brute, ref callout);
                break;
            }
            case HitType.Chop:
                if (part == BodyPartType.Head) Interrupt(State.Stagger, brute ? (h.heavy ? 1.0f : 0.5f) : (h.heavy ? 1.4f : 0.8f)); // оглушил
                else if (h.heavy && !BodyPart.IsLeg(part)) Interrupt(State.Stagger, brute ? 0.7f : 1.2f);
                else LimbEffect(part, h, brute, ref callout);
                Knock(dir * 1.5f);
                break;
            case HitType.Uppercut:
                if (!brute && (part == BodyPartType.Torso || part == BodyPartType.Head))
                {
                    BeginThrow(Vector3.up * (h.heavy ? 10f : 7f) + dir * 1.5f, 5f);
                    callout = Callout.Launch;
                }
                else LimbEffect(part, h, brute, ref callout);
                break;
            case HitType.Thrust:
                if (part == BodyPartType.Head) Interrupt(State.Stagger, brute ? 0.5f : 1.0f);
                else { Knock(dir * 2f); LimbEffect(part, h, brute, ref callout); }
                break;
            case HitType.Kick:
                if (brute)
                {
                    Knock(dir * 4f);
                    Interrupt(State.Stagger, CurrentState == State.Kneel ? 1.0f : 0.5f);
                }
                else BeginThrow(dir * 9f + Vector3.up * 3.5f, 2f);
                break;
            case HitType.Fire:
                // огненный шар: взрывом отбрасывает, тело горит ещё пару секунд
                burnUntil = Time.time + (h.heavy ? 4f : 2.5f);
                if (!brute && h.heavy) { BeginThrow(dir * 7f + Vector3.up * 4f, 3f); break; }
                Knock(dir * (brute ? 2f : 6f));
                Interrupt(State.Stagger, brute ? 0.3f : 0.6f);
                break;
            case HitType.Lightning:
                // молния: трясёт и оглушает
                Interrupt(State.Stagger, brute ? 0.8f : 1.6f);
                Knock(dir * 1.5f);
                break;
            case HitType.Blaster:
                // луч света прожигает: больно, но почти не толкает
                if (!brute) Interrupt(State.Flinch, 0.15f);
                Knock(dir * 0.6f);
                break;
            case HitType.Trip:
                // подкат по ногам: обычная тварь падает, громила только шатается
                if (brute) { Knock(dir * 1.5f); Interrupt(State.Stagger, 0.5f); }
                else { Knock(dir * 2.5f); Interrupt(State.Down, 1.5f); callout = Callout.Trip; }
                break;
            case HitType.RipOut:
                Knock(dir * 2f);
                Interrupt(State.Flinch, 0.5f);
                break;
            case HitType.Arrow:
                // Стрела на полном натяжении пригвождает к стене за спиной
                if (h.heavy && !brute && part == BodyPartType.Torso && WallBehind(dir, 0.45f * bodyScale + 1.2f, out RaycastHit aw))
                {
                    Pin(aw, dir);
                    freePin = true;
                    callout = Callout.Pinned;
                    break;
                }
                if (part == BodyPartType.Head) Interrupt(State.Stagger, brute ? 0.3f : 0.7f);
                else { Knock(dir * Mathf.Clamp(h.damage / 15f, 0.5f, 3f)); LimbEffect(part, h, brute, ref callout); }
                break;
        }
        Finish(part, strength, callout);
    }

    void Finish(BodyPartType part, float strength, int callout)
    {
        EnemyManager.React(this, (int)part, lastHitDir, strength, lastAttacker, callout);
    }

    // Что делает удар по руке / ноге / телу
    void LimbEffect(BodyPartType part, HitInfo h, bool brute, ref int callout)
    {
        if (BodyPart.IsLeg(part))
        {
            limpUntil = Time.time + 4f;
            bool weak = partHp[(int)part] < partMax[(int)part] * 0.5f;
            if ((weak || h.heavy) && Time.time >= kneelCooldown && CurrentState != State.Kneel)
            {
                Interrupt(State.Kneel, brute ? 1.8f : 1.5f);
                kneelCooldown = Time.time + 4.5f;
                if (callout == Callout.None) callout = Callout.Kneel;
            }
            else if (!brute) Interrupt(State.Flinch, 0.35f);
        }
        else if (BodyPart.IsArm(part))
        {
            if (CurrentState == State.WindUp) // удар по руке во время замаха сбивает удар (даже у громилы)
            {
                Interrupt(State.Flinch, 0.6f);
                if (callout == Callout.None) callout = Callout.Disarm;
            }
            else if (!brute) Interrupt(State.Flinch, 0.3f);
        }
        else if (!brute) Interrupt(State.Flinch, part == BodyPartType.Head ? 0.5f : 0.3f);
    }

    void Interrupt(State s, float duration)
    {
        if (OnBlade || CurrentState == State.Thrown || CurrentState == State.Grabbed) return;
        // Удар сбивает замах
        if (CurrentState == State.WindUp) { strikePiece = null; nextAttack = Time.time + 0.5f; }
        if ((CurrentState == State.Stagger || CurrentState == State.Kneel || CurrentState == State.Down) && s == State.Flinch) return; // шатается — пусть шатается
        SetState(s, duration);
        windTint = 0f;
    }

    // Парировали её удар — «открыта»
    public void Parried()
    {
        if (CurrentState == State.Thrown || CurrentState == State.Grabbed || OnBlade) return;
        Knock(-transform.forward * 3f);
        SetState(State.Stagger, kind == EnemyKind.Brute ? 1.2f : 1.7f);
        flash = 1f;
    }

    // Отрубить часть тела (на хосте; у остальных — когда придёт LostMask)
    void Sever(BodyPartType p, Vector3 dir)
    {
        LostMask |= 1 << (int)p;
        DetachPart(p, dir);
    }

    void DetachPart(BodyPartType p, Vector3 dir)
    {
        BodyPart bp = parts != null ? parts[(int)p] : null;
        if (bp == null || bp.lost) return;
        bp.lost = true;
        bp.enabled = false;
        bp.owner = null;
        bp.transform.SetParent(null, true);
        Rigidbody body = MakePhysical(bp.gameObject, 6f * bodyScale);
        body.linearVelocity = dir.normalized * 4f + Vector3.up * 3.5f;
        body.angularVelocity = Random.insideUnitSphere * 10f;
        Destroy(bp.gameObject, 7f);
        CombatFx.Ichor(bp.transform.position, dir, p == BodyPartType.Sack ? 4 : 16);
    }

    static Rigidbody MakePhysical(GameObject go, float mass)
    {
        foreach (Transform t in go.GetComponentsInChildren<Transform>()) t.gameObject.layer = 2; // прицел сквозь куски
        foreach (Collider c in go.GetComponentsInChildren<Collider>()) c.isTrigger = false;
        Rigidbody r = go.AddComponent<Rigidbody>();
        r.mass = mass;
        r.interpolation = RigidbodyInterpolation.Interpolate;
        return r;
    }

    // Мешок плевальщика лопнул: яд обжигает всех вокруг (и тебя, если стоишь рядом!)
    void Explode()
    {
        Vector3 at = transform.position + Vector3.up * 1.2f * bodyScale;
        EnemyManager.Impact(at, 1f, id, 1);
        foreach (Enemy o in All.ToArray())
        {
            if (o == this || o.climbing || o.dead) continue;
            Vector3 p = o.transform.position - at; p.y = 0f;
            if (p.magnitude > 4.5f) continue;
            o.Health -= 50f;
            o.flash = 1f;
            o.lastAttacker = lastAttacker;
            if (o.kind != EnemyKind.Brute && o.CurrentState != State.Grabbed && !o.OnBlade) o.BeginThrow(p.normalized * 7f + Vector3.up * 4f, 3f);
            else o.Knock(p.normalized * 4f);
        }
        if (EnemyManager.Instance != null) EnemyManager.Instance.SplashAt(at, 2.2f, 12f);
    }

    // ---------- На клинке (на хосте) ----------
    void Impale(Transform by, ulong ownerId)
    {
        holder = by;
        HeldBy = (int)ownerId;
        if (cc != null) cc.enabled = false;
        impaledAt = Time.time;
        HasToken = false;
        windTint = 0f;
        strikePiece = null;
        SetState(State.Impaled, 6f); // держится, пока игрок не выдернет клинок (или 6 сек)
    }

    bool WallBehind(Vector3 dir, float dist, out RaycastHit hit)
    {
        Vector3 c = transform.position + Vector3.up * 0.9f * bodyScale;
        foreach (RaycastHit h in Physics.RaycastAll(c, dir, dist, ~(1 << 2), QueryTriggerInteraction.Ignore))
        {
            if (Mathf.Abs(h.normal.y) > 0.6f) continue;
            if (h.collider.GetComponentInParent<Enemy>() != null || h.collider.GetComponentInParent<PlayerMovement>() != null ||
                h.collider.GetComponentInParent<NetworkPlayer>() != null) continue;
            hit = h;
            return true;
        }
        hit = default;
        return false;
    }

    // Тварь на клинке: стоит на месте, дёргается, не может бить. Отошёл далеко — клинок вышел сам
    void HoldOnBlade()
    {
        if (holder == null || Vector3.Distance(holder.position, transform.position) > 4f) { ReleaseBlade(State.Recover, 0.4f); return; }
        Face(holder.position - transform.position);
        Health -= 3f * Time.deltaTime; // больно
        if (Time.time >= stateUntil) ReleaseBlade(State.Recover, 0.5f); // соскользнула
    }

    void Pin(RaycastHit wall, Vector3 dir)
    {
        Vector3 p = wall.point - dir * (0.45f * bodyScale + 0.05f);
        p.y = transform.position.y;
        transform.position = p;
        SetState(State.Pinned, 3f);
        Health -= 15f;
        squash = 1f;
        EnemyManager.Impact(wall.point, 0.9f, id, 2);
    }

    // Насадилась на колья баррикады: висит, дёргается, слабеет
    void StickOnSpikes(Vector3 at)
    {
        if (rb != null) { rb.linearVelocity = Vector3.zero; rb.isKinematic = true; } // убираем в StayPinned (сейчас идёт расчёт столкновения)
        if (cc != null) cc.enabled = false;
        spiked = true;
        holder = null;
        HeldBy = -1;
        knock = Vector3.zero;
        Health -= 35f;
        SetState(State.Pinned, 3.5f);
        squash = 0.6f;
        EnemyManager.Impact(at, 1f, id, 2);
    }

    void StayPinned()
    {
        if (spiked || freePin)
        {
            if (rb != null) { Destroy(rb); rb = null; }
            if (throwCol != null) { Destroy(throwCol); throwCol = null; }
            transform.rotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
            if (spiked) Health -= 22f * Time.deltaTime;
            if (Time.time >= stateUntil) { spiked = false; freePin = false; ReleaseBlade(State.Down, 1.2f); }
            return;
        }
        Health -= 3f * Time.deltaTime;
        if (holder == null || Vector3.Distance(holder.position, transform.position) > 4.5f || Time.time >= stateUntil)
            ReleaseBlade(State.Recover, 0.5f);
    }

    void ReleaseBlade(State next, float duration)
    {
        holder = null;
        HeldBy = -1;
        if (cc != null) cc.enabled = true;
        verticalVelocity = 0f;
        SetState(next, duration);
    }

    // ---------- Захват и бросок (на хосте) ----------
    public bool Grab(Transform by, ulong ownerId)
    {
        if (!CanBeGrabbed || climbing || CurrentState == State.Thrown || CurrentState == State.Grabbed || by == null) return false;
        holder = by;
        HeldBy = (int)ownerId;
        if (cc != null) cc.enabled = false;
        SetState(State.Grabbed, 3f); // через 3 сек вырвется
        return true;
    }

    // Посох поднимает тварь телекинезом. Громилу тоже можно, но он тяжёлый и вырывается быстро.
    public bool GrabTk(Transform by, ulong ownerId)
    {
        if (climbing || CurrentState == State.Thrown || CurrentState == State.Grabbed || OnBlade || by == null) return false;
        holder = by;
        HeldBy = (int)ownerId;
        telekinetic = true;
        tkPoint = transform.position + Vector3.up * 1.2f;
        if (cc != null) cc.enabled = false;
        SetState(State.Grabbed, kind == EnemyKind.Brute ? 1.3f : 2.8f); // столько сможет удержать, потом вырвется
        return true;
    }

    public void SetTkPoint(Vector3 p, ulong by) { if (telekinetic && HeldBy == (int)by) tkPoint = p; }

    void HoldInHands()
    {
        if (holder == null) { Release(); return; }
        if (telekinetic)
        {
            // тварь сопротивляется: дёргается, тянет вниз; громила почти не отрывается от земли
            bool brute = kind == EnemyKind.Brute;
            float t = Time.time;
            Vector3 tug = new Vector3(Mathf.PerlinNoise(t * 2.3f, id) - 0.5f, Mathf.PerlinNoise(id, t * 2.1f) - 0.6f, Mathf.PerlinNoise(t * 1.7f, id * 2f) - 0.5f) * (brute ? 2.2f : 1.2f);
            Vector3 want = tkPoint + tug;
            if (brute && Physics.Raycast(transform.position + Vector3.up, Vector3.down, out RaycastHit g, 6f, ~(1 << 2), QueryTriggerInteraction.Ignore))
                want.y = Mathf.Min(want.y, g.point.y + 0.7f);
            Vector3 next = Vector3.Lerp(transform.position, want, 1f - Mathf.Exp(-(brute ? 2f : 6f) * Time.deltaTime));
            // сквозь стены не тащим
            if (!Physics.Linecast(transform.position + Vector3.up, next + Vector3.up, out RaycastHit wall, ~(1 << 2), QueryTriggerInteraction.Ignore)
                || wall.collider.GetComponentInParent<Enemy>() == this)
                transform.position = next;
            Face(holder.position - transform.position);
            if (Time.time >= stateUntil)
            {
                // вырвалась — падает
                telekinetic = false;
                holder = null;
                HeldBy = -1;
                BeginThrow(Vector3.down * 2f, 1f);
            }
            return;
        }
        transform.position = holder.position + holder.forward * (1.0f + 0.4f * bodyScale) + Vector3.up * 0.3f;
        Face(holder.position - transform.position);
        if (Time.time >= stateUntil)
        {
            // вырвалась и укусила
            EnemyManager.HurtPlayer((ulong)HeldBy, playerDamage * 0.7f, id);
            Release();
        }
    }

    void Release()
    {
        telekinetic = false;
        holder = null;
        HeldBy = -1;
        if (cc != null) cc.enabled = true;
        SetState(State.Recover, 0.6f);
    }

    public void Throw(Vector3 velocity, float spin)
    {
        if (CurrentState != State.Grabbed) return;
        telekinetic = false;
        holder = null;
        HeldBy = -1;
        BeginThrow(velocity, spin);
    }

    // Тварь становится физическим телом: летит, кувыркается, бьётся о стены
    void BeginThrow(Vector3 velocity, float spin)
    {
        if (!Simulated) return;
        if (cc != null) cc.enabled = false;
        float h = BodyHeight * bodyScale;
        if (throwCol == null)
        {
            throwCol = gameObject.AddComponent<CapsuleCollider>();
            throwCol.radius = 0.45f * bodyScale; throwCol.height = h; throwCol.center = new Vector3(0f, h / 2f, 0f);
        }
        if (rb == null) rb = gameObject.AddComponent<Rigidbody>();
        rb.mass = 60f * bodyScale * bodyScale * bodyScale;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.isKinematic = false;
        rb.linearDamping = 0.15f;
        rb.angularDamping = 1.2f;
        rb.linearVelocity = velocity * Mathf.Lerp(1f, knockScale, 0.5f);
        rb.angularVelocity = transform.right * -spin * 0.6f + Random.insideUnitSphere * spin * 0.15f;
        knock = Vector3.zero;
        twist = 0f;
        thrownAt = Time.time;
        windTint = 0f;
        SetState(State.Thrown, 4f);
    }

    void FlyCheck()
    {
        bool settled = rb != null && Time.time - thrownAt > 0.5f && rb.linearVelocity.magnitude < 1f;
        if (settled || Time.time - thrownAt > 3.5f || rb == null) EndThrow();
    }

    void EndThrow()
    {
        if (rb != null) DestroyImmediate(rb);
        if (throwCol != null) DestroyImmediate(throwCol);
        rb = null; throwCol = null;
        transform.rotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
        if (cc != null) cc.enabled = true;
        verticalVelocity = 0f;
        SetState(State.Down, kind == EnemyKind.Brute ? 0.6f : 1.1f); // лежит, потом встаёт
    }

    // Летящая тварь во что-то врезалась
    void OnCollisionEnter(Collision c)
    {
        if (!Simulated || CurrentState != State.Thrown || Time.time - thrownAt < 0.05f || Time.time - lastImpact < 0.15f) return;
        float impact = c.relativeVelocity.magnitude;
        if (impact < 3f) return;
        lastImpact = Time.time;
        Vector3 at = c.contactCount > 0 ? c.GetContact(0).point : transform.position;
        Vector3 normal = c.contactCount > 0 ? c.GetContact(0).normal : Vector3.up;
        bool wall = Mathf.Abs(normal.y) < 0.5f;

        BuildPiece bp = c.collider.GetComponentInParent<BuildPiece>();
        Enemy other = c.collider.GetComponentInParent<Enemy>();
        if (bp != null)
        {
            bool spikes = bp.pieceId == "Barricade";
            if (spikes)
            {
                StickOnSpikes(at);                                    // насадилась на колья
                BuildingManager.Damage(bp, 6f);
                EnemyManager.React(this, -1, Vector3.zero, 0f, lastAttacker, Callout.Spikes);
                return;
            }
            Health -= impact * 6f;                                   // о стену — больно
            BuildingManager.Damage(bp, spikes ? 8f : impact * 3f);
            bool flat = spikes || (wall && impact > 8f);
            EnemyManager.Impact(at, spikes ? 1f : Mathf.Clamp01(impact / 12f), flat ? id : -1, flat ? 2 : 0);
            if (flat) EnemyManager.React(this, -1, Vector3.zero, 0f, lastAttacker, spikes ? Callout.Spikes : Callout.Wall);
        }
        else if (other != null && other != this)
        {
            Health -= impact * 3f;
            other.Health -= impact * 3f;
            other.lastAttacker = lastAttacker;
            Vector3 push = other.transform.position - transform.position; push.y = 0f;
            if (other.CurrentState != State.Thrown && other.CurrentState != State.Grabbed && !other.climbing && !other.OnBlade)
            {
                if (other.kind == EnemyKind.Brute) other.Interrupt(State.Flinch, 0.3f);
                else other.BeginThrow(push.normalized * impact * 0.5f + Vector3.up * 2f, 2f); // сбили соседа как кеглю
            }
            EnemyManager.Impact(at, 0.6f);
        }
        else if (impact > 6f)
        {
            Health -= impact * 2.5f;                                 // шмякнулась о землю или скалу
            bool flat = wall && impact > 8f;
            EnemyManager.Impact(at, Mathf.Clamp01(impact / 15f), flat ? id : -1, flat ? 2 : 0);
            if (flat) EnemyManager.React(this, -1, Vector3.zero, 0f, lastAttacker, Callout.Wall);
        }
        flash = 1f;
    }

    BuildPiece NearestPiece(float radius)
    {
        BuildPiece best = null;
        float bestD = radius;
        foreach (BuildPiece p in BuildPiece.All)
        {
            float d = Vector3.Distance(p.transform.position, transform.position);
            if (!p.village) d += 8f; // больше любят ломать дома деревни
            if (d < bestD) { bestD = d; best = p; }
        }
        return best;
    }

    // =====================================================================
    // Копия у остальных игроков
    // =====================================================================
    public void SetNetState(Vector3 pos, float yaw, float hpFrac, bool climb, int state, int heldBy, int lostMask, int side)
    {
        if (Simulated) return;
        if (state == (int)State.WindUp && CurrentState != State.WindUp) windStart = Time.time;
        AttackSide = side;
        netPos = pos;
        netYaw = yaw;
        climbing = climb;
        State s = (State)state;
        if (s == State.Recover && CurrentState == State.WindUp) lunge = 1f;
        if (s == State.Pinned && CurrentState != State.Pinned) squash = 1f;
        CurrentState = s;
        HeldBy = heldBy;
        if (hpFrac < HealthFraction) flash = 1f;
        HealthFraction = hpFrac;
        int added = lostMask & ~LostMask;
        LostMask = lostMask;
        for (int i = 0; i < 7; i++) if ((added & (1 << i)) != 0) DetachPart((BodyPartType)i, lastHitDir);
    }

    // Тело дёрнулось от удара — у всех игроков одинаково (вызывает EnemyManager или сам ударивший сразу)
    public void LocalReact(int part, Vector3 dir, float strength)
    {
        if (dir.sqrMagnitude > 0.01f) lastHitDir = dir.normalized;
        flash = 1f;
        if (parts == null || strength <= 0f || dir.sqrMagnitude < 0.01f) return;
        Vector3 hdir = new Vector3(dir.x, 0f, dir.z);
        Vector3 axis = hdir.sqrMagnitude > 0.09f * dir.sqrMagnitude
            ? Vector3.Cross(Vector3.up, hdir.normalized)
            : transform.right * (dir.y < 0f ? 1f : -1f);              // сверху — кивок вперёд, снизу — запрокидывает

        // Удар бьёт всё тело разом, как твёрдое: его сразу отбрасывает и откидывает, потом оно выпрямляется
        float heavyK = kind == EnemyKind.Brute ? 0.4f : 1f;
        Vector3 la = transform.InverseTransformDirection(axis);
        recoilRot += la * 16f * strength * heavyK;                   // градусы — сразу, без разгона: так читается удар
        Vector3 lp = transform.InverseTransformDirection(hdir.sqrMagnitude > 0.0001f ? hdir.normalized : Vector3.zero);
        recoilPos += lp * 0.22f * strength * heavyK;
        hitShake = Mathf.Max(hitShake, 0.12f * strength);           // тело дрожит от удара

        Vector3 local = la * 110f * strength; // суставы чуть мотаются, но не гнутся
        BodyPart torso = parts[(int)BodyPartType.Torso];
        BodyPart p = part >= 0 && part < parts.Length ? parts[part] : null;
        if (p == null || p.lost) p = torso;
        if (p != null) p.reactVel += local * (p == torso ? 1f : 0.5f);
        if (torso != null && p != torso) torso.reactVel += local * 0.7f;
        BodyPart head = parts[(int)BodyPartType.Head];
        if (p == torso && head != null && !head.lost) head.reactVel += local * 0.3f; // голова чуть мотается
    }

    // Расплющило (у копий — через EnemyManager.Impact)
    public void Squash() { squash = 1f; }

    // Замедление только этой твари (клинок режет её — она «вязнет» во времени, остальные нет)
    float slowUntil, slowScale = 1f;
    float TS => Time.time < slowUntil ? slowScale : 1f;
    public void SlowMo(float duration, float scale)
    {
        slowUntil = Time.time + duration;
        slowScale = scale;
        float extra = duration * (1f - scale);   // её таймеры тоже идут медленнее
        stateUntil += extra;
        nextAttack += extra;
    }
    Vector3 moveVel;

    Vector3 recoilRot, recoilRotVel, recoilPos, recoilPosVel; // отдача всего тела от удара
    float hitShake;
    Quaternion visRot = Quaternion.identity;
    Vector3 visPos;

    public void Lunge() { lunge = 1f; }
    public void Flash() { flash = 1f; }

    void Update()
    {
        if (!Simulated)
        {
            PlayerHealth me = PlayerHealth.Local;
            bool mine = HeldBy >= 0 && (ulong)HeldBy == Net.LocalId && me != null;
            if (mine && CurrentState == State.Grabbed && PlayerStaff.LocalTkId == id)
            {
                // держу посохом — показываю там, куда тяну, сразу
                transform.position = Vector3.Lerp(transform.position, PlayerStaff.LocalTkPoint, 8f * Time.deltaTime);
            }
            else if (mine && CurrentState == State.Grabbed)
            {
                // Я держу эту тварь — показываю её у себя в руках сразу, без задержки сети
                Transform h = me.transform;
                transform.position = h.position + h.forward * (1.0f + 0.4f * bodyScale) + Vector3.up * 0.3f;
                transform.rotation = Quaternion.LookRotation(-h.forward);
            }
            else
            {
                transform.position = Vector3.Lerp(transform.position, netPos, 12f * Time.deltaTime);
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.Euler(0f, netYaw, 0f), 10f * Time.deltaTime);
            }
        }
        UpdateVisual();
    }

    // Поза тела — по состоянию (одинаково у хоста и у остальных)
    void UpdateVisual()
    {
        if (visual == null) return;
        float dt = Time.deltaTime * TS;
        lunge = Mathf.MoveTowards(lunge, 0f, dt * 3f);
        windTint = CurrentState == State.WindUp ? Mathf.Min(1f, windTint + dt * 3f) : Mathf.MoveTowards(windTint, 0f, dt * 4f);
        squash = Mathf.MoveTowards(squash, 0f, dt * 1.1f);

        // Скорость ходьбы (по тому, как сдвинулись) — для шагов
        Vector3 moved = transform.position - lastVisPos; moved.y = 0f;
        lastVisPos = transform.position;
        if (dt > 0.0001f) visSpeed = Mathf.Lerp(visSpeed, moved.magnitude / dt, 10f * dt);
        walkPhase += Mathf.Min(visSpeed, 8f) * dt * 3.5f / bodyScale;

        if (dt > 0.0001f) visVel = Vector3.Lerp(visVel, transform.InverseTransformDirection(moved / dt), 8f * dt);
        int legsLost = LegsLost;
        Quaternion rot = Quaternion.identity;
        Vector3 pos = new Vector3(0f, 0f, Mathf.Sin(lunge * Mathf.PI) * 0.6f);
        float t = Time.time;
        if (climbing) rot = Quaternion.Euler(-70f, 0f, 0f);
        else switch (CurrentState)
        {
            case State.WindUp: rot = Quaternion.Euler(-12f, 0f, 0f); pos.z -= 0.25f; break;
            case State.Recover: rot = Quaternion.Euler(18f * Mathf.Sin(lunge * Mathf.PI), 0f, 0f); break;
            case State.Flinch: rot = Quaternion.Euler(-10f, Mathf.Sin(t * 40f) * 6f, 0f); break;
            case State.Stagger: rot = Quaternion.Euler(-10f, 0f, Mathf.Sin(t * 9f) * 18f); break;
            case State.Grabbed: rot = Quaternion.Euler(-35f, 0f, Mathf.Sin(t * 25f) * 12f); break;
            case State.Impaled: rot = Quaternion.Euler(-22f, 0f, Mathf.Sin(t * 22f) * 8f); break;
            case State.Pinned: rot = Quaternion.Euler(-8f, 0f, Mathf.Sin(t * 30f) * 4f); break;
            case State.Kneel: rot = Quaternion.Euler(12f, 0f, 0f); pos.y = -0.38f * bodyScale; break;
            case State.Down: rot = Quaternion.Euler(-88f, 0f, 0f); pos.y = 0.25f * bodyScale; break;
            default:
                // наклон по движению: отбросило назад — откидывается, вбок — заваливается
                rot = Quaternion.Euler(Mathf.Clamp(visVel.z * 2f, -15f, 6f), 0f, Mathf.Clamp(-visVel.x * 2.5f, -12f, 12f));
                // без ноги не устоять — ползёт на руках (с одной ногой — ещё и отталкивается ею, кренясь)
                if (legsLost >= 1) { rot = Quaternion.Euler(68f, 0f, legsLost == 1 ? Mathf.Sin(walkPhase) * 8f : 0f); pos.y = -0.25f * bodyScale; }
                break;
        }
        if (CurrentState == State.Thrown && !Simulated)
        {
            visualSpin += dt * 720f; // у копии просто кувыркается
            visual.localRotation = Quaternion.Euler(-visualSpin, 0f, 0f);
        }
        else if (CurrentState == State.Thrown) visual.localRotation = Quaternion.identity; // у хоста вертит физика
        else
        {
            visualSpin = 0f;
            // пружина отдачи: резкий отброс, быстрый возврат без «желе»
            recoilRotVel += (-recoilRot * 170f - recoilRotVel * 22f) * dt;
            recoilRot += recoilRotVel * dt;
            recoilPosVel += (-recoilPos * 140f - recoilPosVel * 20f) * dt;
            recoilPos += recoilPosVel * dt;
            hitShake = Mathf.MoveTowards(hitShake, 0f, dt * 0.8f);
            Vector3 jitter = hitShake > 0f ? new Vector3(Mathf.Sin(t * 90f), 0f, Mathf.Cos(t * 77f)) * hitShake * 0.15f : Vector3.zero;
            visRot = Quaternion.Slerp(visRot, rot, 14f * dt);
            float ra = recoilRot.magnitude;
            Quaternion rr = ra > 0.01f ? Quaternion.AngleAxis(ra, recoilRot / ra) : Quaternion.identity;
            visual.localRotation = rr * visRot;
            visPos = Vector3.Lerp(visPos, pos, 20f * dt);
            pos = visPos + recoilPos + jitter;
        }
        if (CurrentState == State.Thrown) visPos = Vector3.Lerp(visual.localPosition, pos, 20f * dt);
        visual.localPosition = CurrentState == State.Thrown ? visPos : pos;
        visual.localScale = Vector3.one * bodyScale * 1f + Vector3.Scale(new Vector3(0.3f, 0.1f, -0.5f), Vector3.one * bodyScale * squash);

        AnimateParts(t, dt, legsLost);

        // горит — языки пламени
        if (Burning && Time.time >= nextFlame)
        {
            nextFlame = Time.time + 0.05f;
            CombatFx.Flame(transform.position + Vector3.up * Random.Range(0.3f, 1.6f) * bodyScale + Random.insideUnitSphere * 0.3f * bodyScale);
        }

        // Цвет: белая вспышка при ударе, красное «наливание» во время замаха
        if (renderers == null || (flash <= 0f && windTint <= 0f)) return;
        flash = Mathf.Max(0f, flash - dt * 4f);
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] == null) continue;
            Color c = Color.Lerp(baseColors[i], new Color(0.9f, 0.1f, 0.05f), windTint * 0.6f);
            c = Color.Lerp(c, Color.white, flash);
            renderers[i].GetPropertyBlock(block);
            block.SetColor("_BaseColor", c);
            renderers[i].SetPropertyBlock(block);
        }
    }

    // Шаги, замах когтями, судороги — и «пружина» отдачи от ударов на каждом суставе
    void AnimateParts(float t, float dt, int legsLost)
    {
        if (parts == null) return;
        float amp = Mathf.Clamp01(visSpeed / 3f);
        float s = Mathf.Sin(walkPhase);
        for (int i = 0; i < parts.Length; i++)
        {
            BodyPart bp = parts[i];
            if (bp == null || bp.lost || bp.transform.parent == null) continue;
            bool arm = BodyPart.IsArm(bp.type), leg = BodyPart.IsLeg(bp.type);
            float side = bp.type == BodyPartType.ArmL || bp.type == BodyPartType.LegL ? 1f : -1f;
            Vector3 e = Vector3.zero;

            if (leg) e.x = s * 35f * amp * side;
            if (arm) e.x = -s * 20f * amp * side;

            switch (CurrentState)
            {
                case State.WindUp:
                {
                    // заносит лапу: сбоку — отводит руку в сторону и назад (ударит наотмашь), сверху — обе над головой
                    bool raise = AttackSide == 0 || (AttackSide < 0) == (bp.type == BodyPartType.ArmL);
                    float wp = WindProgress;
                    if (arm)
                    {
                        if (!raise) e = new Vector3(-30f, 0f, 0f);
                        else if (AttackSide == 0) e = new Vector3(Mathf.Lerp(-90f, -165f, wp), 0f, 0f);
                        else e = new Vector3(0f, Mathf.Lerp(-10f, -65f, wp) * side, -85f * side);
                    }
                    if (bp.type == BodyPartType.Torso)
                        e = AttackSide == 0 ? new Vector3(Mathf.Lerp(-10f, -28f, wp), 0f, 0f)            // выгибается назад
                                            : new Vector3(-10f, AttackSide * Mathf.Lerp(15f, 40f, wp), 0f); // плечо уходит назад
                    break;
                }
                case State.Recover:
                {
                    // удар: сбоку — лапа проносится вбок поперёк (наотмашь), сверху — обрушивается вниз
                    bool hit = AttackSide == 0 || (AttackSide < 0) == (bp.type == BodyPartType.ArmL);
                    float u = 1f - lunge;
                    float su = 1f - (1f - u) * (1f - u) * (1f - u);  // резко, с хлёстом в начале
                    if (arm && hit)
                        e = AttackSide == 0 ? new Vector3(Mathf.Lerp(-165f, 35f, su), 0f, 0f)
                                            : new Vector3(0f, Mathf.Lerp(-65f, 115f, su) * side, -85f * side);
                    if (bp.type == BodyPartType.Torso)
                        e = AttackSide == 0 ? new Vector3(Mathf.Lerp(-28f, 24f, su), 0f, 0f)
                                            : new Vector3(-6f, AttackSide * Mathf.Lerp(40f, -45f, su), 0f);
                    break;
                }
                case State.Grabbed:
                case State.Impaled:
                case State.Pinned:
                    if (arm) e.x = Mathf.Sin(t * 18f + i) * 45f - 50f;
                    if (leg) e.x = Mathf.Sin(t * 15f + i * 2f) * 30f;
                    break;
                case State.Kneel:
                    if (bp.type == BodyPartType.LegL) e.x = -85f;
                    if (bp.type == BodyPartType.LegR) e.x = 75f;
                    if (arm) e.x = 25f;
                    break;
                case State.Stagger:
                    if (arm) e = new Vector3(20f, 0f, Mathf.Sin(t * 9f) * 25f * side);
                    if (bp.type == BodyPartType.Head) e.x = 25f;
                    break;
                case State.Down:
                    if (arm) e.x = -60f;
                    break;
                default:
                    if (legsLost >= 1 && arm) e.x = -120f + Mathf.Sin(walkPhase * 1.5f + side) * 40f; // подтягивается на руках
                    if (legsLost == 1 && leg) e.x = 40f + Mathf.Sin(walkPhase * 1.5f) * 25f;           // уцелевшая нога волочится и толкает
                    break;
            }

            // Пружина отдачи: удар отклоняет сустав, потом он возвращается с покачиванием
            bp.reactVel += (-260f * bp.react - 28f * bp.reactVel) * dt; // жёсткая пружина без раскачки
            bp.react += bp.reactVel * dt;
            if (bp.react.sqrMagnitude > 25f * 25f) bp.react = bp.react.normalized * 25f;
            Quaternion r = Quaternion.identity;
            float ang = bp.react.magnitude;
            if (ang > 0.05f)
            {
                Vector3 ax = bp.transform.parent.InverseTransformDirection(transform.TransformDirection(bp.react / ang));
                r = Quaternion.AngleAxis(ang, ax);
            }
            Quaternion target = r * bp.baseRotation * Quaternion.Euler(e);
            bp.transform.localRotation = Quaternion.Slerp(bp.transform.localRotation, target, Mathf.Min(1f, 25f * dt));
        }
    }

    // Смерть: тело падает тряпичной куклой (суставы на шарнирах), через несколько секунд исчезает
    public void Die()
    {
        if (dead) return;
        dead = true;
        // смертельный удар отбрасывает тело — эффектно, по направлению удара
        Vector3 hd = lastHitDir; hd.y = Mathf.Max(hd.y, 0f);
        Vector3 v = (rb != null ? rb.linearVelocity : Vector3.zero) + hd * 4.5f + Vector3.up * 2.2f;
        CombatFx.Play(CombatFx.Snd.Fall, transform.position, 0.9f, 0.8f);
        GameObject corpse = new GameObject("Corpse");
        Rigidbody torsoRb = null;
        var limbs = new List<BodyPart>();
        if (parts != null)
        {
            foreach (BodyPart bp in parts)
            {
                if (bp == null || bp.lost) continue;
                bp.lost = true;
                bp.enabled = false;
                bp.owner = null;
                bp.transform.SetParent(corpse.transform, true);
                Rigidbody r = MakePhysical(bp.gameObject, (bp.type == BodyPartType.Torso ? 30f : 6f) * bodyScale);
                r.linearVelocity = v;
                r.linearDamping = 0.2f;
                r.angularDamping = 0.8f;
                if (bp.type == BodyPartType.Torso) { torsoRb = r; r.angularVelocity = Random.insideUnitSphere * 3f; }
                else limbs.Add(bp);
            }
            foreach (BodyPart bp in limbs)
            {
                if (torsoRb == null) break;
                CharacterJoint j = bp.gameObject.AddComponent<CharacterJoint>();
                j.connectedBody = torsoRb;
                j.anchor = Vector3.zero;
                j.axis = Vector3.right;
                j.swingAxis = Vector3.forward;
                j.lowTwistLimit = new SoftJointLimit { limit = -35f };
                j.highTwistLimit = new SoftJointLimit { limit = 35f };
                j.swing1Limit = new SoftJointLimit { limit = 55f };
                j.swing2Limit = new SoftJointLimit { limit = 40f };
            }
        }
        if (torsoRb == null)
        {
            // на всякий случай — старые кусочки
            for (int i = 0; i < 6; i++)
            {
                GameObject d = GameObject.CreatePrimitive(PrimitiveType.Cube);
                d.layer = 2;
                d.transform.SetParent(corpse.transform, false);
                d.transform.position = transform.position + Vector3.up * 0.9f * bodyScale + Random.insideUnitSphere * 0.5f * bodyScale;
                d.transform.localScale = Vector3.one * Random.Range(0.15f, 0.35f) * bodyScale;
                d.AddComponent<Rigidbody>().AddForce(Random.insideUnitSphere * 4f + Vector3.up * 3f, ForceMode.VelocityChange);
            }
        }
        CombatFx.Ichor(transform.position + Vector3.up * bodyScale, lastHitDir, 10);
        Destroy(corpse, 8f);
        Destroy(gameObject);
    }
}
