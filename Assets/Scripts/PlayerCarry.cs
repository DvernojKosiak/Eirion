using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Поднять и бросить предмет (камень, бочку, ящик).
//   E, глядя на предмет, — поднять двумя руками. Оружие на это время убирается.
//   ЛКМ — бросить. Держишь ЛКМ — замахиваешься сильнее (до полсекунды).
//   E ещё раз — положить.
// Тяжёлое несёшь медленнее и бросаешь ближе, зато оно сбивает тварь с ног.
public class PlayerCarry : MonoBehaviour
{
    public static PlayerCarry Local { get; private set; }
    public static bool Busy => Local != null && Local.carried != null;   // руки заняты предметом

    public float reach = 2.8f;
    public float maxCharge = 0.5f;

    Camera cam;
    CharacterController controller;
    PlayerHealth health;
    PlayerCombat combat;
    PlayerHotbar hotbar;
    PlayerMovement movement;

    Throwable carried, looked;
    Collider[] carriedCols;
    bool charging;
    float chargeStart, throwKick;
    Transform hiddenModel;
    struct Ignore { public Collider a; public Collider b; public float until; }
    readonly List<Ignore> ignores = new List<Ignore>();

    void OnEnable() { Local = this; }
    void OnDisable() { if (Local == this) Local = null; Drop(Vector3.zero); }

    void Start()
    {
        cam = Camera.main;
        controller = GetComponent<CharacterController>();
        health = GetComponent<PlayerHealth>();
        combat = GetComponent<PlayerCombat>();
        hotbar = GetComponent<PlayerHotbar>();
        movement = GetComponent<PlayerMovement>();
    }

    void Update()
    {
        // брошенный предмет снова сталкивается с героем через долю секунды (а не застревает в нём при броске)
        for (int i = ignores.Count - 1; i >= 0; i--)
            if (Time.time >= ignores[i].until)
            {
                if (ignores[i].a != null && ignores[i].b != null) Physics.IgnoreCollision(ignores[i].a, ignores[i].b, false);
                ignores.RemoveAt(i);
            }

        if (cam == null) cam = Camera.main;
        Keyboard kb = Keyboard.current;
        Mouse mouse = Mouse.current;
        bool locked = Cursor.lockState == CursorLockMode.Locked;
        bool dead = health != null && health.IsDead;
        bool climbing = movement != null && (movement.IsClimbing || movement.IsMantling);

        if (carried == null && carriedCols != null) EndCarry();   // предмет пропал (сломался)
        if (carried != null && (dead || climbing)) { Drop(Vector3.zero); return; }

        looked = null;
        if (carried == null && locked && !dead && !climbing && cam != null && (combat == null || !combat.IsHolding))
        {
            looked = Aim();
            if (looked != null && kb != null && kb.eKey.wasPressedThisFrame) PickUp(looked);
        }
        else if (carried != null && kb != null && mouse != null && locked)
        {
            if (kb.eKey.wasPressedThisFrame) { Drop(controller != null ? controller.velocity : Vector3.zero); return; }
            if (mouse.leftButton.wasPressedThisFrame) { charging = true; chargeStart = Time.time; }
            if (charging && !mouse.leftButton.isPressed) { charging = false; Throw(); return; }
        }

        if (carried != null) Hold();
        throwKick = Mathf.MoveTowards(throwKick, 0f, Time.deltaTime * 4f);
    }

    Throwable Aim()
    {
        Ray r = new Ray(cam.transform.position, cam.transform.forward);
        if (!Physics.Raycast(r, out RaycastHit h, reach + 3f, ~(1 << 2), QueryTriggerInteraction.Ignore)) return null;
        Throwable t = h.collider.GetComponentInParent<Throwable>();
        if (t == null || t.Carried || t.Body == null) return null;
        if (Vector3.Distance(transform.position, h.point) > reach) return null;
        return t;
    }

    void PickUp(Throwable t)
    {
        carried = t;
        t.Carried = true;
        t.Body.isKinematic = true;
        t.Body.interpolation = RigidbodyInterpolation.None;   // двигаем сами каждый кадр
        carriedCols = t.GetComponentsInChildren<Collider>();
        foreach (Collider c in carriedCols) c.enabled = false;  // в руках ни во что не упирается
        CombatFx.Play(CombatFx.Snd.Thunk, t.transform.position, 0.4f, 1.3f);
        SetWeaponHidden(true);
    }

    float Heavy => carried != null ? Mathf.Clamp01((carried.Body.mass - 3f) / 17f) : 0f;   // 0 — камешек, 1 — бочка

    void Hold()
    {
        float c = charging ? Mathf.Clamp01((Time.time - chargeStart) / maxCharge) : 0f;
        float cs = c * c * (3f - 2f * c);
        Vector3 fwd = transform.forward;
        float pitch = cam != null ? Mathf.Clamp(-cam.transform.forward.y, -0.5f, 0.5f) : 0f;
        // перед грудью; тяжёлое — ниже, у живота. Замах — над правым плечом и назад
        Vector3 local = new Vector3(0f, 0.35f - 0.25f * Heavy - pitch * 0.3f, 0.75f);
        local = Vector3.Lerp(local, new Vector3(0.25f, 0.85f, -0.05f), cs);
        local += Vector3.forward * 0.3f * Mathf.Sin(throwKick * Mathf.PI);
        Vector3 want = transform.TransformPoint(local);
        Transform ct = carried.transform;
        ct.position = Vector3.Lerp(ct.position, want, 1f - Mathf.Exp(-18f * Time.deltaTime));
        ct.rotation = Quaternion.Slerp(ct.rotation, Quaternion.LookRotation(fwd), 1f - Mathf.Exp(-8f * Time.deltaTime));
        if (cs > 0f) PlayerCamera.Sway(new Vector3(-3f * cs, 1.5f * cs, -2f * cs));
        if (movement != null) movement.CarrySlow = Mathf.Lerp(1f, 0.6f, Heavy);
        if (hotbar != null && hotbar.CurrentModel != hiddenModel) SetWeaponHidden(true);   // сменил оружие на ходу — и его убрать

        // руки — по бокам предмета
        if (PlayerBody.Local != null)
        {
            float half = 0.12f;
            foreach (Renderer r in carried.GetComponentsInChildren<Renderer>()) half = Mathf.Max(half, r.bounds.extents.x, r.bounds.extents.z);
            half = Mathf.Min(half, 0.35f);
            PlayerBody.Local.CarryL = ct.position - transform.right * half;
            PlayerBody.Local.CarryR = ct.position + transform.right * half;
        }
    }

    void Throw()
    {
        if (carried == null) return;
        float c = Mathf.Clamp01((Time.time - chargeStart) / maxCharge);
        float m = carried.Body.mass;
        if (health != null && !health.UseStamina(5f + m * 0.4f, false)) { charging = false; return; }
        Vector3 dir = cam != null ? cam.transform.forward : transform.forward;
        float speed = Mathf.Lerp(18f, 9f, Heavy) * Mathf.Lerp(0.6f, 1f, c);
        Vector3 v = dir * speed + Vector3.up * Mathf.Lerp(1.5f, 3f, Heavy) + (controller != null ? controller.velocity * 0.5f : Vector3.zero);
        Throwable t = carried;
        Release();
        t.Body.linearVelocity = v;
        t.Body.angularVelocity = Random.insideUnitSphere * 6f;
        t.OnThrown(true);
        throwKick = 1f;
        CombatFx.Play(CombatFx.Snd.Whoosh, transform.position + Vector3.up, 0.8f, Mathf.Lerp(1.2f, 0.7f, Heavy));
        PlayerCamera.Punch(new Vector3(-3f - 3f * Heavy, -2f, 2f));
    }

    void Drop(Vector3 vel)
    {
        if (carried == null) return;
        Throwable t = carried;
        Release();
        t.Body.linearVelocity = vel;
    }

    void Release()
    {
        Throwable t = carried;
        carried = null;
        charging = false;
        t.Carried = false;
        t.Body.isKinematic = false;
        t.Body.interpolation = RigidbodyInterpolation.Interpolate;
        t.Body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        foreach (Collider c in carriedCols)
        {
            if (c == null) continue;
            c.enabled = true;
            if (controller != null) { Physics.IgnoreCollision(c, controller, true); ignores.Add(new Ignore { a = c, b = controller, until = Time.time + 0.4f }); }
        }
        EndCarry();
    }

    void EndCarry()
    {
        carriedCols = null;
        if (movement != null) movement.CarrySlow = 1f;
        if (PlayerBody.Local != null) { PlayerBody.Local.CarryL = null; PlayerBody.Local.CarryR = null; }
        SetWeaponHidden(false);
    }

    // Пока руки заняты предметом — оружие убрано (не видно)
    void SetWeaponHidden(bool hide)
    {
        if (hiddenModel != null) foreach (Renderer r in hiddenModel.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
        hiddenModel = null;
        if (!hide || hotbar == null) return;
        hiddenModel = hotbar.CurrentModel;
        if (hiddenModel != null) foreach (Renderer r in hiddenModel.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
    }

    void OnGUI()
    {
        if (Cursor.lockState != CursorLockMode.Locked) return;
        string text = null;
        if (carried != null) text = "ЛКМ — бросить " + carried.displayName.ToLower() + " (держи — сильнее)   E — положить";
        else if (looked != null) text = "E — поднять: " + looked.displayName.ToLower();
        if (text != null) GUI.Label(new Rect(0, Screen.height - 140, Screen.width, 22), text, Hud.CenterTiny);
    }
}
