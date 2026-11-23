using UnityEngine;
using UnityEngine.InputSystem;

// Меч: ЛКМ — взмах. Свои постройки меч НЕ ломает (для этого молот).
// Зато толкает обломки и ресурсы. Урон по врагам добавим, когда появятся враги.
// Ещё рисует прицел.
public class PlayerCombat : MonoBehaviour
{
    public float range = 3f;
    public float cooldown = 0.45f;
    public float pushForce = 6f;

    Camera cam;
    PlayerHotbar hotbar;
    float nextHitTime;
    float swing;
    Transform swingModel;
    Quaternion restRotation;

    public void PlaySwing()
    {
        swing = 1f;
    }

    void Start()
    {
        cam = Camera.main;
        hotbar = GetComponent<PlayerHotbar>();
    }

    void Update()
    {
        Transform model = hotbar != null ? hotbar.CurrentModel : null;
        if (model != swingModel)
        {
            if (swingModel != null) swingModel.localRotation = restRotation;
            swingModel = model;
            if (swingModel != null) restRotation = swingModel.localRotation;
        }

        bool swordInHand = hotbar == null || hotbar.Current == Tool.Sword;
        Mouse mouse = Mouse.current;
        if (swordInHand && mouse != null && Cursor.lockState == CursorLockMode.Locked &&
            mouse.leftButton.wasPressedThisFrame && Time.time >= nextHitTime)
        {
            nextHitTime = Time.time + cooldown;
            PlaySwing();
            PushThings();
        }

        if (swingModel != null)
        {
            swing = Mathf.MoveTowards(swing, 0f, Time.deltaTime / cooldown);
            float a = Mathf.Sin(swing * Mathf.PI) * 80f;
            swingModel.localRotation = restRotation * Quaternion.Euler(a, -a * 0.4f, 0f);
        }
    }

    // Толкнуть обломки и ресурсы перед собой
    void PushThings()
    {
        if (cam == null) return;
        Vector3 center = transform.position + Vector3.up * 0.6f + cam.transform.forward * (range * 0.6f);
        foreach (Collider c in Physics.OverlapSphere(center, range * 0.6f, ~0, QueryTriggerInteraction.Ignore))
        {
            Rigidbody rb = c.attachedRigidbody;
            if (rb == null || rb.isKinematic) continue;
            rb.AddForce((cam.transform.forward + Vector3.up * 0.4f) * pushForce, ForceMode.VelocityChange);
        }
    }

    void OnGUI()
    {
        GUI.Label(new Rect(Screen.width / 2f - 6, Screen.height / 2f - 12, 20, 24), "+", Hud.Big);
    }
}

// Общие помощники: куда смотрит игрок
public static class AimHelper
{
    public static bool Aim(Camera cam, Transform self, float reach, out RaycastHit result)
    {
        result = default;
        if (cam == null) return false;
        // В виде от 3-го лица камера дальше — добавляем это расстояние
        Vector3 head = cam.transform.parent != null ? cam.transform.parent.position : cam.transform.position;
        float extra = Vector3.Distance(cam.transform.position, head);
        Ray ray = new Ray(cam.transform.position, cam.transform.forward);
        RaycastHit[] hits = Physics.RaycastAll(ray, reach + extra, ~(1 << 2), QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        bool found = false;
        foreach (RaycastHit h in hits)
        {
            if (h.transform.root == self.root) continue;
            if (h.distance < best) { best = h.distance; result = h; found = true; }
        }
        return found;
    }

    public static BuildPiece LookedPiece(Camera cam, Transform self, float reach)
    {
        return Aim(cam, self, reach, out RaycastHit h) ? h.collider.GetComponentInParent<BuildPiece>() : null;
    }
}

// Общие помощники для надписей на экране
public static class Hud
{
    static GUIStyle big, small;
    public static GUIStyle Big => big ??= new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
    public static GUIStyle Small => small ??= new GUIStyle(GUI.skin.label) { fontSize = 16, normal = { textColor = Color.white } };

    public static void Bar(Rect r, float fill, Color color, string text)
    {
        GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, false, 0, new Color(0, 0, 0, 0.6f), 0, 0);
        Rect f = new Rect(r.x, r.y, r.width * Mathf.Clamp01(fill), r.height);
        GUI.DrawTexture(f, Texture2D.whiteTexture, ScaleMode.StretchToFill, false, 0, color, 0, 0);
        if (!string.IsNullOrEmpty(text)) GUI.Label(new Rect(r.x, r.y - 24, r.width + 300, 24), text, Small);
    }
}
