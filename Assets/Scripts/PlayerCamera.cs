using UnityEngine;
using UnityEngine.InputSystem;

// Камера героя: вид от первого и от третьего лица.
// Вешается на объект CameraPivot ("голова"), который лежит внутри Player.
// Внутри CameraPivot лежит Main Camera.
//
// Управление:
//   мышь            — смотреть (влево-вправо поворачивает героя, вверх-вниз — голову)
//   V               — переключить первое / третье лицо
//   колёсико мыши   — приблизить / отдалить камеру (в третьем лице)
//   Esc             — освободить курсор, клик левой кнопкой — снова захватить
public class PlayerCamera : MonoBehaviour
{
    [Header("Ссылки (можно не заполнять — найдутся сами)")]
    public Transform playerBody;   // объект Player
    public Transform cameraObject; // Main Camera

    [Header("Мышь")]
    public float mouseSensitivity = 0.1f;
    public float minPitch = -85f;  // насколько можно посмотреть вниз
    public float maxPitch = 85f;   // насколько можно посмотреть вверх

    [Header("Переключение вида")]
    public Key toggleKey = Key.V;
    public bool startInThirdPerson = false;

    [Header("Третье лицо")]
    public float distance = 3.5f;       // расстояние от головы до камеры
    public float minDistance = 1.5f;
    public float maxDistance = 8f;
    public float zoomStep = 0.5f;       // на сколько метров за один щелчок колёсика
    public float shoulderOffset = 0.5f; // сдвиг камеры вправо, "из-за плеча"
    public float collisionRadius = 0.2f;// чтобы камера не залезала в стены

    // Когда открыто меню (например, построек), клик мышью не должен захватывать курсор
    public static bool BlockCursorLock;

    // Тряска камеры от сильных ударов: PlayerCamera.Shake(0.5f)
    static float shake;
    public static void Shake(float amount) { shake = Mathf.Max(shake, amount); }

    // Толчок камеры от удара: голову резко дёргает (градусы: x — кивок, y — поворот, z — завал вбок) и она возвращается
    static Vector3 punch, punchVel;
    public static void Punch(Vector3 degrees) { punchVel += degrees * 28f; }

    // Насколько послушна мышь (меньше 1 — «тяжело», например клинок застрял в теле)
    public static float LookScale = 1f;

    float pitch;            // наклон головы вверх/вниз
    bool thirdPerson;
    float currentDistance;  // текущее расстояние (с учётом стен)
    PlayerMovement movement;

    void Start()
    {
        if (playerBody == null) playerBody = transform.parent;
        if (cameraObject == null)
        {
            Camera cam = GetComponentInChildren<Camera>();
            if (cam != null) cameraObject = cam.transform;
        }
        thirdPerson = startInThirdPerson;
        currentDistance = distance;
        LockCursor(true);
    }

    void Update()
    {
        Mouse mouse = Mouse.current;
        Keyboard kb = Keyboard.current;

        // Курсор
        if (kb != null && kb.escapeKey.wasPressedThisFrame) LockCursor(false);
        if (mouse != null && mouse.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked && !BlockCursorLock)
            LockCursor(true);

        // Переключение вида
        if (kb != null && kb[toggleKey].wasPressedThisFrame) thirdPerson = !thirdPerson;

        if (mouse == null || Cursor.lockState != CursorLockMode.Locked) return;

        // Взгляд мышкой
        Vector2 delta = mouse.delta.ReadValue() * mouseSensitivity * LookScale;

        pitch -= delta.y;
        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
        transform.localRotation = Quaternion.Euler(pitch, 0f, 0f); // голова вверх-вниз

        if (playerBody != null) playerBody.Rotate(Vector3.up * delta.x); // тело влево-вправо

        // Колёсико — приблизить/отдалить (только в третьем лице)
        float scroll = mouse.scroll.ReadValue().y;
        bool building = PlayerBuilder.Local != null && PlayerBuilder.Local.IsBuilding; // с молотом колёсико листает детали
        if (thirdPerson && !building && Mathf.Abs(scroll) > 0.01f)
            distance = Mathf.Clamp(distance - Mathf.Sign(scroll) * zoomStep, minDistance, maxDistance);
    }

    // LateUpdate идёт после всех Update — к этому моменту герой уже сдвинулся
    void LateUpdate()
    {
        if (cameraObject == null) return;

        if (!thirdPerson)
        {
            // Первое лицо: камера прямо в голове
            // при перекате голова «ныряет» вниз
            if (movement == null && playerBody != null) movement = playerBody.GetComponent<PlayerMovement>();
            float roll = movement != null ? movement.DodgeRoll : 0f;
            float low = movement != null ? movement.SlideLow : 0f;
            float lowS = low * low * (3f - 2f * low);
            cameraObject.localPosition = Vector3.down * (Mathf.Sin(roll * Mathf.PI) * 0.6f + lowS * 0.75f);
            cameraObject.localRotation = ShakeRotation() * Quaternion.Euler(-4f * lowS, 0f, -6f * lowS); // в подкате голова низко и чуть набок
            return;
        }

        // Третье лицо: камера сзади и чуть правее головы
        Vector3 wantedPos = transform.TransformPoint(new Vector3(shoulderOffset, 0f, -distance));
        Vector3 toCamera = wantedPos - transform.position;
        float wantedDist = toCamera.magnitude;
        Vector3 dir = toCamera / wantedDist;

        // Если между головой и камерой стена — придвигаем камеру ближе
        float safeDist = wantedDist;
        RaycastHit[] hits = Physics.SphereCastAll(transform.position, collisionRadius, dir, wantedDist,
                                                  ~0, QueryTriggerInteraction.Ignore);
        foreach (RaycastHit hit in hits)
        {
            if (playerBody != null && hit.transform.root == playerBody.root) continue; // себя не считаем
            if (hit.distance < safeDist) safeDist = hit.distance;
        }

        // К стене — сразу, от стены — плавно
        if (safeDist < currentDistance) currentDistance = safeDist;
        else currentDistance = Mathf.Lerp(currentDistance, safeDist, 8f * Time.deltaTime);

        cameraObject.position = transform.position + dir * currentDistance;
        cameraObject.localRotation = ShakeRotation();
    }

    Quaternion ShakeRotation()
    {
        // пружина толчка: резко отклоняется, плавно возвращается
        float udt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
        punchVel += (-punch * 220f - punchVel * 22f) * udt;
        punch += punchVel * udt;
        Quaternion pq = Quaternion.Euler(punch);
        if (shake <= 0.001f) return pq;
        float t = Time.unscaledTime * 35f;
        Quaternion q = Quaternion.Euler((Mathf.PerlinNoise(t, 0f) - 0.5f) * 6f * shake,
                                        (Mathf.PerlinNoise(0f, t) - 0.5f) * 6f * shake,
                                        (Mathf.PerlinNoise(t, t) - 0.5f) * 4f * shake);
        shake = Mathf.MoveTowards(shake, 0f, Time.unscaledDeltaTime * 3f);
        return pq * q;
    }

    void LockCursor(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }
}
