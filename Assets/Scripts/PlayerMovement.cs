using UnityEngine;
using UnityEngine.InputSystem;

// Движение героя: WASD — ходьба, Shift — бег, Пробел — прыжок.
// Вешается на объект Player вместе с CharacterController.
[RequireComponent(typeof(CharacterController))]
public class PlayerMovement : MonoBehaviour
{
    [Header("Скорость")]
    public float walkSpeed = 4f;     // обычный шаг
    public float sprintSpeed = 7f;   // бег с Shift

    [Header("Прыжок и гравитация")]
    public float jumpHeight = 1.2f;  // высота прыжка в метрах
    public float gravity = -20f;     // сила притяжения вниз

    [Header("Пинать предметы")]
    public float pushPower = 2f;     // с какой силой толкаем обломки и ресурсы

    [Header("Перекат (Ctrl или C)")]
    public float dodgeSpeed = 11f;
    public float dodgeTime = 0.32f;
    public float dodgeCost = 16f;
    public float slideTime = 0.6f;          // подкат: перекат вперёд на бегу
    public float slideSpeed = 13f;
    public bool IsDodging => Time.time < dodgeUntil - 0.05f;
    public bool IsSliding => sliding && Time.time < dodgeUntil;
    public Vector3 SlideDir => dodgeDir;
    public float SlideLow { get; private set; }  // 0..1 — насколько присел в подкате (для камеры)
    bool sliding;
    float dodgeLen;
    float dodgeUntil, nextDodge;
    Vector3 dodgeDir;

    [Header("Выносливость и падение")]
    public float sprintCost = 8f;      // в секунду бега
    public float jumpCost = 7f;
    public float safeFallSpeed = 13.5f; // быстрее — урон
    public float fallDamage = 6f;

    CharacterController controller;
    PlayerHealth health;
    public float DodgeRoll { get; private set; } // 0..1 — для наклона камеры при перекате
    float verticalVelocity;          // скорость вверх/вниз
    Vector3 pushVel;                 // толчок от удара (тварь отбросила)
    float staggerUntil;              // после сильного удара ноги заплетаются — медленнее

    // Удар отбросил героя: vel — скорость толчка, stagger — сколько секунд идти медленно
    public void Push(Vector3 vel, float stagger)
    {
        vel.y = 0f;
        pushVel += vel;
        staggerUntil = Mathf.Max(staggerUntil, Time.time + stagger);
    }

    void Awake()
    {
        controller = GetComponent<CharacterController>();
    }

    // После смерти/возрождения начинаем с нуля (иначе посчитает падение с прошлого места)
    void OnEnable() { verticalVelocity = 0f; }
    public void ResetFall() { verticalVelocity = 0f; }

    void Update()
    {
        Keyboard kb = Keyboard.current;
        if (kb == null) return;

        // 1. Читаем клавиши WASD
        Vector2 input = Vector2.zero;
        if (kb.wKey.isPressed) input.y += 1;
        if (kb.sKey.isPressed) input.y -= 1;
        if (kb.dKey.isPressed) input.x += 1;
        if (kb.aKey.isPressed) input.x -= 1;
        input = Vector2.ClampMagnitude(input, 1f); // чтобы по диагонали не было быстрее

        // 2. Направление — относительно того, куда смотрит герой
        Vector3 move = transform.forward * input.y + transform.right * input.x;
        // Бег тратит выносливость. Кончилась — идёшь шагом. С поднятым блоком — медленно.
        if (health == null) health = GetComponent<PlayerHealth>();
        bool sprint = kb.leftShiftKey.isPressed && input.sqrMagnitude > 0.01f &&
                      (health == null || health.UseStamina(sprintCost * Time.deltaTime, true));
        float speed = sprint ? sprintSpeed : walkSpeed;
        if (health != null && health.IsBlocking) speed *= 0.5f;
        if (Time.time < staggerUntil) speed *= 0.4f;

        // Перекат: рывок в сторону движения (или назад), на миг неуязвим
        if ((kb.leftCtrlKey.wasPressedThisFrame || kb.cKey.wasPressedThisFrame) && Time.time >= nextDodge && controller.isGrounded &&
            (health == null || health.UseStamina(dodgeCost, false)))
        {
            dodgeDir = move.sqrMagnitude > 0.01f ? move.normalized : -transform.forward;
            sliding = input.y > 0.3f;                          // жмёшь вперёд — это подкат, сбивает тварей с ног
            dodgeLen = sliding ? slideTime : dodgeTime;
            dodgeUntil = Time.time + dodgeLen;
            nextDodge = Time.time + dodgeLen + 0.35f;
        }
        if (Time.time < dodgeUntil)
        {
            move = dodgeDir;
            if (sliding)
            {
                float k = 1f - (dodgeUntil - Time.time) / dodgeLen;   // 0..1
                speed = Mathf.Lerp(slideSpeed, walkSpeed * 0.6f, k * k); // разгон, потом тормозит по земле
            }
            else speed = dodgeSpeed;
        }
        DodgeRoll = Time.time < dodgeUntil && !sliding ? 1f - (dodgeUntil - Time.time) / dodgeLen : 0f;
        SlideLow = Mathf.MoveTowards(SlideLow, IsSliding ? 1f : 0f, Time.deltaTime * (IsSliding ? 9f : 4f));

        // 3. Прыжок и гравитация
        if (controller.isGrounded && verticalVelocity < 0)
            verticalVelocity = -2f; // слегка прижимаем к земле

        if (controller.isGrounded && kb.spaceKey.wasPressedThisFrame && (health == null || health.UseStamina(jumpCost, false)))
            verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);

        verticalVelocity += gravity * Time.deltaTime;

        // 4. Двигаем героя
        pushVel = Vector3.Lerp(pushVel, Vector3.zero, 1f - Mathf.Exp(-7f * Time.deltaTime));
        Vector3 velocity = move * speed + pushVel + Vector3.up * verticalVelocity;
        bool wasGrounded = controller.isGrounded;
        float fallSpeed = -verticalVelocity;
        controller.Move(velocity * Time.deltaTime);

        // 5. Урон от падения: с высоты больше ~4.5 м больно, с 12 м — почти смерть
        if (!wasGrounded && controller.isGrounded && fallSpeed > safeFallSpeed && health != null)
            health.TakeDamage((fallSpeed - safeFallSpeed) * fallDamage, "Разбился при падении", false);
    }

    // Unity вызывает это, когда герой во что-то врезается при движении.
    // Если это предмет с физикой (обломок, ресурс) — толкаем его.
    void OnControllerColliderHit(ControllerColliderHit hit)
    {
        Rigidbody rb = hit.collider.attachedRigidbody;
        if (rb == null || rb.isKinematic) return;
        if (hit.moveDirection.y < -0.3f) return; // стоим сверху — не толкаем
        Vector3 dir = new Vector3(hit.moveDirection.x, 0.2f, hit.moveDirection.z);
        rb.AddForceAtPosition(dir * pushPower, hit.point, ForceMode.Impulse);
    }
}
