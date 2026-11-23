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

    CharacterController controller;
    float verticalVelocity;          // скорость вверх/вниз

    void Awake()
    {
        controller = GetComponent<CharacterController>();
    }

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
        float speed = kb.leftShiftKey.isPressed ? sprintSpeed : walkSpeed;

        // 3. Прыжок и гравитация
        if (controller.isGrounded && verticalVelocity < 0)
            verticalVelocity = -2f; // слегка прижимаем к земле

        if (controller.isGrounded && kb.spaceKey.wasPressedThisFrame)
            verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);

        verticalVelocity += gravity * Time.deltaTime;

        // 4. Двигаем героя
        Vector3 velocity = move * speed + Vector3.up * verticalVelocity;
        controller.Move(velocity * Time.deltaTime);
    }
}
