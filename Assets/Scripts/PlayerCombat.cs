using UnityEngine;
using UnityEngine.InputSystem;

// ЛКМ — взмах тем, что в руке (меч, топор, кирка).
//   Топор рубит деревья, кирка бьёт камни — выпадают ресурсы.
//   Меч бьёт тварей сильнее всего (топор и кирка — слабее). Свои постройки не ломает.
// Ещё рисует прицел и подсказку, на что смотришь.
public class PlayerCombat : MonoBehaviour
{
    public float range = 3f;
    public float cooldown = 0.45f;
    public float pushForce = 6f;
    public float swordDamage = 25f;
    public float otherToolDamage = 12f;

    Camera cam;
    PlayerHotbar hotbar;
    float nextHitTime;
    float swing;
    Transform swingModel;
    Quaternion restRotation;
    ResourceNode lookedNode;
    Enemy lookedEnemy;
    string message;
    float messageUntil;

    public void PlaySwing() { swing = 1f; }

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

        Tool tool = hotbar != null ? hotbar.Current : Tool.Sword;
        lookedNode = null;
        lookedEnemy = null;
        if (tool != Tool.Hammer && AimHelper.Aim(cam, transform, range, out RaycastHit hit))
        {
            lookedNode = hit.collider.GetComponentInParent<ResourceNode>();
            lookedEnemy = hit.collider.GetComponentInParent<Enemy>();
        }

        Mouse mouse = Mouse.current;
        if (tool != Tool.Hammer && mouse != null && Cursor.lockState == CursorLockMode.Locked &&
            mouse.leftButton.wasPressedThisFrame && Time.time >= nextHitTime)
        {
            nextHitTime = Time.time + cooldown;
            PlaySwing();
            if (lookedEnemy != null)
                EnemyManager.RequestHit(lookedEnemy, tool == Tool.Sword ? swordDamage : otherToolDamage);
            else if (lookedNode != null)
            {
                if (tool == lookedNode.requiredTool) BuildingManager.RequestHitNode(lookedNode);
                else Say("Нужен " + PlayerHotbar.NameOf(lookedNode.requiredTool).ToLower() + " (клавиша " + ((int)lookedNode.requiredTool + 1) + ")");
            }
            else if (tool == Tool.Sword) PushThings();
        }

        if (swingModel != null)
        {
            swing = Mathf.MoveTowards(swing, 0f, Time.deltaTime / cooldown);
            float a = Mathf.Sin(swing * Mathf.PI) * 80f;
            swingModel.localRotation = restRotation * Quaternion.Euler(a, -a * 0.4f, 0f);
        }
    }

    void Say(string text) { message = text; messageUntil = Time.time + 1.5f; }

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

        if (lookedNode != null)
        {
            Rect r = new Rect(Screen.width / 2f - 100, Screen.height / 2f + 40, 200, 10);
            Hud.Bar(r, (float)lookedNode.HitsLeft / lookedNode.maxHits, new Color(0.9f, 0.75f, 0.3f),
                    lookedNode.displayName + "  —  " + PlayerHotbar.NameOf(lookedNode.requiredTool).ToLower());
        }
        if (Time.time < messageUntil)
            GUI.Label(new Rect(Screen.width / 2f - 250, Screen.height / 2f - 70, 500, 30), message, Hud.CenterBig);
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

// Общие помощники для надписей и панелей на экране
public static class Hud
{
    static GUIStyle big, small, tiny, center, centerBig, centerTiny, title;
    public static GUIStyle Big => big ??= new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
    public static GUIStyle Small => small ??= new GUIStyle(GUI.skin.label) { fontSize = 16, normal = { textColor = Color.white } };
    public static GUIStyle Tiny => tiny ??= new GUIStyle(GUI.skin.label) { fontSize = 12, normal = { textColor = new Color(1f, 1f, 1f, 0.7f) } };
    public static GUIStyle Center => center ??= new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
    public static GUIStyle CenterBig => centerBig ??= new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
    public static GUIStyle CenterTiny => centerTiny ??= new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(1f, 1f, 1f, 0.75f) } };
    public static GUIStyle Title => title ??= new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold, wordWrap = true, normal = { textColor = Color.white } };

    public static void Fill(Rect r, Color c, float radius = 0f)
    {
        GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, false, 0, c, 0, radius);
    }

    public static void Bar(Rect r, float fill, Color color, string text)
    {
        Fill(r, new Color(0, 0, 0, 0.6f));
        Fill(new Rect(r.x, r.y, r.width * Mathf.Clamp01(fill), r.height), color);
        if (!string.IsNullOrEmpty(text)) GUI.Label(new Rect(r.x - 150, r.y - 26, r.width + 300, 24), text, Center);
    }
}
