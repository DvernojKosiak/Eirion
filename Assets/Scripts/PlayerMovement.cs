using UnityEngine;
using UnityEngine.InputSystem;

// Движение героя: WASD — ходьба, Shift — бег, Пробел — прыжок.
// Ctrl/C на земле — перекат (вперёд на бегу — подкат), в воздухе — рывок.
// Пробел у уступа — залезть. Прыгнул на стену и держишь W — карабкаешься (тратит выносливость).
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

    [Header("Прыжок: удобство")]
    public float coyoteTime = 0.12f;       // сорвался с края — ещё успеваешь прыгнуть
    public float jumpBuffer = 0.12f;       // нажал пробел чуть раньше приземления — прыжок всё равно будет
    public float airControl = 12f;         // насколько можно рулить в воздухе

    [Header("Рывок в воздухе (Ctrl/C)")]
    public float airDashSpeed = 13f;
    public float airDashTime = 0.18f;
    public float airDashCost = 14f;

    [Header("Лазание")]
    public float ledgeReach = 2.4f;        // до какой высоты уступа над ногами достаёшь руками
    public float climbSpeed = 2.2f;
    public float climbCost = 9f;           // выносливости в секунду

    public bool IsClimbing => climbing;
    public bool IsMantling => mantling;
    public float ClimbPhase { get; private set; }   // для рук и ног (картинка)
    public float MantleP { get; private set; }       // 0..1 — как далеко залез на уступ
    public float CarrySlow { get; set; } = 1f;       // несёшь тяжёлое — медленнее

    bool climbing, mantling;
    Vector3 wallNormal;
    float mantleStart, mantleDur;
    Vector3 mantleFrom, mantleUp, mantleTo;
    float lastGrounded = -10f, jumpPressedAt = -10f, airDashUntil;
    bool airDashUsed;
    Vector3 airDashDir;

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
    Vector3 hVel;                    // скорость по земле — разгоняется и тормозит, а не дёргается
    public float HorizontalSpeed => hVel.magnitude;
    public bool Grounded => controller != null && controller.isGrounded;
    public float LandKick { get; set; }  // приземлился — насколько сильно (для камеры)
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
    void OnDisable()
    {
        // умер посреди залезания — вернуть тело в обычный режим
        if (mantling && controller != null) controller.enabled = true;
        mantling = false; climbing = false;
    }
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

        // 3. Лазание и залезание на уступ — своё движение, обычное в это время не работает
        bool grounded = controller.isGrounded;
        if (grounded) { lastGrounded = Time.time; airDashUsed = false; }
        if (kb.spaceKey.wasPressedThisFrame) jumpPressedAt = Time.time;
        if (mantling) { UpdateMantle(); return; }
        if (climbing) { UpdateClimb(kb, input); return; }

        // пробел у уступа (по грудь — по макушку с вытянутыми руками) — залезаю, а не прыгаю
        if (kb.spaceKey.wasPressedThisFrame && !IsSliding && FindLedge(out Vector3 ledge, 0.6f) && StartMantle(ledge)) return;
        // в воздухе руки дотянулись до края — хватаюсь и залезаю
        if (!grounded && input.y > 0.3f && verticalVelocity < 4f && FindLedge(out Vector3 ledge2, 0.3f) && StartMantle(ledge2)) return;
        // прыгнул на стену, жму вперёд — начинаю карабкаться
        if (!grounded && input.y > 0.5f && Time.time - lastGrounded > 0.15f && WallAhead(transform.forward, out RaycastHit wh)
            && (health == null || health.Stamina > 5f))
        {
            climbing = true;
            wallNormal = wh.normal; wallNormal.y = 0f; wallNormal.Normalize();
            verticalVelocity = 0f; hVel = Vector3.zero; pushVel = Vector3.zero;
            return;
        }

        // 4. Прыжок и гравитация
        if (grounded && verticalVelocity < 0)
            verticalVelocity = -2f; // слегка прижимаем к земле

        bool canJump = Time.time - lastGrounded <= coyoteTime && verticalVelocity <= 0.5f;   // «койот»: только что сошёл с края — ещё можно
        bool wantJump = Time.time - jumpPressedAt <= jumpBuffer;                          // нажал заранее — прыгну при касании
        if (canJump && wantJump && (health == null || health.UseStamina(jumpCost, false)))
        {
            verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
            jumpPressedAt = -10f; lastGrounded = -10f;
        }
        // отпустил пробел на взлёте — прыжок ниже (короткое нажатие — маленький прыжок)
        if (!grounded && verticalVelocity > 0f && !kb.spaceKey.isPressed) verticalVelocity *= Mathf.Exp(-12f * Time.deltaTime);

        // рывок в воздухе: резко в сторону движения, на миг зависает
        if (!grounded && !airDashUsed && (kb.leftCtrlKey.wasPressedThisFrame || kb.cKey.wasPressedThisFrame) && Time.time - lastGrounded > 0.08f
            && (health == null || health.UseStamina(airDashCost, false)))
        {
            airDashUsed = true;
            airDashDir = move.sqrMagnitude > 0.01f ? move.normalized : transform.forward;
            airDashUntil = Time.time + airDashTime;
            PlayerCamera.Punch(new Vector3(-2f, 0f, 0f));
            CombatFx.Play(CombatFx.Snd.Whoosh, transform.position, 0.6f, 1.3f);
        }
        bool airDashing = Time.time < airDashUntil;
        if (airDashing) { hVel = airDashDir * airDashSpeed; verticalVelocity = Mathf.Max(verticalVelocity, 0f); }
        else verticalVelocity += gravity * Time.deltaTime;
        speed *= CarrySlow;

        // 4. Двигаем героя
        pushVel = Vector3.Lerp(pushVel, Vector3.zero, 1f - Mathf.Exp(-7f * Time.deltaTime));
        Vector3 wantH = move * speed;
        bool rushing = Time.time < dodgeUntil;
        float accelRate = rushing ? 200f : controller.isGrounded ? (wantH.sqrMagnitude > hVel.sqrMagnitude ? 30f : 24f) : airControl;
        if (!airDashing) hVel = Vector3.MoveTowards(hVel, wantH, accelRate * Time.deltaTime);
        Vector3 velocity = hVel + pushVel + Vector3.up * verticalVelocity;
        bool wasGrounded = controller.isGrounded;
        float fallSpeed = -verticalVelocity;
        controller.Move(velocity * Time.deltaTime);

        // 5. Урон от падения: с высоты больше ~4.5 м больно, с 12 м — почти смерть
        if (!wasGrounded && controller.isGrounded && fallSpeed > 3f) LandKick = Mathf.Clamp01(fallSpeed / 14f);
        if (!wasGrounded && controller.isGrounded && fallSpeed > safeFallSpeed && health != null)
            health.TakeDamage((fallSpeed - safeFallSpeed) * fallDamage, "Разбился при падении", false);
    }

    // ------------------------------------------------------------------ лазание

    float HalfHeight => controller.height * 0.5f;
    float FeetY => transform.position.y + controller.center.y - HalfHeight;

    // Можно ли за это цепляться: не тварь, не игрок, не катающийся предмет
    static bool Solid(Collider c)
    {
        if (c == null || c.isTrigger) return false;
        if (c.GetComponentInParent<Enemy>() != null || c.GetComponentInParent<PlayerMovement>() != null) return false;
        Rigidbody rb = c.attachedRigidbody;
        return rb == null || rb.isKinematic;
    }

    // Стена прямо передо мной на уровне груди
    bool WallAhead(Vector3 dir, out RaycastHit hit)
    {
        dir.y = 0f; dir.Normalize();
        Vector3 chest = new Vector3(transform.position.x, FeetY + 1.3f, transform.position.z);
        if (Physics.Raycast(chest, dir, out hit, controller.radius + 0.45f, ~(1 << 2), QueryTriggerInteraction.Ignore) && Solid(hit.collider)
            && Mathf.Abs(hit.normal.y) < 0.35f) return true;
        return false;
    }

    // Уступ впереди: верх стены, на который можно встать, на высоте от minH до ledgeReach над ногами
    bool FindLedge(out Vector3 top, float minH)
    {
        top = Vector3.zero;
        Vector3 fwd = transform.forward; fwd.y = 0f; fwd.Normalize();
        // есть ли перед ногами-грудью препятствие (без него нечего залезать)
        Vector3 low = new Vector3(transform.position.x, FeetY + Mathf.Max(0.35f, minH - 0.1f), transform.position.z);
        if (!Physics.Raycast(low, fwd, out RaycastHit front, controller.radius + 0.6f, ~(1 << 2), QueryTriggerInteraction.Ignore) || !Solid(front.collider)) return false;
        // сверху вниз, чуть за краем
        Vector3 from = front.point + fwd * 0.3f; from.y = FeetY + ledgeReach + 0.3f;
        if (!Physics.Raycast(from, Vector3.down, out RaycastHit down, ledgeReach + 0.3f - minH + 0.05f, ~(1 << 2), QueryTriggerInteraction.Ignore)) return false;
        if (down.normal.y < 0.7f || !Solid(down.collider)) return false;
        float h = down.point.y - FeetY;
        if (h < minH || h > ledgeReach) return false;
        // помещаюсь ли наверху
        Vector3 c = down.point + Vector3.up * (HalfHeight + 0.05f);
        float r = controller.radius * 0.9f;
        if (Physics.CheckCapsule(c + Vector3.up * (HalfHeight - r), c - Vector3.up * (HalfHeight - r - 0.02f), r, ~(1 << 2), QueryTriggerInteraction.Ignore)) return false;
        // и над головой сейчас ничего, что не даст подняться
        top = down.point;
        return true;
    }

    bool StartMantle(Vector3 top)
    {
        float h = top.y - FeetY;
        if (health != null && !health.UseStamina(h > 1.3f ? 10f : 5f, false)) return false;
        mantling = true;
        climbing = false;
        mantleStart = Time.time;
        mantleDur = 0.22f + 0.16f * h;               // выше — дольше подтягивается
        mantleFrom = transform.position;
        Vector3 fwd = transform.forward; fwd.y = 0f; fwd.Normalize();
        mantleTo = top + Vector3.up * (HalfHeight - controller.center.y + 0.04f) + fwd * 0.15f;
        mantleUp = new Vector3(mantleFrom.x, mantleTo.y + 0.05f, mantleFrom.z);
        verticalVelocity = 0f; hVel = Vector3.zero; pushVel = Vector3.zero;
        controller.enabled = false;
        CombatFx.Play(CombatFx.Snd.Thunk, top, 0.35f, 1.4f);
        return true;
    }

    void UpdateMantle()
    {
        float u = Mathf.Clamp01((Time.time - mantleStart) / mantleDur);
        MantleP = u;
        // сначала тело идёт вверх (подтягивается на руках), потом — вперёд на уступ
        Vector3 p = u < 0.6f ? Vector3.Lerp(mantleFrom, mantleUp, Ease(u / 0.6f)) : Vector3.Lerp(mantleUp, mantleTo, Ease((u - 0.6f) / 0.4f));
        transform.position = p;
        PlayerCamera.Sway(new Vector3(u < 0.6f ? 3f : -2f, 0f, 0f));
        if (u >= 1f)
        {
            mantling = false;
            MantleP = 0f;
            controller.enabled = true;
            verticalVelocity = -2f;
            lastGrounded = Time.time;
            LandKick = 0.25f;
        }
    }

    static float Ease(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }

    void UpdateClimb(Keyboard kb, Vector2 input)
    {
        float dt = Time.deltaTime;
        Vector3 into = -wallNormal;
        bool tired = health != null && !health.UseStamina(climbCost * dt * (input.sqrMagnitude > 0.01f ? 1f : 0.4f), true);
        // отпрыгнуть от стены / отпустить / кончились силы
        if (kb.spaceKey.wasPressedThisFrame && (health == null || health.UseStamina(jumpCost, false)))
        {
            climbing = false;
            hVel = wallNormal * 5f;
            verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity) * 0.9f;
            return;
        }
        if (tired || kb.leftCtrlKey.wasPressedThisFrame || kb.cKey.wasPressedThisFrame) { climbing = false; hVel = wallNormal * 1.5f; verticalVelocity = 0f; return; }

        // стена всё ещё передо мной?
        if (!WallAhead(into, out RaycastHit wh))
        {
            // руки дошли до верха — залезаю на уступ
            if (FindLedgeDir(into, out Vector3 top) && StartMantle(top)) return;
            climbing = false;
            return;
        }
        Vector3 n = wh.normal; n.y = 0f;
        if (n.sqrMagnitude > 0.01f) wallNormal = Vector3.Slerp(wallNormal, n.normalized, 10f * dt).normalized;
        if (controller.isGrounded && input.y < -0.1f) { climbing = false; return; }   // спустился до земли

        Vector3 side = Vector3.Cross(Vector3.up, wallNormal);   // вдоль стены
        Vector3 v = Vector3.up * input.y * climbSpeed + side * -input.x * climbSpeed * 0.7f;
        // прижимаюсь к стене: держу расстояние, а не отъезжаю
        float gap = wh.distance - controller.radius - 0.05f;
        v += into * Mathf.Clamp(gap * 8f, -2f, 2f);
        if (v.sqrMagnitude > 0.01f) ClimbPhase += dt * 6f * Mathf.Clamp01(Mathf.Abs(input.y) + Mathf.Abs(input.x));
        controller.Move(v * dt);
        verticalVelocity = 0f;
        hVel = Vector3.zero;
    }

    // То же, что FindLedge, но вперёд — в стену (когда карабкаюсь, смотреть можно куда угодно)
    bool FindLedgeDir(Vector3 dir, out Vector3 top)
    {
        Quaternion was = transform.rotation;
        transform.rotation = Quaternion.LookRotation(dir);
        bool ok = FindLedge(out top, 0.3f);
        transform.rotation = was;
        return ok;
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
