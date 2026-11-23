using UnityEngine;
using UnityEngine.InputSystem;

public enum Tool { Sword, Hammer }

// Панель предметов внизу экрана: 1 — меч, 2 — молот.
// Показывает в руке нужную модель.
public class PlayerHotbar : MonoBehaviour
{
    public Transform swordModel;
    public Transform hammerModel;

    public Tool Current { get; private set; } = Tool.Sword;

    void Start() { Apply(); }

    void Update()
    {
        Keyboard kb = Keyboard.current;
        if (kb == null) return;
        if (kb.digit1Key.wasPressedThisFrame) { Current = Tool.Sword; Apply(); }
        if (kb.digit2Key.wasPressedThisFrame) { Current = Tool.Hammer; Apply(); }
    }

    void Apply()
    {
        if (swordModel != null) swordModel.gameObject.SetActive(Current == Tool.Sword);
        if (hammerModel != null) hammerModel.gameObject.SetActive(Current == Tool.Hammer);
    }

    // Текущая модель в руке (для анимации взмаха)
    public Transform CurrentModel => Current == Tool.Sword ? swordModel : hammerModel;

    void OnGUI()
    {
        string[] names = { "1  Меч", "2  Молот" };
        float w = 130, h = 44, gap = 8;
        float x = Screen.width / 2f - (w * 2 + gap) / 2f;
        float y = Screen.height - h - 14;
        for (int i = 0; i < 2; i++)
        {
            Rect r = new Rect(x + i * (w + gap), y, w, h);
            bool on = (int)Current == i;
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, false, 0,
                            on ? new Color(1f, 0.8f, 0.2f, 0.85f) : new Color(0f, 0f, 0f, 0.55f), 0, 6);
            GUI.Label(new Rect(r.x + 14, r.y + 10, w, h), names[i], Hud.Small);
        }
    }
}
