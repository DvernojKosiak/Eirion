using UnityEngine;
using UnityEngine.InputSystem;

// Оружие в руках живое: отстаёт от поворота мышью, покачивается на ходу, вздрагивает при приземлении.
// Вызывай Apply в позе оружия (лук, посох, бластер) — оно добавит покачивание к позиции и повороту.
public static class WeaponSway
{
    static int frame = -1;
    static Vector2 look;               // сглаженное движение мыши
    static float phase, walk;
    static Vector3 swayPos;
    static Quaternion swayRot = Quaternion.identity;

    static void Tick()
    {
        if (frame == Time.frameCount) return;
        frame = Time.frameCount;
        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        Mouse m = Mouse.current;
        Vector2 d = m != null && Cursor.lockState == CursorLockMode.Locked ? m.delta.ReadValue() * PlayerCamera.LookScale : Vector2.zero;
        look = Vector2.Lerp(look, Vector2.ClampMagnitude(d, 40f), 1f - Mathf.Exp(-10f * dt));

        PlayerMovement pm = PlayerHealth.Local != null ? PlayerHealth.Local.GetComponent<PlayerMovement>() : null;
        float speed = pm != null && pm.Grounded ? pm.HorizontalSpeed : 0f;
        walk = Mathf.Lerp(walk, Mathf.Clamp01(speed / 7f), 1f - Mathf.Exp(-6f * dt));
        phase += speed * dt * 1.9f;

        Vector3 bob = new Vector3(Mathf.Sin(phase) * 0.012f, -Mathf.Abs(Mathf.Sin(phase)) * 0.014f, 0f) * walk;
        Vector3 lag = new Vector3(-look.x * 0.0009f, -look.y * 0.0009f, 0f);
        swayPos = bob + lag;
        swayRot = Quaternion.Euler(look.y * 0.12f, -look.x * 0.12f, -look.x * 0.18f + Mathf.Sin(phase) * 1.2f * walk);
    }

    public static void Apply(ref Vector3 pos, ref Quaternion rot, float amount = 1f)
    {
        Tick();
        pos += swayPos * amount;
        rot = Quaternion.SlerpUnclamped(Quaternion.identity, swayRot, amount) * rot;
    }
}
