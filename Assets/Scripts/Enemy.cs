using System.Collections.Generic;
using UnityEngine;

// Тварь с Тёмной половины.
// Думает только у хоста: идёт к деревне, ломает постройки на пути, нападает на игроков рядом.
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
            cc.radius = 0.45f; cc.height = 1.7f; cc.center = new Vector3(0f, 0.85f, 0f);
            cc.stepOffset = 0.4f;
        }
        else
        {
            CapsuleCollider col = gameObject.AddComponent<CapsuleCollider>();
            col.radius = 0.45f; col.height = 1.7f; col.center = new Vector3(0f, 0.85f, 0f);
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

        Vector3 goal;
        if (target != null) goal = target.transform.position;
        else
        {
            // У деревни — выбираем ближайшую постройку и ломаем её
            if (Vector3.Distance(transform.position, villageCenter) < 18f)
            {
                if (targetPiece == null || !targetPiece.gameObject.activeInHierarchy) targetPiece = NearestPiece(20f);
                goal = targetPiece != null ? targetPiece.Center : villageCenter;
            }
            else goal = villageCenter;
        }

        Vector3 to = goal - transform.position; to.y = 0f;
        float dist = to.magnitude;
        Vector3 dir = dist > 0.01f ? to / dist : transform.forward;
        if (Time.time < detourUntil) dir = detourDir;

        bool moving = true;
        if (target != null && dist < attackRange)
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

        if (cc.isGrounded && verticalVelocity < 0f) verticalVelocity = -2f;
        verticalVelocity += -20f * Time.deltaTime;
        Vector3 v = (moving ? dir * speed : Vector3.zero) + Vector3.up * verticalVelocity;
        cc.Move(v * Time.deltaTime);

        if (dir.sqrMagnitude > 0.01f)
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
            if (d < bestD) { bestD = d; best = p; }
        }
        return best;
    }

    // ---------- Копия у остальных игроков ----------
    public void SetNetState(Vector3 pos, float yaw, float hpFrac)
    {
        if (Simulated) return;
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
