using UnityEngine;
using UnityEngine.InputSystem;

public enum Tool { Sword, Hammer, Axe, Pickaxe, Bow, Staff, Blaster }

// Панель предметов внизу экрана: 1 — меч, 2 — молот, 3 — топор, 4 — кирка, 5 — лук, 6 — посох, 7 — бластер.
// Показывает в руке нужную модель.
public class PlayerHotbar : MonoBehaviour
{
    public Transform swordModel;
    public Transform hammerModel;
    public Transform axeModel;
    public Transform pickaxeModel;
    public Transform bowModel;
    public Transform staffModel;
    public Transform blasterModel;

    static readonly string[] names = { "Меч", "Молот", "Топор", "Кирка", "Лук", "Посох", "Бластер" };

    public Tool Current { get; private set; } = Tool.Sword;

    void Start() { ShowTool(Current); }

    void Update()
    {
        Keyboard kb = Keyboard.current;
        if (kb == null) return;
        if (kb.digit1Key.wasPressedThisFrame) Select(Tool.Sword);
        if (kb.digit2Key.wasPressedThisFrame) Select(Tool.Hammer);
        if (kb.digit3Key.wasPressedThisFrame) Select(Tool.Axe);
        if (kb.digit4Key.wasPressedThisFrame) Select(Tool.Pickaxe);
        if (kb.digit5Key.wasPressedThisFrame) Select(Tool.Bow);
        if (kb.digit6Key.wasPressedThisFrame) Select(Tool.Staff);
        if (kb.digit7Key.wasPressedThisFrame) Select(Tool.Blaster);
    }

    void Select(Tool t) { Current = t; ShowTool(t); }

    public static string NameOf(Tool t) => names[(int)t];

    Transform ModelOf(Tool t)
    {
        switch (t)
        {
            case Tool.Sword: return swordModel;
            case Tool.Hammer: return hammerModel;
            case Tool.Axe: return axeModel;
            case Tool.Bow: return bowModel;
            case Tool.Staff: return staffModel;
            case Tool.Blaster: return blasterModel;
            default: return pickaxeModel;
        }
    }

    // Показать в руке нужный предмет (вызывается и для чужих игроков по сети)
    public void ShowTool(Tool t)
    {
        for (int i = 0; i < names.Length; i++)
        {
            Transform m = ModelOf((Tool)i);
            if (m != null) m.gameObject.SetActive((Tool)i == t);
        }
    }

    public Transform CurrentModel => ModelOf(Current);

    void OnGUI()
    {
        float w = 110, h = 46, gap = 8;
        int n = names.Length;
        float x = Screen.width / 2f - (w * n + gap * (n - 1)) / 2f;
        float y = Screen.height - h - 14;
        for (int i = 0; i < n; i++)
        {
            Rect r = new Rect(x + i * (w + gap), y, w, h);
            bool on = (int)Current == i;
            Hud.Fill(r, on ? new Color(0.85f, 0.65f, 0.2f, 0.9f) : new Color(0f, 0f, 0f, 0.55f), 6);
            GUI.Label(new Rect(r.x + 10, r.y + 3, 30, 20), (i + 1).ToString(), Hud.Tiny);
            GUI.Label(new Rect(r.x, r.y + 12, w, 30), names[i], Hud.Center);
        }
    }
}
