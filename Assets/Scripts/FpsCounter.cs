using UnityEngine;
using UnityEngine.InputSystem;

// Показывает FPS (кадров в секунду) в левом верхнем углу. F3 — спрятать/показать.
// 60 и больше — плавно. Меньше 30 — будут рывки.
public class FpsCounter : MonoBehaviour
{
    public bool show = true;
    float smoothedDelta = 0.016f;
    GUIStyle style;

    void Update()
    {
        smoothedDelta = Mathf.Lerp(smoothedDelta, Time.unscaledDeltaTime, 0.05f);
        if (Keyboard.current != null && Keyboard.current.f3Key.wasPressedThisFrame) show = !show;
    }

    void OnGUI()
    {
        if (!show) return;
        if (style == null)
        {
            style = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
            style.normal.textColor = Color.yellow;
        }
        float fps = 1f / smoothedDelta;
        GUI.Label(new Rect(12, 8, 300, 40), "FPS: " + Mathf.RoundToInt(fps), style);
    }
}
