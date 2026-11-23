using System.Collections.Generic;
using UnityEngine;

public enum EnemyKind { Crawler, Runner, Brute, Spitter }

// Чем ударили тварь — от этого зависит, как она отреагирует
public enum HitType { Light, ComboEnd, Heavy, Kick, Finisher, Tool }

// Тварь с Тёмной половины. Виды:
//   Ползун — обычная. Бегун — быстрый и слабый. Громила — огромный, медленный, ломает стены, его почти не отбросить.
//   Плевальщик — держится на расстоянии и плюётся ядом (плевок летит дугой — можно отбежать).
//
// Как она дерётся (думает только хост):
//   - Нападают на одного игрока максимум двое («очередь» раздаёт EnemyManager). Остальные кружат вокруг и ждут.
//   - Перед ударом тварь ЗАМАХИВАЕТСЯ (отклоняется назад и краснеет) — в этот момент можно
//     сбить её ударом, отпрыгнуть перекатом или поставить блок/парировать.
//   - От ударов вздрагивает, от тяжёлого удара и парирования — «открыта» (шатается) — можно добить.
//   - Пинок и бросок превращают её в физическое тело: летит, бьётся о стены, насаживается на колья.
// У остальных игроков это «копия», которая повторяет то, что присылает хост.
public class Enemy : MonoBehaviour
{
    public enum State { Move, WindUp, Recover, Flinch, Stagger, Grabbed, Thrown, Down }

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
    public bool IsOpen => CurrentState == State.Stagger; // можно добить
    public bool CanBeGrabbed => kind != EnemyKind.Brute || CurrentState == State.Stagger;
    public int HeldBy { get; private set; } = -1;           // кто держит (номер игрока), -1 — никто
    public ulong TargetOwner { get; private set; } = Net.NoOne;
    public bool HasToken;          // можно нападать (раздаёт EnemyManager)
    public float TokenCooldownUntil;

    CharacterController cc;
    Transform visual;
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
    Rigidbody rb;
    CapsuleCollider throwCol;
    float thrownAt, lastImpact;
    float visualSpin;

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

    public void Setup(int enemyId, bool simulated, Transform visualRoot)
    {
        id = enemyId;
        Simulated = simulated;
        visual = visualRoot;
        Health = maxHealth;
        netPos = transform.position;
        netYaw = transform.eulerAngles.y;
        circleSign = (enemyId % 2 == 0) ? 1f : -1f;

        if (simulated)
        {
            cc = gameObject.AddComponent<CharacterController>();
            cc.radius = 0.45f * bodyScale; cc.height = 1.7f * bodyScale; cc.center = new Vector3(0f, 0.85f * bodyScale, 0f);
            cc.stepOffset = 0.4f;
        }
        else
        {
            CapsuleCollider col = gameObject.AddComponent<CapsuleCollider>();
            col.radius = 0.45f * bodyScale; col.height = 1.7f * bodyScale; col.center = new Vector3(0f, 0.85f * bodyScale, 0f);
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
        CurrentState = s;
        stateUntil = Time.time + duration;
    }

    // =====================================================================
    // Мозги (только у хоста)
    // =====================================================================
    public void Simulate(List<EnemyManager.PlayerInfo> players, Vector3 villageCenter)
    {
        HealthFraction = Health / maxHealth;

        // ---- Лезет из пропасти ----
        if (climbing) { Climb(); return; }

        // ---- Состояния, когда тварь не управляет собой ----
        switch (CurrentState)
        {
            case State.Grabbed: HoldInHands(); return;
            case State.Thrown: FlyCheck(); return;
            case State.Down:
            case State.Flinch:
            case State.Stagger:
            case State.Recover:
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
            if (strikePiece.isActiveAndEnabled) BuildingManager.Damage(strikePiece, buildingDamage);
            strikePiece = null;
        }
        else if (target != null && target.transform != null)
        {
            Vector3 to = target.transform.position - transform.position; to.y = 0f;
            if (to.magnitude < attackRange + 0.8f && Vector3.Angle(transform.forward, to) < 70f)
                EnemyManager.HurtPlayer(target.ownerId, playerDamage, id);
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
        Vector3 v = dir * moveSpeed + sep * 3f + Vector3.up * verticalVelocity + knock;
        knock = Vector3.MoveTowards(knock, Vector3.zero, 22f * Time.deltaTime);
        cc.Move(v * Time.deltaTime);
    }

    void Face(Vector3 dir)
    {
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.01f && knock.sqrMagnitude < 4f)
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(dir), 8f * Time.deltaTime);
    }

    // ---------- Удары по твари (на хосте) ----------
    public void ApplyHit(float damage, Vector3 from, HitType type)
    {
        Health -= damage;
        flash = 1f;
        Vector3 away = transform.position - from; away.y = 0f;
        away = away.sqrMagnitude > 0.01f ? away.normalized : -transform.forward;
        bool brute = kind == EnemyKind.Brute;
        if (CurrentState == State.Thrown || CurrentState == State.Grabbed || climbing) return;

        switch (type)
        {
            case HitType.Light:
            case HitType.Tool:
                Knock(away * 3f);
                if (!brute) Interrupt(State.Flinch, 0.3f);
                break;
            case HitType.ComboEnd:
                Knock(away * 7f + Vector3.up);
                Interrupt(State.Flinch, brute ? 0.2f : 0.6f);
                break;
            case HitType.Heavy:
                Knock(away * 6f);
                Interrupt(State.Stagger, brute ? 0.7f : 1.2f);
                break;
            case HitType.Kick:
                if (brute) { Knock(away * 4f); Interrupt(State.Stagger, 0.5f); }
                else BeginThrow(away * 9f + Vector3.up * 3.5f, 2f);
                break;
            case HitType.Finisher:
                BeginThrow(Vector3.up * 9f + away * 3f, 9f); // переворот в воздухе и об землю
                break;
        }
    }

    void Interrupt(State s, float duration)
    {
        // Удар сбивает замах
        if (CurrentState == State.WindUp) { strikePiece = null; nextAttack = Time.time + 0.5f; }
        if (CurrentState == State.Stagger && s == State.Flinch) return; // шатается — пусть шатается
        SetState(s, duration);
        windTint = 0f;
    }

    // Парировали её удар — «открыта»
    public void Parried()
    {
        if (CurrentState == State.Thrown || CurrentState == State.Grabbed) return;
        Knock(-transform.forward * 3f);
        SetState(State.Stagger, kind == EnemyKind.Brute ? 1.2f : 1.7f);
        flash = 1f;
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

    void HoldInHands()
    {
        if (holder == null) { Release(); return; }
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
        holder = null;
        HeldBy = -1;
        if (cc != null) cc.enabled = true;
        SetState(State.Recover, 0.6f);
    }

    public void Throw(Vector3 velocity, float spin)
    {
        if (CurrentState != State.Grabbed) return;
        holder = null;
        HeldBy = -1;
        BeginThrow(velocity, spin);
    }

    // Тварь становится физическим телом: летит, кувыркается, бьётся о стены
    void BeginThrow(Vector3 velocity, float spin)
    {
        if (!Simulated) return;
        if (cc != null) cc.enabled = false;
        if (throwCol == null)
        {
            throwCol = gameObject.AddComponent<CapsuleCollider>();
            throwCol.radius = 0.45f * bodyScale; throwCol.height = 1.7f * bodyScale; throwCol.center = new Vector3(0f, 0.85f * bodyScale, 0f);
        }
        if (rb == null) rb = gameObject.AddComponent<Rigidbody>();
        rb.mass = 60f * bodyScale * bodyScale * bodyScale;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.linearVelocity = velocity * Mathf.Lerp(1f, knockScale, 0.5f);
        rb.angularVelocity = transform.right * -spin + Random.insideUnitSphere * spin * 0.3f;
        knock = Vector3.zero;
        thrownAt = Time.time;
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

        BuildPiece bp = c.collider.GetComponentInParent<BuildPiece>();
        Enemy other = c.collider.GetComponentInParent<Enemy>();
        if (bp != null)
        {
            bool spikes = bp.pieceId == "Barricade";
            Health -= spikes ? 90f : impact * 6f;                    // о колья — почти насмерть, о стену — больно
            BuildingManager.Damage(bp, spikes ? 8f : impact * 3f);
            EnemyManager.Impact(at, spikes ? 1f : Mathf.Clamp01(impact / 12f));
        }
        else if (other != null && other != this)
        {
            Health -= impact * 3f;
            other.Health -= impact * 3f;
            Vector3 push = other.transform.position - transform.position; push.y = 0f;
            if (other.CurrentState != State.Thrown && other.CurrentState != State.Grabbed && !other.climbing)
            {
                if (other.kind == EnemyKind.Brute) other.Interrupt(State.Flinch, 0.3f);
                else other.BeginThrow(push.normalized * impact * 0.5f + Vector3.up * 2f, 2f); // сбили соседа как кеглю
            }
            EnemyManager.Impact(at, 0.6f);
        }
        else if (impact > 6f)
        {
            Health -= impact * 2.5f;                                 // шмякнулась о землю
            EnemyManager.Impact(at, Mathf.Clamp01(impact / 15f));
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
    public void SetNetState(Vector3 pos, float yaw, float hpFrac, bool climb, int state, int heldBy)
    {
        if (Simulated) return;
        netPos = pos;
        netYaw = yaw;
        climbing = climb;
        State s = (State)state;
        if (s == State.Recover && CurrentState == State.WindUp) lunge = 1f;
        CurrentState = s;
        HeldBy = heldBy;
        if (hpFrac < HealthFraction) flash = 1f;
        HealthFraction = hpFrac;
    }

    public void Lunge() { lunge = 1f; }
    public void Flash() { flash = 1f; }

    void Update()
    {
        if (!Simulated)
        {
            PlayerHealth me = PlayerHealth.Local;
            if (HeldBy >= 0 && (ulong)HeldBy == Net.LocalId && me != null)
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
        lunge = Mathf.MoveTowards(lunge, 0f, Time.deltaTime * 3f);
        windTint = CurrentState == State.WindUp ? Mathf.Min(1f, windTint + Time.deltaTime * 3f) : Mathf.MoveTowards(windTint, 0f, Time.deltaTime * 4f);

        Quaternion rot = Quaternion.identity;
        Vector3 pos = new Vector3(0f, 0f, Mathf.Sin(lunge * Mathf.PI) * 0.6f);
        float t = Time.time;
        if (climbing) rot = Quaternion.Euler(-70f, 0f, 0f);
        else switch (CurrentState)
        {
            case State.WindUp: rot = Quaternion.Euler(-28f, 0f, 0f); pos.z -= 0.25f; break;
            case State.Recover: rot = Quaternion.Euler(18f * Mathf.Sin(lunge * Mathf.PI), 0f, 0f); break;
            case State.Flinch: rot = Quaternion.Euler(-14f, Mathf.Sin(t * 40f) * 6f, 0f); break;
            case State.Stagger: rot = Quaternion.Euler(-10f, 0f, Mathf.Sin(t * 9f) * 18f); break;
            case State.Grabbed: rot = Quaternion.Euler(-35f, 0f, Mathf.Sin(t * 25f) * 12f); break;
            case State.Down: rot = Quaternion.Euler(-88f, 0f, 0f); pos.y = 0.25f * bodyScale; break;
        }
        if (CurrentState == State.Thrown && !Simulated)
        {
            visualSpin += Time.deltaTime * 720f; // у копии просто кувыркается
            visual.localRotation = Quaternion.Euler(-visualSpin, 0f, 0f);
        }
        else if (CurrentState == State.Thrown) visual.localRotation = Quaternion.identity; // у хоста вертит физика
        else
        {
            visualSpin = 0f;
            visual.localRotation = Quaternion.Slerp(visual.localRotation, rot, 14f * Time.deltaTime);
        }
        visual.localPosition = Vector3.Lerp(visual.localPosition, pos, 20f * Time.deltaTime);

        // Цвет: белая вспышка при ударе, красное «наливание» во время замаха
        if (renderers == null || (flash <= 0f && windTint <= 0f)) return;
        flash = Mathf.Max(0f, flash - Time.deltaTime * 4f);
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

    public void Die()
    {
        Renderer r = renderers != null && renderers.Length > 0 ? renderers[0] : null;
        Material mat = r != null ? r.sharedMaterial : null;
        for (int i = 0; i < 9; i++)
        {
            GameObject d = GameObject.CreatePrimitive(PrimitiveType.Cube);
            d.layer = 2;
            d.transform.position = transform.position + Vector3.up * 0.9f * bodyScale + Random.insideUnitSphere * 0.5f * bodyScale;
            d.transform.localScale = Vector3.one * Random.Range(0.15f, 0.35f) * bodyScale;
            d.transform.rotation = Random.rotation;
            if (mat != null) d.GetComponent<Renderer>().sharedMaterial = mat;
            Rigidbody body = d.AddComponent<Rigidbody>();
            body.mass = 0.5f;
            body.AddForce(Random.insideUnitSphere * 4f + Vector3.up * 3f, ForceMode.VelocityChange);
            Destroy(d, Random.Range(4f, 7f));
        }
        Destroy(gameObject);
    }
}
