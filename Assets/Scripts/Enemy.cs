using System.Collections.Generic;
using UnityEngine;

public enum EnemyKind { Crawler, Runner, Brute, Spitter }

// Тварь с Тёмной половины. Виды:
//   Ползун — обычная. Бегун — быстрый и слабый. Громила — огромный, медленный, ломает стены, его почти не отбросить.
//   Плевальщик — держится на расстоянии и плюётся ядом (плевок летит дугой — можно отбежать).
// Думает только у хоста: вылезает из пропасти Раскола, идёт по дороге через форт к деревне,
// ломает постройки на пути (ворота, баррикады), нападает на игроков рядом.
// У остальных игроков это «копия», которая просто плавно повторяет то, что присылает хост.
public class Enemy : MonoBehaviour
{
    public int id;
    public float maxHealth = 60f;
    public float speed = 3.2f;
    public float attackRange = 1.9f;
    public float attackCooldown = 1.3f;
    public float playerDamage = 12f;
    public float buildingDamage = 30f;
    public float aggroRange = 18f;   // с какого расстояния замечает игрока
    public float climbSpeed = 2.5f;  // как быстро карабкается по обрыву
    public EnemyKind kind = EnemyKind.Crawler;
    public float bodyScale = 1f;
    public float knockScale = 1f;    // насколько отлетает от ударов
    public float shootRange;         // >0 — умеет плеваться
    public float preferRange = 10f;
    public float shootCooldown = 2.6f;
    float nextShot;

    // Характеристики вида. wave — номер набега (с каждым тварь крепче)
    public void ApplyKind(EnemyKind k, int wave)
    {
        kind = k;
        int w = Mathf.Max(0, wave - 1);
        switch (k)
        {
            case EnemyKind.Runner:
                maxHealth = 30f + 5f * w; speed = 6.5f; playerDamage = 7f; buildingDamage = 10f;
                attackRange = 1.6f; attackCooldown = 0.7f; bodyScale = 0.75f; aggroRange = 28f; break;
            case EnemyKind.Brute:
                maxHealth = 280f + 45f * w; speed = 2.1f; playerDamage = 30f; buildingDamage = 140f;
                attackRange = 2.6f; attackCooldown = 2f; bodyScale = 1.55f; aggroRange = 14f; knockScale = 0.15f; break;
            case EnemyKind.Spitter:
                maxHealth = 45f + 6f * w; speed = 3f; playerDamage = 14f; buildingDamage = 20f;
                attackRange = 1.9f; attackCooldown = 1.4f; bodyScale = 0.95f; aggroRange = 22f; shootRange = 18f; break;
            default:
                maxHealth = 60f + 10f * w; break;
        }
        Health = maxHealth;
    }

    public float Health { get; set; }
    public float HealthFraction { get; set; } = 1f;
    public bool Simulated { get; private set; }

    CharacterController cc;
    Transform visual;
    float verticalVelocity;
    float nextAttack;
    Vector3 detourDir;
    float detourUntil;
    float lunge;
    float flash;
    Renderer[] renderers;
    Color[] baseColors;
    MaterialPropertyBlock block;
    BuildPiece targetPiece;

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

    public bool Climbing => climbing;

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

    // Отбросить (от удара)
    public void Knock(Vector3 force) { knock += force * knockScale; }

    public void Setup(int enemyId, bool simulated, Transform visualRoot)
    {
        id = enemyId;
        Simulated = simulated;
        visual = visualRoot;
        Health = maxHealth;
        netPos = transform.position;
        netYaw = transform.eulerAngles.y;

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

    // ---------- Мозги (только у хоста) ----------
    public void Simulate(List<EnemyManager.PlayerInfo> players, Vector3 villageCenter)
    {
        // Ближайший живой игрок рядом
        EnemyManager.PlayerInfo target = null;
        float best = aggroRange;
        foreach (var p in players)
        {
            if (p.transform == null || p.dead) continue;
            float d = Vector3.Distance(p.transform.position, transform.position);
            if (d < best) { best = d; target = p; }
        }

        // Лезет из пропасти: только вверх, пока не выберется на край
        if (climbing)
        {
            // Сначала вверх по обрыву, потом (уже наверху) переползаем через край, пока под ногами не будет земли
            bool atTop = transform.position.y >= climbTop;
            Vector3 over = Vector3.zero;
            if (atTop && path != null && path.Length > 0)
            {
                over = path[0] - transform.position; over.y = 0f;
                over = over.sqrMagnitude > 0.01f ? over.normalized : Vector3.back;
                transform.rotation = Quaternion.LookRotation(over);
            }
            else if (atTop) over = Vector3.back;
            cc.Move(((atTop ? Vector3.zero : Vector3.up * climbSpeed) + over * speed + knock) * Time.deltaTime);
            knock = Vector3.MoveTowards(knock, Vector3.zero, 20f * Time.deltaTime);
            if (visual != null) visual.localRotation = Quaternion.Euler(atTop ? -20f : -70f, 0f, 0f);
            if (atTop && Physics.Raycast(transform.position + Vector3.up * 0.5f, Vector3.down, out RaycastHit ground, 1.5f, ~(1 << 2), QueryTriggerInteraction.Ignore)
                && ground.collider.GetComponentInParent<Enemy>() == null)
            {
                climbing = false; // под ногами земля — дальше идёт как обычно
                verticalVelocity = 0f;
                if (visual != null) visual.localRotation = Quaternion.identity;
            }
            HealthFraction = Health / maxHealth;
            return;
        }

        Vector3 goal;
        if (target != null) goal = target.transform.position;
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
        float dist = to.magnitude;
        Vector3 dir = dist > 0.01f ? to / dist : transform.forward;
        if (Time.time < detourUntil) dir = detourDir;

        bool moving = true;
        if (target != null && shootRange > 0f && dist > attackRange + 1.5f && dist < shootRange)
        {
            // Плевальщик: держит расстояние и плюётся
            if (dist < preferRange) moving = false;
            if (Time.time >= nextShot)
            {
                nextShot = Time.time + shootCooldown;
                lunge = 1f;
                EnemyManager.Spit(transform.position + Vector3.up * 1.4f * bodyScale + dir * 0.6f,
                                  target.transform.position + Vector3.up * 0.6f, playerDamage);
            }
        }
        else if (target != null && dist < attackRange)
        {
            moving = false;
            if (Time.time >= nextAttack)
            {
                nextAttack = Time.time + attackCooldown;
                lunge = 1f;
                EnemyManager.HurtPlayer(target.ownerId, playerDamage);
            }
        }
        else if (Physics.SphereCast(transform.position + Vector3.up * 0.9f, 0.4f, dir, out RaycastHit hit, 1.3f, ~(1 << 2), QueryTriggerInteraction.Ignore))
        {
            BuildPiece bp = hit.collider.GetComponentInParent<BuildPiece>();
            Enemy other = hit.collider.GetComponentInParent<Enemy>();
            if (bp != null)
            {
                moving = false;
                if (Time.time >= nextAttack)
                {
                    nextAttack = Time.time + attackCooldown;
                    lunge = 1f;
                    BuildingManager.Damage(bp, buildingDamage);
                }
            }
            else if (other != null && other != this)
                dir = Quaternion.Euler(0f, 45f, 0f) * dir; // обходим своих
            else if (hit.collider.GetComponentInParent<NetworkPlayer>() == null && hit.collider.GetComponentInParent<PlayerMovement>() == null && Time.time >= detourUntil)
            {
                // дерево или камень — обходим
                detourDir = Quaternion.Euler(0f, Random.value < 0.5f ? -70f : 70f, 0f) * dir;
                detourUntil = Time.time + 1.2f;
                dir = detourDir;
            }
        }

        // Застрял (упёрся во что-то не ломаемое) — пробуем в сторону
        if (moving && Time.time >= detourUntil)
        {
            Vector3 moved = transform.position - lastPos; moved.y = 0f;
            stuckTime = moved.magnitude < speed * Time.deltaTime * 0.2f ? stuckTime + Time.deltaTime : 0f;
            if (stuckTime > 1f)
            {
                stuckTime = 0f;
                detourDir = Quaternion.Euler(0f, Random.value < 0.5f ? -90f : 90f, 0f) * dir;
                detourUntil = Time.time + 1.5f;
            }
        }
        lastPos = transform.position;

        if (cc.isGrounded && verticalVelocity < 0f) verticalVelocity = -2f;
        verticalVelocity += -20f * Time.deltaTime;
        Vector3 v = (moving ? dir * speed : Vector3.zero) + Vector3.up * verticalVelocity + knock;
        knock = Vector3.MoveTowards(knock, Vector3.zero, 25f * Time.deltaTime);
        cc.Move(v * Time.deltaTime);

        if (dir.sqrMagnitude > 0.01f && knock.sqrMagnitude < 4f)
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(dir), 8f * Time.deltaTime);

        HealthFraction = Health / maxHealth;
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

    // ---------- Копия у остальных игроков ----------
    public void SetNetState(Vector3 pos, float yaw, float hpFrac, bool climb)
    {
        if (Simulated) return;
        if (visual != null) visual.localRotation = climb ? Quaternion.Euler(-70f, 0f, 0f) : Quaternion.identity;
        netPos = pos;
        netYaw = yaw;
        if (hpFrac < HealthFraction) flash = 1f;
        HealthFraction = hpFrac;
    }

    public void Lunge() { lunge = 1f; }
    public void Flash() { flash = 1f; }

    void Update()
    {
        if (!Simulated)
        {
            transform.position = Vector3.Lerp(transform.position, netPos, 10f * Time.deltaTime);
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.Euler(0f, netYaw, 0f), 10f * Time.deltaTime);
        }

        // Бросок вперёд при атаке
        lunge = Mathf.MoveTowards(lunge, 0f, Time.deltaTime * 3f);
        if (visual != null) visual.localPosition = new Vector3(0f, 0f, Mathf.Sin(lunge * Mathf.PI) * 0.5f);

        // Вспышка при ударе
        if (renderers != null && flash > 0f)
        {
            flash = Mathf.Max(0f, flash - Time.deltaTime * 4f);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null) continue;
                renderers[i].GetPropertyBlock(block);
                block.SetColor("_BaseColor", Color.Lerp(baseColors[i], Color.white, flash));
                renderers[i].SetPropertyBlock(block);
            }
        }
    }

    public void Die()
    {
        Renderer r = renderers != null && renderers.Length > 0 ? renderers[0] : null;
        Material mat = r != null ? r.sharedMaterial : null;
        for (int i = 0; i < 7; i++)
        {
            GameObject d = GameObject.CreatePrimitive(PrimitiveType.Cube);
            d.layer = 2;
            d.transform.position = transform.position + Vector3.up * 0.9f + Random.insideUnitSphere * 0.5f;
            d.transform.localScale = Vector3.one * Random.Range(0.15f, 0.35f);
            d.transform.rotation = Random.rotation;
            if (mat != null) d.GetComponent<Renderer>().sharedMaterial = mat;
            Rigidbody rb = d.AddComponent<Rigidbody>();
            rb.mass = 0.5f;
            rb.AddForce(Random.insideUnitSphere * 4f + Vector3.up * 3f, ForceMode.VelocityChange);
            Destroy(d, Random.Range(4f, 7f));
        }
        Destroy(gameObject);
    }
}
